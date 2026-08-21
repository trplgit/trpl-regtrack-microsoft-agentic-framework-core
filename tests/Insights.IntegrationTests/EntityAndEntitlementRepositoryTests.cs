using Insights.Data;
using Insights.Domain;

namespace Insights.IntegrationTests;

/// <summary>
/// Wraps sql/04 (entity hierarchy + entitlement gate). Same real-data-now-over-
/// no-coverage stance as ScopeRepositoryTests - see that file's note on the
/// fixture database gap. Tenant 29 is the orphan-hierarchy stress test tenant
/// (89% of estate under a soft-deleted parent, CLAUDE.md Section 5/11) - exactly
/// the case usp_Insights_EntityRollup's reconciliation exists to catch.
/// </summary>
public sealed class EntityAndEntitlementRepositoryTests
{
    private const string DefaultDevConnectionString =
        "Server=localhost;Database=vitComplianceSystem_dev;Trusted_Connection=True;TrustServerCertificate=True";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__RegTrack") ?? DefaultDevConnectionString;

    private static IEntityRepository EntityRepository => new SqlEntityRepository(ConnectionString);
    private static IEntitlementRepository EntitlementRepository => new SqlEntitlementRepository(ConnectionString);

    [Fact]
    public async Task GetEntityTreeAsync_ReturnsNodes_ForRealTenant()
    {
        var nodes = await EntityRepository.GetEntityTreeAsync(customerId: 29);

        Assert.NotEmpty(nodes);
    }

    [Fact]
    public async Task GetEntityTreeAsync_FlagsAtLeastOneOrphanRoot_ForTenant29()
    {
        // This is the whole point of tenant 29 - it must have an orphan-rooted
        // subtree, or the fixture the rest of this suite relies on has changed.
        var nodes = await EntityRepository.GetEntityTreeAsync(customerId: 29);

        Assert.Contains(nodes, n => n.RootKind == EntityRootKind.Orphan);
    }

    [Fact]
    public async Task GetEntityRollupAsync_ReconciliesCleanly_ForRealTenant()
    {
        // Throws EntityRollupReconciliationException if anything doesn't tie out -
        // a clean return here IS the assertion.
        var rollup = await EntityRepository.GetEntityRollupAsync(customerId: 29);

        Assert.NotEmpty(rollup.Nodes);
    }

    [Fact]
    public async Task GetTenantShapeAsync_ReturnsAGrain_ForRealTenant()
    {
        var shape = await EntityRepository.GetTenantShapeAsync(customerId: 29);

        Assert.True(shape.ApexEntityCount > 0);
        Assert.NotEmpty(shape.Apexes);
    }

    [Theory]
    [InlineData(EntitlementTier.Free)]
    [InlineData(EntitlementTier.Paid)]
    public async Task EvaluateGateAsync_ReturnsADecision_WithoutThrowing(EntitlementTier tier)
    {
        // No fixed expectation on the decision itself - real entitlement data, will
        // vary by tenant/tier. The contract under test is that the gate runs cleanly
        // and always returns SOMETHING actionable (never null, never an exception).
        var result = await EntitlementRepository.EvaluateGateAsync(customerId: 29, tier);

        Assert.Equal(29, result.CustomerId);
        Assert.Equal(tier, result.Tier);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }
}
