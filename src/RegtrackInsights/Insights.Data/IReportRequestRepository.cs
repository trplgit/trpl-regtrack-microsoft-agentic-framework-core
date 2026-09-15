using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Fan-out request grouping (sql/30_report_request.sql) - reads GeneratedReport's sibling table
/// directly via EF Core, the same exception to "Dapper for the read path" ICooldownRepository
/// already relies on (CLAUDE.md 7): plain CRUD over one table, no scope/entitlement/
/// reconciliation logic to enforce in SQL.
/// </summary>
public interface IReportRequestRepository
{
    /// <summary>One row per runId, all sharing reqId - called once, right after the fan-out POST enqueues every unit.</summary>
    Task SaveAsync(Guid reqId, IReadOnlyList<string> runIds, CancellationToken cancellationToken = default);

    /// <summary>Every runId a reqId groups. Empty means no such reqId - the caller's own "not found" signal.</summary>
    Task<IReadOnlyList<string>> GetRunIdsAsync(Guid reqId, CancellationToken cancellationToken = default);
}
