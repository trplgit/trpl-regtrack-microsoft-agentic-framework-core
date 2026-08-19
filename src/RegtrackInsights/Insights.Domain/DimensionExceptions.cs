namespace Insights.Domain;

/// <summary>
/// Thrown when a dimension proc refuses to compute because the caller has no authorised
/// (branch, category) pairs for the tenant - SQL error base+0, e.g. 51030 for Location.
///
/// This is the pre-flight fail-closed gate, not an empty result. An empty scope and a denied
/// scope are different things and must never collapse into "no findings".
/// </summary>
public sealed class DimensionScopeDeniedException(string dimension, int userId, int customerId, Exception inner)
    : Exception($"Scope denied for the {dimension} dimension - user {userId} has no authorised (branch, category) pairs for tenant {customerId}. Refusing to compute.", inner)
{
    public string Dimension { get; } = dimension;
    public int UserId { get; } = userId;
    public int CustomerId { get; } = customerId;
}

/// <summary>
/// Thrown when a dimension's per-member sums do not tie to the scoped instance total - SQL error
/// base+1, e.g. 51031 for Location.
///
/// The proc THROWs before selecting anything, so there is no partial result to salvage. A caller
/// catching this MUST refuse to publish; never degrade it to a warning and never fall back to the
/// unreconciled numbers, which is precisely how a report "still looks right" while dropping a
/// thousand instances.
/// </summary>
public sealed class DimensionReconciliationException(string dimension, int customerId, Exception inner)
    : Exception($"{dimension} dimension reconciliation failed for tenant {customerId} - per-member sums do not tie to the scoped total. Refusing to publish.", inner)
{
    public string Dimension { get; } = dimension;
    public int CustomerId { get; } = customerId;
}

/// <summary>
/// Thrown when a dimension cannot resolve a value it needs from the classification dictionary -
/// SQL error base+2, e.g. 51032 for Location.
///
/// Most often the RiskType value meaning Critical. The procs refuse rather than fall back to a
/// literal, because a literal would silently report the wrong critical count with no error if the
/// mapping were ever re-ruled.
/// </summary>
public sealed class DimensionDictionaryGapException(string dimension, Exception inner)
    : Exception($"{dimension} dimension cannot resolve a required value from the classification dictionary. Refusing to compute.", inner)
{
    public string Dimension { get; } = dimension;
}

/// <summary>
/// Thrown when a dimension's six result sets violate a contract rule that SQL cannot enforce with
/// a THROW - see <see cref="DimensionResult{TControlTotals,TRow}.Validate"/>.
///
/// Reaching this means a detector was wired to mismatched populations, or the emission policy was
/// bypassed. Both would otherwise reach a customer looking entirely plausible.
/// </summary>
public sealed class DimensionContractViolationException(string dimension, string detail)
    : Exception($"{dimension} dimension violated the result contract: {detail} Refusing to publish.")
{
    public string Dimension { get; } = dimension;
}
