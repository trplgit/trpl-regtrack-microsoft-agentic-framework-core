using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Enqueues a paid report run. Kept as an interface for the same reason as
/// <see cref="IRunStatusReader"/> - the RegTrack API can serve API_CONTRACTS.md §3 without
/// taking a hard dependency on Durable Task in its own composition root; the DTFx-backed
/// implementation lives in Insights.Worker.
///
/// [TRAP] This interface performs NO authorisation and NO scope resolution. It answers "start
/// this run", not "may you ask for it" or "do you have anything in scope". The caller checks
/// tenant eligibility and resolves scope first, every request - this only enqueues.
/// </summary>
public interface IInsightsRunEnqueuer
{
    /// <summary>
    /// Starts (or attaches to an already-running) orchestration for this key, and returns the run
    /// id. The run id is DERIVED from (tenantId, scope, reportType, period) - not random - which
    /// is what makes a second call for the same key attach to the existing run instead of starting
    /// a duplicate (API_CONTRACTS.md §3 step 4, the one-active-run-per-key lock).
    /// </summary>
    Task<string> EnqueueAsync(
        int tenantId, string reportType, InsightsScopeRequest scope, string period, int userId,
        CancellationToken cancellationToken = default);
}
