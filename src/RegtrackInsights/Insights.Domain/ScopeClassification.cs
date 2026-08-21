namespace Insights.Domain;

/// <summary>
/// Mirrors the ScopeClass values usp_Insights_ClassifyScope computes in SQL.
/// The classification itself is decided entirely by the proc (both branch AND
/// category axes) - this enum only types the result, it decides nothing.
/// </summary>
public enum ScopeClass
{
    /// <summary>Zero scope pairs. The caller must refuse, never treat as unrestricted.</summary>
    Deny,

    /// <summary>All active branches AND all tenant categories.</summary>
    TenantWide,

    /// <summary>All active branches but not all categories - a functional head, not a tenant-wide CCO.</summary>
    Functional,

    /// <summary>Anything less than the above.</summary>
    EntityScoped,
}

/// <summary>Result of usp_Insights_ClassifyScope for one (user, tenant) pair.</summary>
public sealed record ScopeClassification(
    int UserId,
    int CustomerId,
    int ScopeBranches,
    int TenantBranches,
    int ScopeCategories,
    int TenantCategories,
    ScopeClass Class,
    bool IsDenied);
