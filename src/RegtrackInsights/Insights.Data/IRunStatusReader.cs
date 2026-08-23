using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Reads a report run's progress. Kept as an interface so the RegTrack API can serve
/// API_CONTRACTS.md §4 without taking a hard dependency on Durable Task in its own composition
/// root - the DTFx-backed implementation lives in Insights.Worker.
///
/// [TRAP] This interface performs NO authorisation. It answers "what is this run doing", not
/// "may you ask". The caller re-checks tenant eligibility first, every request.
/// </summary>
public interface IRunStatusReader
{
    /// <summary>
    /// The current snapshot, or null when no such run exists. Callers must render null exactly as
    /// they render a run belonging to another tenant - a distinguishable answer confirms the run
    /// exists, which is what REPORT_NOT_VISIBLE being 404 rather than 403 exists to prevent.
    /// </summary>
    Task<InsightsRunStatus?> GetStatusAsync(string runId, CancellationToken cancellationToken = default);
}
