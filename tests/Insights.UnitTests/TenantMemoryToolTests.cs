using System.Text.Json;
using Insights.Agents;
using Insights.Data;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// Pins TenantMemoryTool's own guardrails - dimension scope, size cap, call budget. All three
/// checks run before any blob/Key Vault I/O (mirrors ReadOnlySqlFetchTool's own "Validate runs
/// first" shape), so a deliberately unreachable connection string plus throwing stub
/// encryptor/decryptor are safe to use throughout - if a test here ever DID reach I/O, it would
/// throw loudly, not silently pass.
/// </summary>
public sealed class TenantMemoryToolTests
{
    private const string UnreachableConnectionString = "DefaultEndpointsProtocol=https;AccountName=unreachable-test-host;AccountKey=AAAA;EndpointSuffix=core.windows.net";

    private sealed class ThrowingEncryptor : IReportEncryptor
    {
        public Task<EncryptedReportEnvelope> EncryptAsync(string plaintextHtml, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Should not be called - validation must reject before this point.");
    }

    private sealed class ThrowingDecryptor : IReportDecryptor
    {
        public Task<string> DecryptAsync(byte[] encryptedContent, byte[] encryptedAesKey, string keyVaultObjectVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Should not be called - validation must reject before this point.");
    }

    private static TenantMemoryTool NewTool(IReadOnlyList<string>? allowedDimensions = null) => new(
        new ThrowingEncryptor(), new ThrowingDecryptor(),
        UnreachableConnectionString, "insights-tenant-memory-test",
        tenantId: 29, allowedDimensions: allowedDimensions ?? ["Internal", "Risk"]);

    [Fact]
    public async Task WriteTenantMemoryAsync_DimensionOutsideAllowedSet_RejectedBeforeIo()
    {
        var tool = NewTool(["Internal"]);

        var result = await tool.WriteTenantMemoryAsync("Risk", "some finding");

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Contains("not one of the dimensions", err.GetString());
    }

    [Fact]
    public async Task WriteTenantMemoryAsync_EmptyContent_RejectedBeforeIo()
    {
        var tool = NewTool(["Internal"]);

        var result = await tool.WriteTenantMemoryAsync("Internal", "   ");

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Contains("Empty", err.GetString());
    }

    [Fact]
    public async Task WriteTenantMemoryAsync_ContentOverInputLimit_RejectedBeforeIo()
    {
        var tool = NewTool(["Internal"]);
        var tooLong = new string('x', TenantMemoryTool.MaxInputChars + 1);

        var result = await tool.WriteTenantMemoryAsync("Internal", tooLong);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Contains("limit", err.GetString());
    }

    /// <summary>[2026-09-28] Over the section cap is no longer refused - it is compacted, then stored.</summary>
    [Fact]
    public async Task WriteTenantMemoryAsync_OverSectionCapButCompactable_PassesValidation_AttemptsRealIo()
    {
        var tool = NewTool(["Internal"]);
        var entries = string.Concat(Enumerable.Range(0, 60).Select(i =>
            $"### {new DateOnly(2026, 9, 28).AddMonths(-i):yyyy-MM-dd} (last_30_days)\n- headline {i}\n- detail {new string('d', 80)}\n"));
        Assert.True(entries.Length > TenantMemoryTool.MaxSectionChars);

        var result = await tool.WriteTenantMemoryAsync("Internal", "### Keep\n- baseline\n" + entries);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.StartsWith("Memory write failed", err.GetString());   // reached the (throwing) I/O
    }

    [Fact]
    public async Task WriteTenantMemoryAsync_KeepBlockTooLarge_RejectedBeforeIo_SoTheModelDecides()
    {
        var tool = NewTool(["Internal"]);
        var keep = "### Keep\n" + string.Concat(Enumerable.Range(0, 90).Select(i => $"- must keep {i} {new string('k', 40)}\n"));

        var result = await tool.WriteTenantMemoryAsync("Internal", keep + "### 2026-09-28 (q2)\n- x");

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Contains("Keep", err.GetString());
    }

    [Fact]
    public async Task WriteTenantMemoryAsync_ContentAtExactSizeCap_PassesValidation_AttemptsRealIo()
    {
        var tool = NewTool(["Internal"]);
        var atCap = new string('x', TenantMemoryTool.MaxSectionChars);

        // Validation passed (scope + size both fine) - it goes on to attempt real I/O against the
        // unreachable host/throwing stub, which throws. That thrown exception is caught by the
        // tool's own catch-all and surfaced as an "error" result, never an unhandled exception -
        // but the failure message must NOT be a validation message, proving it got past validation.
        var result = await tool.WriteTenantMemoryAsync("Internal", atCap);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.DoesNotContain("limit", err.GetString());
        Assert.DoesNotContain("not one of the dimensions", err.GetString());
    }

    [Fact]
    public async Task WriteTenantMemoryAsync_BudgetExhaustedAfterMaxCalls_RejectsFurtherCalls()
    {
        var tool = NewTool(["Internal"]);

        for (var i = 0; i < TenantMemoryTool.MaxCallsPerRun; i++)
            await tool.WriteTenantMemoryAsync("Internal", "an entry");

        var result = await tool.WriteTenantMemoryAsync("Internal", "one call too many");

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Contains("Budget exhausted", err.GetString());
    }

    [Fact]
    public async Task ReadSectionsAsync_UnreachableBlobHost_DegradesToEmptySections_NeverThrows()
    {
        var tool = NewTool(["Internal", "Risk"]);

        var sections = await tool.ReadSectionsAsync();

        Assert.Equal("", sections["Internal"]);
        Assert.Equal("", sections["Risk"]);
    }

    private sealed class RecordingSummarizer(string? reply) : ITenantMemorySummarizer
    {
        public string? ReceivedOlderEntries { get; private set; }
        public int ReceivedBudget { get; private set; }
        public Task<string?> SummarizeAsync(string dimensionName, string olderEntries, int maxChars, CancellationToken cancellationToken = default)
        {
            ReceivedOlderEntries = olderEntries;
            ReceivedBudget = maxChars;
            return Task.FromResult(reply);
        }
    }

    private static string LongHistory() =>
        "### Keep\n- Baseline Aug 2026 (last 30 days): 28 of 33 overdue\n" + string.Concat(Enumerable.Range(0, 40).Select(i =>
            $"### {new DateOnly(2026, 9, 28).AddMonths(-i):yyyy-MM-dd} (last_30_days)\n- headline {i}\n- detail {new string('d', 120)}\n"));

    /// <summary>[2026-09-28] Over the limit the summariser gets ONLY the older runs - never Keep, never the two newest.</summary>
    [Fact]
    public async Task WriteTenantMemoryAsync_OverLimit_SummariserSeesOnlyOlderRuns()
    {
        var summarizer = new RecordingSummarizer("### Summary of older runs (2023-06-28 – 2026-07-28)\n- headline 2 .. headline 39");
        var tool = new TenantMemoryTool(new ThrowingEncryptor(), new ThrowingDecryptor(), UnreachableConnectionString, "insights-tenant-memory-test",
            tenantId: 29, allowedDimensions: ["Internal"], summarizer);

        await tool.WriteTenantMemoryAsync("Internal", LongHistory());

        Assert.NotNull(summarizer.ReceivedOlderEntries);
        Assert.Contains("headline 2\n", summarizer.ReceivedOlderEntries);
        Assert.Contains("headline 39", summarizer.ReceivedOlderEntries);
        Assert.DoesNotContain("headline 0\n", summarizer.ReceivedOlderEntries);
        Assert.DoesNotContain("headline 1\n", summarizer.ReceivedOlderEntries);
        Assert.DoesNotContain("Baseline Aug 2026", summarizer.ReceivedOlderEntries);
        Assert.True(summarizer.ReceivedBudget is > 0 and < TenantMemoryTool.MaxSectionChars);
    }

    /// <summary>A bad summary (e.g. one that would start another dimension's section) is never stored - the write still goes ahead via the fallback.</summary>
    [Fact]
    public async Task WriteTenantMemoryAsync_InvalidSummary_FallsBackAndStillWrites()
    {
        var summarizer = new RecordingSummarizer("## Act\n- would overwrite another dimension");
        var tool = new TenantMemoryTool(new ThrowingEncryptor(), new ThrowingDecryptor(), UnreachableConnectionString, "insights-tenant-memory-test",
            tenantId: 29, allowedDimensions: ["Internal"], summarizer);

        var result = await tool.WriteTenantMemoryAsync("Internal", LongHistory());

        using var doc = JsonDocument.Parse(result);
        Assert.StartsWith("Memory write failed", doc.RootElement.GetProperty("error").GetString()); // reached I/O, not refused
    }

    [Fact]
    public void BlobFile_LivesInTheTenantsOwnFolder()
    {
        Assert.Equal("tenant-memory.md.enc", TenantMemoryTool.BlobFileName);
    }
}
