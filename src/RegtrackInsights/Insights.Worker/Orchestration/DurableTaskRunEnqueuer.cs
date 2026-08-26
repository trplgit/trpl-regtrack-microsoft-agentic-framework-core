using DurableTask.Core;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration;

/// <inheritdoc cref="IInsightsRunEnqueuer"/>
public sealed class DurableTaskRunEnqueuer(TaskHubClient client) : IInsightsRunEnqueuer
{
    public async Task<string> EnqueueAsync(
        int tenantId, string reportType, InsightsScopeRequest scope, string period, int userId,
        CancellationToken cancellationToken = default, LlmCallPriority priority = LlmCallPriority.Interactive)
    {
        /*  The instance id is DERIVED, not left to DTFx - same reasoning as
            InsightsRunOnceWorker: it IS the one-active-run-per-key lock, and it carries the
            tenant, which is the only way GET /api/insights/runs/{runId}/stream can re-derive
            eligibility, since that URL has no tenantId of its own.                             */
        var runId = InsightsRunId.For(tenantId, scope.ToDescriptor(), reportType, period);
        var input = new InsightsReportOrchestrationInput(tenantId, reportType, scope, period, userId, priority);

        await client.CreateOrchestrationInstanceAsync(
            InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, runId, input);

        return runId;
    }
}
