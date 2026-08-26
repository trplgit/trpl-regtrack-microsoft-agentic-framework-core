using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Wraps the nine dimension procs (sql/05, sql/07 - sql/14). Every one emits the SAME six result
/// sets in the same order - control_totals, rows, detector_policy, assertions, findings,
/// data_quality - which is what lets a single repository serve all of them.
///
/// EVERY METHOD CAN THROW, and that is the point:
///   <see cref="DimensionScopeDeniedException"/>       - caller has no authorised scope
///   <see cref="DimensionReconciliationException"/>    - per-member sums do not tie to the total
///   <see cref="DimensionDictionaryGapException"/>     - a required dictionary value is missing
///   <see cref="DimensionContractViolationException"/> - a result-set rule SQL cannot enforce
///
/// None of these may EVER be degraded to a warning, an empty result, or the unreconciled numbers -
/// a dimension that silently drops a thousand instances still looks entirely plausible in a
/// report, which is the failure this whole design exists to prevent. That rule is absolute and
/// applies to all four uniformly.
///
/// What is NOT absolute: whether one of these failing must refuse the WHOLE report.
/// FetchDimensionsActivity (design doc Sec.11.4, "Partial generation") catches
/// DimensionReconciliationException and DimensionContractViolationException specifically - both
/// represent a bug local to ONE dimension's own procedure - and degrades just that dimension's
/// slot to a fixed, non-numeric placeholder, publishing the rest of the report. The dimension's
/// DATA still never reaches the user in any form; only whether the REPORT AS A WHOLE still ships
/// differs. DimensionScopeDeniedException and DimensionDictionaryGapException remain uncaught and
/// still fail the entire run - see each exception's own doc comment for why.
/// </summary>
public interface IDimensionRepository
{
    /// <summary>Locations - the geography cut. Members are branches; ranks carry a materiality floor.</summary>
    Task<DimensionResult<LocationControlTotals, LocationRow>> GetLocationAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Entity hierarchy - apex-or-orphan anchored. Reconciles on DirectInstances, never SubtreeInstances.</summary>
    Task<DimensionResult<EntityControlTotals, EntityRow>> GetEntityAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Risk tiers, with the critical value taken from the dictionary rather than a literal.</summary>
    Task<DimensionResult<RiskControlTotals, RiskRow>> GetRiskAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Nature of compliance. Half-blind by construction - always surface the uncategorised caveat.</summary>
    Task<DimensionResult<NatureControlTotals, NatureRow>> GetNatureAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Departments. Obligations with no department are counted back but appear in no row.</summary>
    Task<DimensionResult<DepartmentsControlTotals, DepartmentsRow>> GetDepartmentsAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Acts, one row per Act per state - the same law across states is several rows.</summary>
    Task<DimensionResult<ActControlTotals, ActRow>> GetActAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Users. Does NOT partition instances - see <see cref="UsersControlTotals"/> before deriving any share.</summary>
    Task<DimensionResult<UsersControlTotals, UsersRow>> GetUsersAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Internal vs statutory obligations - two populations, reconciled independently.</summary>
    Task<DimensionResult<InternalControlTotals, InternalRow>> GetInternalAsync(
        int userId, int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event-triggered compliance. Counts EVENT instances, a different population from the other
    /// eight. <paramref name="dormancyMonths"/> is the activity window; the proc defaults to 12.
    /// </summary>
    Task<DimensionResult<EventControlTotals, EventRow>> GetEventAsync(
        int userId, int customerId, DateTime? asOf = null, int dormancyMonths = 12, CancellationToken cancellationToken = default);
}
