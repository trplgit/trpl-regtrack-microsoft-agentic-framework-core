using DurableTask.Core;
using Insights.Agents;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ReflectOnCompositionInput(
    CompositionPlan Plan, IReadOnlyList<Assertion> Assertions, IReadOnlyList<Finding> Findings, string TenantShape,
    LlmCallPriority Priority = LlmCallPriority.Interactive);

public sealed record ReflectOnCompositionOutput(CompositionReflectionResult Result, long TotalTokens);

/// <summary>Node 5r. The bounded revise loop itself lives in the orchestrator, not here (Task 14).</summary>
public sealed class ReflectOnCompositionActivity(ICompositionReflectionAgent reflectionAgent)
    : AsyncTaskActivity<ReflectOnCompositionInput, ReflectOnCompositionOutput>
{
    protected override Task<ReflectOnCompositionOutput> ExecuteAsync(TaskContext context, ReflectOnCompositionInput input) => RunAsync(input);

    internal async Task<ReflectOnCompositionOutput> RunAsync(ReflectOnCompositionInput input)
    {
        using var _priority = LlmCallPriorityContext.Push(input.Priority);
        var result = await reflectionAgent.ReflectAsync(input.Plan, input.Assertions, input.Findings, input.TenantShape, CancellationToken.None);
        return new ReflectOnCompositionOutput(result.Value, result.TotalTokens);
    }
}
