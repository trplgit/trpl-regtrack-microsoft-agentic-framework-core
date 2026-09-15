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

/// <summary>
/// The wire names for <see cref="ScopeClass"/>. SQL emits them (usp_Insights_ClassifyScope,
/// usp_Insights_EligibleTenants) and API_CONTRACTS.md §1 puts the same strings on the wire, so
/// the mapping lives here once rather than in each repository and each endpoint.
/// </summary>
public static class ScopeClassNames
{
    /// <summary>
    /// Maps a ScopeClass string from SQL. Throws on an unrecognised value rather than defaulting:
    /// silently treating an unknown class as the most permissive one would be a scope leak, and as
    /// the least permissive one would be an outage nobody could explain. Fail loudly instead.
    /// </summary>
    public static ScopeClass Parse(string value) => value switch
    {
        "DENY" => ScopeClass.Deny,
        "tenant_wide" => ScopeClass.TenantWide,
        "functional" => ScopeClass.Functional,
        "entity_scoped" => ScopeClass.EntityScoped,
        _ => throw new InvalidOperationException($"Unknown ScopeClass '{value}' from SQL."),
    };

    /// <summary>The contract spelling, for JSON. Never <c>ToString()</c> - that gives PascalCase.</summary>
    public static string ToContractName(this ScopeClass value) => value switch
    {
        ScopeClass.Deny => "DENY",
        ScopeClass.TenantWide => "tenant_wide",
        ScopeClass.Functional => "functional",
        ScopeClass.EntityScoped => "entity_scoped",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unmapped ScopeClass."),
    };
}
