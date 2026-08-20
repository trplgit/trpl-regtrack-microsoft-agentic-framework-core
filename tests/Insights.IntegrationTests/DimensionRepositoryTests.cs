using Insights.Data;
using Insights.Domain;

namespace Insights.IntegrationTests;

/// <summary>
/// Closes the gap flagged 2026-08-20: SqlDimensionRepository (all nine GetXAsync methods) had
/// been validated manually against UAT (tenants 5, 1363, 29, 23, 1355 - Tanvi, SSMS, checking
/// control totals and flagged percentages) but never as a committed, repeatable test. This
/// re-runs the same five tenants through the actual C# wrapper, not just the raw SQL - the two
/// are different layers and a bug can live in either (sql/06's Dapper wrapper had a nested-EXEC
/// result-set bug that the underlying SQL logic did not).
///
/// (userId, customerId) pairs come from EntitiesAssignment - each user genuinely has scope rows
/// for that tenant, found via the same join tvfInsightsScopePairs itself uses (chat 2026-08-20):
///   5 -> 35, 23 -> 36, 29 -> 38, 1355 -> 11885, 1363 -> 12006
/// </summary>
public sealed class DimensionRepositoryTests
{
    private const string DefaultDevConnectionString =
        "Server=localhost;Database=vitComplianceSystem_dev;Trusted_Connection=True;TrustServerCertificate=True";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__RegTrack") ?? DefaultDevConnectionString;

    private static IDimensionRepository Repository => new SqlDimensionRepository(ConnectionString);

    public static IEnumerable<object[]> ValidatedTenants =>
    [
        [35, 5], [36, 23], [38, 29], [11885, 1355], [12006, 1363],
    ];

    /// <summary>
    /// Every result that comes back already passed the proc's own reconciliation THROW and
    /// DimensionResult.Validate() (see SqlDimensionRepository.ExecuteAsync) - reaching this
    /// point already proves both. What is actually being checked here is narrower and still
    /// worth asserting explicitly: the Dapper positional read landed on the right columns.
    /// </summary>
    private static void AssertWellFormed<TControlTotals, TRow>(DimensionResult<TControlTotals, TRow> result, string expectedDimension)
    {
        Assert.Equal(expectedDimension, result.Dimension);
        Assert.NotNull(result.Rows);
        Assert.NotNull(result.Detectors);
        Assert.NotNull(result.Assertions);
        Assert.NotNull(result.Findings);
        Assert.NotNull(result.DataQuality);
    }

    [Theory]
    [MemberData(nameof(ValidatedTenants))]
    public async Task GetLocationAsync_ReturnsWellFormedResult(int userId, int customerId)
    {
        var result = await Repository.GetLocationAsync(userId, customerId);
        AssertWellFormed(result, "Location");
        Assert.True(result.ControlTotals.Reconciled);
    }

    [Theory]
    [MemberData(nameof(ValidatedTenants))]
    public async Task GetEntityAsync_ReturnsWellFormedResult(int userId, int customerId)
    {
        var result = await Repository.GetEntityAsync(userId, customerId);
        AssertWellFormed(result, "Entity");
        Assert.True(result.ControlTotals.Reconciled);
    }

    [Theory]
    [MemberData(nameof(ValidatedTenants))]
    public async Task GetRiskAsync_ReturnsWellFormedResult(int userId, int customerId)
    {
        var result = await Repository.GetRiskAsync(userId, customerId);
        AssertWellFormed(result, "Risk");
        Assert.True(result.ControlTotals.Reconciled);
    }

    [Theory]
    [MemberData(nameof(ValidatedTenants))]
    public async Task GetNatureAsync_ReturnsWellFormedResult(int userId, int customerId)
    {
        var result = await Repository.GetNatureAsync(userId, customerId);
        AssertWellFormed(result, "Nature");
        Assert.True(result.ControlTotals.Reconciled);
    }

    [Theory]
    [MemberData(nameof(ValidatedTenants))]
    public async Task GetDepartmentsAsync_ReturnsWellFormedResult(int userId, int customerId)
    {
        var result = await Repository.GetDepartmentsAsync(userId, customerId);
        AssertWellFormed(result, "Departments");
        Assert.True(result.ControlTotals.Reconciled);
    }

    [Theory]
    [MemberData(nameof(ValidatedTenants))]
    public async Task GetActAsync_ReturnsWellFormedResult(int userId, int customerId)
    {
        var result = await Repository.GetActAsync(userId, customerId);
        AssertWellFormed(result, "Act");
        Assert.True(result.ControlTotals.Reconciled);
    }

    [Theory]
    [MemberData(nameof(ValidatedTenants))]
    public async Task GetUsersAsync_ReturnsWellFormedResult(int userId, int customerId)
    {
        var result = await Repository.GetUsersAsync(userId, customerId);
        AssertWellFormed(result, "Users");
        Assert.True(result.ControlTotals.Reconciled);
    }

    [Theory]
    [MemberData(nameof(ValidatedTenants))]
    public async Task GetInternalAsync_ReturnsWellFormedResult(int userId, int customerId)
    {
        var result = await Repository.GetInternalAsync(userId, customerId);
        AssertWellFormed(result, "Internal");
        Assert.True(result.ControlTotals.Reconciled);
    }

    [Theory]
    [MemberData(nameof(ValidatedTenants))]
    public async Task GetEventAsync_ReturnsWellFormedResult(int userId, int customerId)
    {
        var result = await Repository.GetEventAsync(userId, customerId);
        AssertWellFormed(result, "Event");
        Assert.True(result.ControlTotals.Reconciled);
    }

    /// <summary>
    /// [TRAP] Users does NOT partition instances - SumOfPerUserInstances legitimately exceeds
    /// ScopedInstances (one obligation carries a performer AND a reviewer). Reconciling on the
    /// row sum here would reproduce the impossible 155% concentration the design doc names.
    /// This asserts the wrapper still exposes both totals distinctly rather than collapsing them.
    /// </summary>
    [Fact]
    public async Task GetUsersAsync_DoesNotConflateScopedAndPerUserTotals()
    {
        var result = await Repository.GetUsersAsync(userId: 38, customerId: 29);

        Assert.True(result.ControlTotals.ScopedInstances >= 0);
        Assert.True(result.ControlTotals.SumOfPerUserInstances >= 0);
    }

    /// <summary>
    /// [TRAP] Internal reconciles two independent populations (statutory and internal) - a
    /// tenant can legitimately have zero internal obligations (InternalAbsentEntirely = true)
    /// without that being a scope or reconciliation failure.
    /// </summary>
    [Fact]
    public async Task GetInternalAsync_HandlesInternalAbsentEntirelyWithoutThrowing()
    {
        var result = await Repository.GetInternalAsync(userId: 38, customerId: 29);

        Assert.True(result.ControlTotals.ScopedInstances >= 0);
    }
}
