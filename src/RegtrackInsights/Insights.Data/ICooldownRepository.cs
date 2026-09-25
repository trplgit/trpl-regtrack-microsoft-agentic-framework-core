using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Design doc Sec.2.4 / API_CONTRACTS.md Sec.3 step 3 - the cooldown, keyed to scope not user.
/// Reads GeneratedReport directly via EF Core, the same exception to "Dapper for the read path"
/// ReportContentService already relies on (CLAUDE.md 7) - no stored proc exists for this, and none
/// is needed: it is plain CRUD over one table, no scope/entitlement/reconciliation logic to enforce
/// in SQL.
///
/// [REDESIGNED 2026-09-25] The key is now (customerId, reportType, scopeDescriptor, dimension) -
/// Period dropped entirely. Product decision: "we don't need to look at the combination any more" -
/// the old (scope, reportType, period) key let a caller dodge the lock for the SAME dimension by
/// simply sending a different period string (30 days vs 60 days vs a free-text value); the real
/// thing that must stay locked is the DIMENSION, not any particular period phrasing of it.
/// <paramref name="dimension"/> is the real dimension name (e.g. "Act", case/whitespace-normalised
/// the same way <see cref="Insights.Domain.ReportDimensionKey"/> already does for the interim
/// Period-suffix storage - see EfCooldownRepository's own doc comment for how the match works
/// against today's schema) for a dimension_selection unit, or null for every other report type
/// (fixed_holistic today) - there is only one unit, so the scope+reportType key alone identifies it.
///
/// Only a Status = "complete" row starts the clock - design doc Sec.2.4 "Not consumed on
/// failure": any failure class, including a still-running "queued" row, must leave the window
/// open, never block a retry after a failed run.
/// </summary>
public interface ICooldownRepository
{
    /// <summary>
    /// True (with a null NextAvailableUtc/DaysRemaining) when there is no successful generation for
    /// this exact key within the cooldown window - either none exists at all, or the most recent
    /// one has already aged out. Also true unconditionally when the cooldown is disabled via
    /// Reports:CooldownEnabled (the testing/demo toggle) - see EfCooldownRepository.
    /// </summary>
    Task<CooldownResult> CheckAsync(
        int customerId, string reportType, string scopeDescriptor, string? dimension,
        CancellationToken cancellationToken = default);
}
