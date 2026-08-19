namespace Insights.Domain;

/// <summary>Whether a root node is a true apex (no parent) or an orphan (parent soft-deleted/missing).</summary>
public enum EntityRootKind
{
    Apex,
    Orphan,
}

/// <summary>Whether a node has active children.</summary>
public enum EntityNodeType
{
    Leaf,
    Intermediate,
}

/// <summary>
/// One node from tvfInsightsEntityTree - the full branch hierarchy, anchored on
/// apex OR orphan so a soft-deleted parent never makes an active subtree unreachable
/// (sql/04, the fix for the 89%-of-estate-lost defect).
/// </summary>
public sealed record EntityTreeNode(
    int BranchId, string BranchName, int? ParentId,
    int ApexId, string ApexName, EntityRootKind RootKind, int Depth, EntityNodeType NodeType);
