using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Design doc Sec.2.4 / API_CONTRACTS.md Sec.3 step 3 - the 30-day cooldown: at most one
/// generation per (scope, report-type, period), keyed to scope not user. Reads GeneratedReport
/// directly via EF Core, the same exception to "Dapper for the read path" ReportContentService
/// already relies on (CLAUDE.md 7) - no stored proc exists for this, and none is needed: it is
/// plain CRUD over one table, no scope/entitlement/reconciliation logic to enforce in SQL.
///
/// Only a Status = "complete" row starts the clock - design doc Sec.2.4 "Not consumed on
/// failure": any failure class, including a still-running "queued" row, must leave the window
/// open, never block a retry after a failed run.
/// </summary>
public interface ICooldownRepository
{
    /// <summary>
    /// True (with a null NextAvailableUtc) when there is no successful generation for this exact
    /// key within the cooldown window - either none exists at all, or the most recent one has
    /// already aged out.
    /// </summary>
    Task<CooldownResult> CheckAsync(
        int customerId, string reportType, string scopeDescriptor, string period,
        CancellationToken cancellationToken = default);
}
