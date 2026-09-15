using Microsoft.Data.SqlClient;

namespace Insights.GoldenTests;

/// <summary>
/// Tier 2 of the regression strategy (sql/02 Sec.B, docs/GOLDEN_FIXTURES.md): frozen fixture
/// rows with HAND-VERIFIED ABSOLUTE VALUES - the thing usp_Insights_GoldenInvariants itself
/// cannot check, since G-1..G-9 are relationship invariants that hold regardless of what the
/// data contains. This is the other half: does tenant 999001 actually produce past-due=80,
/// overdue=40, completed=20, resolved_terminal=20, on-time%=50.0, and does F-6's rollup
/// actually come out to 25 (not the leaf-only 15). Live-validated 2026-08-26 against real UAT
/// with these exact numbers before sql/00_golden_fixture_999001.sql was written.
/// </summary>
public sealed class GoldenAggregateExpectationsTests
{
    private const string DefaultDevConnectionString =
        "Server=localhost;Database=vitComplianceSystem_dev;Trusted_Connection=True;TrustServerCertificate=True";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__RegTrack") ?? DefaultDevConnectionString;

    [Fact]
    public async Task Tenant999001_AggregatesMatchTheFrozenFixtureExactly()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var cmd = new SqlCommand(@"
            SELECT
                COUNT(*) AS past_due_schedules,
                SUM(CASE WHEN d.OverdueEligible = 1 THEN 1 ELSE 0 END) AS overdue,
                SUM(CASE WHEN d.ClosureClass = 'completed' THEN 1 ELSE 0 END) AS completed,
                SUM(CASE WHEN d.ClosureClass = 'resolved_terminal' THEN 1 ELSE 0 END) AS resolved_terminal,
                SUM(CASE WHEN d.ClosureClass = 'completed' AND d.Timeliness = 'on_time' THEN 1 ELSE 0 END) AS on_time_completions
            FROM ComplianceScheduleOn cso
            JOIN ComplianceInstance i ON i.ID = cso.ComplianceInstanceID
            JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID
            JOIN RecentComplianceTransactionView rct ON rct.ComplianceScheduleOnID = cso.ID
            LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = rct.ComplianceStatusID
            WHERE cb.CustomerID = 999001 AND cb.IsDeleted = 0 AND i.IsDeleted = 0
              AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1 AND cso.ScheduleOn <= GETDATE()", connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        var pastDue = reader.GetInt32(0);
        var overdue = reader.GetInt32(1);
        var completed = reader.GetInt32(2);
        var resolvedTerminal = reader.GetInt32(3);
        var onTime = reader.GetInt32(4);

        Assert.Equal(80, pastDue);
        Assert.Equal(40, overdue);
        Assert.Equal(20, completed);
        Assert.Equal(20, resolvedTerminal);
        Assert.Equal(10, onTime); // 10 of 20 completed -> 50.0% - never assert the 40 denominator (that's the resolved_terminal-leak bug F-4 exists to catch)
    }

    /// <summary>F-6: rollup on an intermediate node holding 10 + two leaves holding 10 and 5 must total 25, not the leaf-only 15.</summary>
    [Fact]
    public async Task Tenant999001_F6Rollup_CountsTheIntermediateNodeNotJustLeaves()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var cmd = new SqlCommand(@"
            SELECT t.BranchName, COUNT(i.ID) AS DirectInstances
            FROM dbo.tvfInsightsEntityTree(999001) t
            LEFT JOIN ComplianceInstance i ON i.CustomerBranchID = t.BranchID AND i.IsDeleted = 0
            WHERE t.BranchName IN (N'Golden Intermediate', N'Golden Leaf A', N'Golden Leaf B')
            GROUP BY t.BranchName", connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        var counts = new Dictionary<string, int>();
        while (await reader.ReadAsync())
            counts[reader.GetString(0)] = reader.GetInt32(1);

        Assert.Equal(10, counts["Golden Intermediate"]);
        Assert.Equal(10, counts["Golden Leaf A"]);
        Assert.Equal(5, counts["Golden Leaf B"]);
        Assert.Equal(25, counts.Values.Sum()); // NOT 15 - a leaf-only rollup would silently drop the intermediate node's own 10
    }

    /// <summary>F-11: an active branch under a soft-deleted parent must be tagged 'orphan' and still counted, never dropped.</summary>
    [Fact]
    public async Task Tenant999001_F11OrphanChild_IsTaggedOrphanAndStillReachable()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var cmd = new SqlCommand(
            "SELECT RootKind FROM dbo.tvfInsightsEntityTree(999001) WHERE BranchName = N'Golden Orphan Child'", connection);
        var rootKind = (string?)await cmd.ExecuteScalarAsync();

        Assert.Equal("orphan", rootKind);
    }
}
