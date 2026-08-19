using Insights.Domain;

namespace Insights.Data;

/// <summary>Wraps the entity hierarchy service (sql/04 Part 1) - apex-or-orphan anchored, reconciled.</summary>
public interface IEntityRepository
{
    /// <summary>The full branch tree for a tenant, every node tagged with its apex/orphan root.</summary>
    Task<IReadOnlyList<EntityTreeNode>> GetEntityTreeAsync(int customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-node instance rollup, reconciled to the tenant control total. Throws
    /// <see cref="EntityRollupReconciliationException"/> if a node was dropped -
    /// the caller must refuse to publish, never degrade to a warning.
    /// </summary>
    Task<EntityRollup> GetEntityRollupAsync(int customerId, CancellationToken cancellationToken = default);

    /// <summary>Picks the comparison grain (apex / descend-one-level / locations) for this tenant's shape.</summary>
    Task<TenantShapeResult> GetTenantShapeAsync(int customerId, decimal dominanceThreshold = 70.00m, CancellationToken cancellationToken = default);
}
