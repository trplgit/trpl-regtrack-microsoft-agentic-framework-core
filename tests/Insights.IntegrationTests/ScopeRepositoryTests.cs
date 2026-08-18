using Insights.Data;
using Insights.Domain;

namespace Insights.IntegrationTests;

/// <summary>
/// Wraps sql/03 (scope resolution) per docs/SOLUTION_STRUCTURE.md.
///
/// NOTE: this project's stated target is the fixture database (tenant 999001,
/// docs/GOLDEN_FIXTURES.md), which does not exist yet. Until it does, these run
/// against a real non-production tenant instead (user 38 / tenant 29, confirmed
/// present 2026-08-18) - real coverage now beats no coverage while that gap closes.
/// Swap to the fixture tenant once it lands.
///
/// Connects via ConnectionStrings__RegTrack, same convention as Insights.GoldenTests.
/// NEVER point this at production.
/// </summary>
public sealed class ScopeRepositoryTests
{
    private const string DefaultDevConnectionString =
        "Server=localhost;Database=vitComplianceSystem_dev;Trusted_Connection=True;TrustServerCertificate=True";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__RegTrack") ?? DefaultDevConnectionString;

    private static IScopeRepository Repository => new SqlScopeRepository(ConnectionString);

    [Fact]
    public async Task GetScopePairsAsync_ReturnsPairs_ForUserWithScope()
    {
        var pairs = await Repository.GetScopePairsAsync(userId: 38, customerId: 29);

        Assert.NotEmpty(pairs);
    }

    [Fact]
    public async Task GetScopePairsAsync_ReturnsEmpty_ForUserWithNoAssignments()
    {
        // A user id with no EntitiesAssignment rows for this tenant must DENY -
        // empty, never "unrestricted" (the central trap in sql/03).
        var pairs = await Repository.GetScopePairsAsync(userId: 999999999, customerId: 29);

        Assert.Empty(pairs);
    }

    [Fact]
    public async Task ClassifyScopeAsync_ReturnsDeny_WhenScopeIsEmpty()
    {
        var result = await Repository.ClassifyScopeAsync(userId: 999999999, customerId: 29);

        Assert.Equal(ScopeClass.Deny, result.Class);
        Assert.True(result.IsDenied);
    }

    [Fact]
    public async Task ClassifyScopeAsync_ReturnsNonDeny_ForUserWithScope()
    {
        var result = await Repository.ClassifyScopeAsync(userId: 38, customerId: 29);

        Assert.NotEqual(ScopeClass.Deny, result.Class);
        Assert.False(result.IsDenied);
    }

    [Fact]
    public async Task AuditScopeAsync_Passes_ForUserWithScope()
    {
        var result = await Repository.AuditScopeAsync(userId: 38, customerId: 29);

        Assert.Equal(0, result.BranchViolations);
        Assert.Equal(0, result.CategoryViolations);
        Assert.Equal(0, result.PairViolations);
    }

    [Fact]
    public async Task FindScopelessUsersAsync_ReturnsWithoutThrowing()
    {
        // No fixed expectation on count - this is real data and will vary. The
        // contract under test is just that the provisioning check runs cleanly.
        var users = await Repository.FindScopelessUsersAsync();

        Assert.NotNull(users);
    }
}
