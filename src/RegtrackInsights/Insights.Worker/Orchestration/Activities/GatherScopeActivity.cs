using DurableTask.Core;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record GatherScopeInput(int UserId, int CustomerId);
public sealed record GatherScopeOutput(IReadOnlyList<ScopePair> ScopePairs, string TenantShape);

/// <summary>
/// Nodes 1-2 of the workflow graph: entitlement gate, scope resolution, and the tenant-shape
/// lookup composition/reflection need (a third cheap deterministic read, folded in here rather
/// than a separate activity - all three are node-1/2-class reads with no LLM involved).
/// DTFx activities do not receive a caller CancellationToken (confirmed via OrchestrationContext/
/// TaskContext inspection, Task 1) - CancellationToken.None is passed to the wrapped repository
/// calls deliberately, not an oversight.
/// </summary>
public sealed class GatherScopeActivity(
    IEntitlementRepository entitlementRepository, IScopeRepository scopeRepository, IEntityRepository entityRepository)
    : AsyncTaskActivity<GatherScopeInput, GatherScopeOutput>
{
    protected override Task<GatherScopeOutput> ExecuteAsync(TaskContext context, GatherScopeInput input) => RunAsync(input);

    internal async Task<GatherScopeOutput> RunAsync(GatherScopeInput input)
    {
        var gate = await entitlementRepository.EvaluateGateAsync(input.CustomerId, EntitlementTier.Paid, CancellationToken.None);
        if (!gate.ShouldProceed)
            throw new OrchestrationRefusedException("NOT_ENTITLED", gate.Reason);

        var pairs = await scopeRepository.GetScopePairsAsync(input.UserId, input.CustomerId, CancellationToken.None);
        if (pairs.Count == 0)
            throw new OrchestrationRefusedException("SCOPE_DENIED", "No entities are currently in your Insights scope.");

        var shape = await entityRepository.GetTenantShapeAsync(input.CustomerId, cancellationToken: CancellationToken.None);
        var tenantShape = shape.Shape switch
        {
            EntityCountShape.SingleEntity => "single_entity",
            EntityCountShape.MultiEntity => "multi_entity",
            _ => throw new ArgumentOutOfRangeException(nameof(shape.Shape), shape.Shape, "Unknown EntityCountShape - dictionary/enum drift, fail closed rather than guess a prompt-facing string."),
        };

        return new GatherScopeOutput(pairs, tenantShape);
    }
}
