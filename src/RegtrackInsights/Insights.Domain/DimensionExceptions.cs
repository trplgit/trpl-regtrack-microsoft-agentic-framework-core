namespace Insights.Domain;

/// <summary>
/// Thrown when a dimension proc refuses to compute because the caller has no authorised
/// (branch, category) pairs for the tenant - SQL error base+0, e.g. 51030 for Location.
///
/// This is the pre-flight fail-closed gate, not an empty result. An empty scope and a denied
/// scope are different things and must never collapse into "no findings".
///
/// [DESIGN DOC Sec.11.4] Left uncaught by FetchDimensionsActivity - unlike
/// DimensionReconciliationException/DimensionContractViolationException, this is not a
/// bug-local-to-one-dimension case. GatherScopeActivity already validated scope is non-empty
/// before any dimension is fetched; every dimension proc runs the identical scope pre-flight
/// check, so this firing for one dimension after the gate already passed almost certainly signals
/// a genuine inconsistency (a scope change mid-run, or a real bug), not "just this one section is
/// broken" - safer to fail the whole run loudly than to quietly hide what that inconsistency means.
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
/// The proc THROWs before selecting anything, so there is no partial result to salvage. THIS
/// DIMENSION'S DATA must never reach the user in any form - never degraded to a warning, never
/// falling back to the unreconciled numbers, which is precisely how a report "still looks right"
/// while dropping a thousand instances.
///
/// [DESIGN DOC Sec.11.4] That is not the same as refusing the WHOLE report. This exception (and
/// DimensionContractViolationException) represents a bug local to THIS ONE dimension's own
/// procedure - coverage/risk/nature can compute cleanly while exposure alone errors - so
/// FetchDimensionsActivity catches exactly these two and degrades JUST this dimension's slot to a
/// fixed, non-numeric placeholder (PartialDimensionPlaceholder), continuing with the rest. The two
/// OTHER dimension exceptions (DimensionScopeDeniedException, DimensionDictionaryGapException)
/// still fail the whole run - see their own doc comments for why they are not locally scoped the
/// same way.
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
///
/// [DESIGN DOC Sec.11.3/11.4] NOT locally scoped to one dimension, unlike
/// DimensionReconciliationException/DimensionContractViolationException. The classification
/// dictionary is SHARED across every dimension proc - a gap surfacing here means the tenant's
/// underlying data has an unmapped enum value, which is a data/dictionary integrity problem that
/// plausibly taints every other dimension too, not a bug local to this one procedure. Matches
/// Sec.11.3's own text naming "an unmapped enum raises" as a gate-refusal trigger. Left uncaught
/// by FetchDimensionsActivity on purpose - fails the whole run.
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
///
/// [DESIGN DOC Sec.11.4] Local to this one dimension's own procedure, same as
/// DimensionReconciliationException - FetchDimensionsActivity catches this and degrades just this
/// dimension's slot to a fixed placeholder rather than failing the whole run. See that
/// exception's doc comment for the full reasoning and for why the other two dimension exceptions
/// are treated differently.
/// </summary>
public sealed class DimensionContractViolationException(string dimension, string detail)
    : Exception($"{dimension} dimension violated the result contract: {detail} Refusing to publish.")
{
    public string Dimension { get; } = dimension;
}
