using DurableTask.Core;
using Insights.Data;

namespace Insights.Worker.Orchestration;

/// <inheritdoc cref="IInsightsRunCanceller"/>
public sealed class DurableTaskRunCanceller(TaskHubClient client) : IInsightsRunCanceller
{
    public async Task<bool> CancelAsync(string runId, string reason, CancellationToken cancellationToken = default)
    {
        var state = await client.GetOrchestrationStateAsync(runId);
        if (state is null || IsTerminal(state.OrchestrationStatus))
            return false;

        await client.TerminateInstanceAsync(new OrchestrationInstance { InstanceId = runId }, reason);
        return true;
    }

    private static bool IsTerminal(OrchestrationStatus status) => status switch
    {
        OrchestrationStatus.Completed => true,
        OrchestrationStatus.Failed => true,
        OrchestrationStatus.Terminated => true,
        OrchestrationStatus.Canceled => true,
        _ => false,
    };
}
