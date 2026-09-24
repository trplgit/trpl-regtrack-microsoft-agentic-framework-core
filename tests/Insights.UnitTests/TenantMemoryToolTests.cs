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
    public async Task WriteTenantMemoryAsync_ContentOverSizeCap_RejectedBeforeIo()
    {
        var tool = NewTool(["Internal"]);
        var tooLong = new string('x', TenantMemoryTool.MaxSectionChars + 1);

        var result = await tool.WriteTenantMemoryAsync("Internal", tooLong);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Contains("limit", err.GetString());
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
}
