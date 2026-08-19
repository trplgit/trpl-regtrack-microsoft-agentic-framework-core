namespace Insights.Domain;

/// <summary>One tree node with its direct instance count - the same shape as EntityTreeNode, plus DirectInstances.</summary>
public sealed record EntityRollupNode(
    int BranchId, string BranchName, int? ParentId,
    int ApexId, string ApexName, EntityRootKind RootKind, int Depth, EntityNodeType NodeType,
    int DirectInstances);

/// <summary>An apex subtree whose parent entity was soft-deleted - surfaced, never silently re-parented.</summary>
public sealed record OrphanDeclaration(int ApexId, string ApexName, int SubtreeInstances, string DataQualityNote);

/// <summary>Root-level rollup summary for one apex (or orphan root).</summary>
public sealed record EntityRootSummary(
    int ApexId, string ApexName, EntityRootKind RootKind,
    int NodesInSubtree, int SubtreeInstances, int InstancesOnIntermediateNodes, int InstancesOnLeaves);

/// <summary>
/// Result of usp_Insights_EntityRollup - only ever constructed on the success path.
/// The proc THROWs (51020) before selecting anything if subtree sums do not tie to
/// the tenant control total, so a caller holding one of these knows it reconciled.
/// See <see cref="EntityRollupReconciliationException"/> for the failure path.
/// </summary>
public sealed class EntityRollup(
    IReadOnlyList<OrphanDeclaration> orphans,
    IReadOnlyList<EntityRootSummary> roots,
    IReadOnlyList<EntityRollupNode> nodes)
{
    public IReadOnlyList<OrphanDeclaration> Orphans { get; } = orphans;
    public IReadOnlyList<EntityRootSummary> Roots { get; } = roots;
    public IReadOnlyList<EntityRollupNode> Nodes { get; } = nodes;
}

/// <summary>
/// Thrown when usp_Insights_EntityRollup's subtree sums do not tie to the tenant
/// control total (error 51020) - a node was dropped, most likely an instance-bearing
/// intermediate one. Refuse to publish; never degrade to a warning.
/// </summary>
public sealed class EntityRollupReconciliationException(int customerId, Exception inner)
    : Exception($"Entity rollup reconciliation failed for tenant {customerId} - a node was dropped. Refusing to publish.", inner)
{
    public int CustomerId { get; } = customerId;
}
