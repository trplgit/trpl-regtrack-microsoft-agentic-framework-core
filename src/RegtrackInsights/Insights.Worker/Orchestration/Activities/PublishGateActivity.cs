using DurableTask.Core;
using Insights.Agents;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record PublishGateInput(int UserId, int CustomerId, NarrativeResult Narrative, IReadOnlyList<Assertion> Assertions);
public sealed record PublishGateOutput(bool Approved);

/// <summary>
/// Node 8, per the corrected ordering (spec 3): runs on the narrative BEFORE rendering, not after.
/// Non-negotiable - a refusal here throws rather than returning a false Approved for the
/// orchestrator to inspect, matching how GatherScopeActivity refuses on empty scope.
/// </summary>
public sealed class PublishGateActivity(PublishGate publishGate) : AsyncTaskActivity<PublishGateInput, PublishGateOutput>
{
    protected override Task<PublishGateOutput> ExecuteAsync(TaskContext context, PublishGateInput input) => RunAsync(input);

    internal async Task<PublishGateOutput> RunAsync(PublishGateInput input)
    {
        var result = await publishGate.EvaluateAsync(input.UserId, input.CustomerId, input.Narrative, input.Assertions, CancellationToken.None);
        if (!result.Approved)
            throw new OrchestrationRefusedException("GATE_REFUSED", result.UserFacingRefusal!, result.InternalDiagnostics);

        return new PublishGateOutput(true);
    }
}
