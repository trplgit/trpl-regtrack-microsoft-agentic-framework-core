using DurableTask.Core;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record GatherScopeInput(int UserId, int CustomerId);
public sealed record GatherScopeOutput(IReadOnlyList<ScopePair> ScopePairs);

/// <summary>
/// Nodes 1-2 of the workflow graph: entitlement gate, then scope resolution. Cheapest-first,
/// matching every other entry point in this codebase - an unentitled tenant costs nothing.
/// DTFx activities do not receive a caller CancellationToken (confirmed via OrchestrationContext/
/// TaskContext inspection, Task 1) - CancellationToken.None is passed to the wrapped repository
/// calls deliberately, not an oversight.
/// </summary>
public sealed class GatherScopeActivity(IEntitlementRepository entitlementRepository, IScopeRepository scopeRepository)
    : AsyncTaskActivity<GatherScopeInput, GatherScopeOutput>
{
    // ExecuteAsync is `protected` on AsyncTaskActivity - InternalsVisibleTo does not reach
    // protected members, so tests cannot call it directly. Delegating to an `internal` method
    // keeps DTFx's required override shape while giving tests a real, directly-callable entry
    // point - same pattern applied to every activity in this plan from here on.
    protected override Task<GatherScopeOutput> ExecuteAsync(TaskContext context, GatherScopeInput input) => RunAsync(input);

    internal async Task<GatherScopeOutput> RunAsync(GatherScopeInput input)
    {
        var gate = await entitlementRepository.EvaluateGateAsync(input.CustomerId, EntitlementTier.Paid, CancellationToken.None);
        if (!gate.ShouldProceed)
            throw new OrchestrationRefusedException("NOT_ENTITLED", gate.Reason);

        var pairs = await scopeRepository.GetScopePairsAsync(input.UserId, input.CustomerId, CancellationToken.None);
        if (pairs.Count == 0)
            throw new OrchestrationRefusedException("SCOPE_DENIED", "No entities are currently in your Insights scope.");

        return new GatherScopeOutput(pairs);
    }
}
