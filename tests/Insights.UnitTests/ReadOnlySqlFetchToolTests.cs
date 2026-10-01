using System.Text.Json;
using Insights.Agents;

namespace Insights.UnitTests;

/// <summary>
/// Pins ReadOnlySqlFetchTool's query-shape validation - the ONLY real backstop on an environment
/// with no DB-enforced read-only login (UAT: sa, full sysadmin - confirmed live 2026-09-22). Every
/// rejection case here never opens a DB connection (Validate runs before SqlConnection is
/// constructed), so a deliberately unreachable connection string is safe to use throughout - if a
/// test here ever DID try to connect, it would fail loudly with a connection exception, not
/// silently pass.
/// </summary>
public sealed class ReadOnlySqlFetchToolTests
{
    private const string UnreachableConnectionString = "Server=unreachable-host-for-tests;Database=x;Connection Timeout=1;";

    private static ReadOnlySqlFetchTool NewTool() => new(UnreachableConnectionString, userId: 38, customerId: 29);

    [Theory]
    [InlineData("SELECT * FROM #scoped WHERE Imprisonment = 1")]
    [InlineData("select branchname, count(*) as n from #scoped group by branchname")]
    [InlineData("WITH x AS (SELECT * FROM #scoped) SELECT * FROM x")]
    public async Task FetchDataAsync_WellFormedSelectReferencingScoped_PassesValidation(string sql)
    {
        var tool = NewTool();

        var result = await tool.FetchDataAsync(sql);

        // Validation passed - it attempted a real connection to an unreachable host, which fails
        // with a real connection error, not a validation error. Distinguishes "rejected by shape
        // check" from "rejected because the test host is unreachable" (expected here).
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.DoesNotContain("must", err.GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disallowed", err.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchDataAsync_MissingScopedReference_RejectedBeforeConnecting()
    {
        var tool = NewTool();

        var result = await tool.FetchDataAsync("SELECT * FROM CustomerBranch");

        AssertRejected(result, "#scoped");
        Assert.Equal(0, tool.CallsMade);
    }

    [Theory]
    [InlineData("INSERT INTO #scoped VALUES (1)")]
    [InlineData("UPDATE #scoped SET RiskType = 1")]
    [InlineData("DELETE FROM #scoped")]
    [InlineData("DROP TABLE #scoped")]
    [InlineData("ALTER TABLE #scoped ADD X INT")]
    [InlineData("EXEC sp_who")]
    [InlineData("SELECT * FROM #scoped; DROP TABLE CustomerBranch")]
    public async Task FetchDataAsync_WriteOrDangerousStatement_RejectedBeforeConnecting(string sql)
    {
        var tool = NewTool();

        var result = await tool.FetchDataAsync(sql);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
        Assert.Equal(0, tool.CallsMade);
    }

    [Fact]
    public async Task FetchDataAsync_DoesNotStartWithSelectOrWith_Rejected()
    {
        var tool = NewTool();

        var result = await tool.FetchDataAsync("garbage SELECT * FROM #scoped");

        AssertRejected(result, "SELECT or WITH");
    }

    [Fact]
    public async Task FetchDataAsync_EmptyQuery_Rejected()
    {
        var tool = NewTool();

        var result = await tool.FetchDataAsync("   ");

        AssertRejected(result, "Empty");
    }

    [Fact]
    public async Task FetchDataAsync_BudgetExhausted_RejectsFourthCallWithoutValidating()
    {
        var tool = NewTool();
        // First 3 calls: well-formed, so they pass validation and attempt (and fail) a real
        // connection to the unreachable host - that's fine, they still count against budget.
        await tool.FetchDataAsync("SELECT * FROM #scoped");
        await tool.FetchDataAsync("SELECT * FROM #scoped");
        await tool.FetchDataAsync("SELECT * FROM #scoped");

        var fourth = await tool.FetchDataAsync("SELECT * FROM #scoped");

        AssertRejected(fourth, "Budget exhausted");
        Assert.Equal(ReadOnlySqlFetchTool.MaxCalls, tool.CallsMade);
    }

    private static void AssertRejected(string json, string expectedReasonSubstring)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out var err));
        Assert.Contains(expectedReasonSubstring, err.GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
