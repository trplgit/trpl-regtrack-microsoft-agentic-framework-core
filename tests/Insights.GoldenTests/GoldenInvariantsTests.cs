using Insights.Data;

namespace Insights.GoldenTests;

/// <summary>
/// Wraps usp_Insights_GoldenInvariants (sql/02) per docs/SOLUTION_STRUCTURE.md.
/// Must pass on 5+ tenants with different profiles before any dimension proc
/// ships (CLAUDE.md Section 9) - the two below are a start, not the full set.==
///====
/// Connects to ConnectionStrings:RegTrack via the ConnectionStrings__RegTrack
/// environment variable (standard .NET double-underscore override), falling
/// back to the same non-production default as appsettings.Development.json.
/// NEVER point this at production - CLAUDE.md Section 8, docs/SOLUTION_STRUCTURE.md.
/// </summary>
public sealed class GoldenInvariantsTests
{
    private const string DefaultDevConnectionString =
        "Server=localhost;Database=vitComplianceSystem_dev;Trusted_Connection=True;TrustServerCertificate=True";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__RegTrack") ?? DefaultDevConnectionString;

    // Tenant 5: newly-onboarded profile, 20 distinct statuses (CLAUDE.md Section 11).
    // Tenant 29: 89% of estate under a soft-deleted parent - the orphan-hierarchy
    // stress test (CLAUDE.md Section 5 / Section 11).
    // Tenant 999001/999002: the golden fixture itself (docs/GOLDEN_FIXTURES.md, sql/00) -
    // frozen, hand-verified expected values, seeded by sql/00_golden_fixture_999001.sql.
    // Live-validated 2026-08-26: all G-1..G-9 PASS for both, aggregate expectations match
    // exactly (past-due=80, overdue=40, completed=20, resolved_terminal=20, on-time=50.0%).
    [Theory]
    [InlineData(5)]
    [InlineData(29)]
    [InlineData(999001)]
    [InlineData(999002)]
    public async Task AllInvariantsPass(int customerId)
    {
        IGoldenRegressionRepository repository = new SqlGoldenRegressionRepository(ConnectionString);

        var run = await repository.RunAsync(customerId);

        Assert.True(run.AllPassed, $"tenant {customerId}: {run.DescribeFailures()}");
    }
}
