using DurableTask.Core;
using DurableTask.Core.Exceptions;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration;

/// <inheritdoc cref="IInsightsRunEnqueuer"/>
public sealed class DurableTaskRunEnqueuer(TaskHubClient client) : IInsightsRunEnqueuer
{
    public async Task<string> EnqueueAsync(
        int tenantId, string reportType, InsightsScopeRequest scope, string period, int userId,
        CancellationToken cancellationToken = default, LlmCallPriority priority = LlmCallPriority.Interactive,
        IReadOnlyList<string>? requestedDimensions = null)
    {
        /*  The instance id is DERIVED, not left to DTFx - same reasoning as
            InsightsRunOnceWorker: it IS the one-active-run-per-key lock, and it carries the
            tenant, which is the only way GET /api/insights/runs/{runId}/stream can re-derive
            eligibility, since that URL has no tenantId of its own.

            [KNOWN GAP, 2026-09-09] InsightsRunId.For's key is (tenantId, scope, reportType,
            period) - it does NOT fold in requestedDimensions. For "dimension_selection" this
            means two different dimension subsets against the SAME period collide onto the SAME
            run id: the second POST attaches to the first's already-running (or already-cooled-
            down) instance instead of starting its own, silently ignoring the caller's actual
            dimension selection. Not fixed here - InsightsRunId's key shape is a locked identity
            format (API_CONTRACTS.md's runId, the cooldown key, the DTFx instance id all derive
            from it), so widening it is a real design decision, not a plumbing change - flagged,
            not silently worked around. Safe today only because every manual test so far used a
            distinct Period per dimension subset.                                               */
        var runId = InsightsRunId.For(tenantId, scope.ToDescriptor(), reportType, period);
        var input = new InsightsReportOrchestrationInput(tenantId, reportType, scope, period, userId, priority, requestedDimensions);

        /*  [BUG FOUND LIVE, 2026-09-11] The "one-active-run-per-key lock" this class and
            InsightsRunId's own doc comment both describe ("a second enqueue for the same key
            attaches to the running instance instead of starting a duplicate") is NOT something
            DTFx does automatically on this call - the plain overload throws
            OrchestrationAlreadyExistsException the instant an instance with this id is Pending or
            Running, full stop. Confirmed live: a real retry against a key whose first attempt was
            still in flight surfaced this exception as an unhandled 500, not a silent attach.
            Since runId is already fully DETERMINISTIC from the key (not something this call
            invents), "attaching" is simply: catch the conflict, and hand back the same id anyway
            - the caller was always going to get told to poll/stream this exact id, whether this
            call created it moments ago or minutes ago.                                          */
        try
        {
            await client.CreateOrchestrationInstanceAsync(
                InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, runId, input);
        }
        catch (OrchestrationAlreadyExistsException)
        {
        }

        return runId;
    }
}
