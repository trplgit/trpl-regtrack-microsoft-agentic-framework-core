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
    ///
    /// <paramref name="priority"/> defaults to Interactive - every caller before priority lanes
    /// existed (RunEndpoints.cs's Generate click, InsightsRunOnceWorker's CLI trigger) IS a human
    /// waiting, so the default matches their actual meaning without those call sites needing to
    /// change. Only PaidKeepWarmScheduler passes Batch explicitly (design doc Sec.4.4).
    ///
    /// Placed AFTER <paramref name="cancellationToken"/>, not before it - <paramref
    /// name="cancellationToken"/> already had every existing call site passing it as a bare
    /// positional 6th argument; inserting a new optional parameter ahead of it would silently
    /// rebind those positional CancellationToken arguments onto this one instead (both optional,
    /// so the compiler would accept it - it just would not mean what the call site wrote).
    ///
    /// <paramref name="requestedDimensions"/> [ADDED 2026-09-09] - same trailing-optional
    /// reasoning as <paramref name="priority"/>, placed last so no existing positional call site
    /// shifts meaning. Only meaningful when <paramref name="reportType"/> is
    /// <c>DimensionSelectionComposition.ReportType</c> ("dimension_selection") - forwarded
    /// verbatim into <c>InsightsReportOrchestrationInput.RequestedDimensions</c>, null for every
    /// other report type exactly as before this parameter existed.
    /// </summary>
    Task<string> EnqueueAsync(
        int tenantId, string reportType, InsightsScopeRequest scope, string period, int userId,
        CancellationToken cancellationToken = default, LlmCallPriority priority = LlmCallPriority.Interactive,
        IReadOnlyList<string>? requestedDimensions = null);
}
