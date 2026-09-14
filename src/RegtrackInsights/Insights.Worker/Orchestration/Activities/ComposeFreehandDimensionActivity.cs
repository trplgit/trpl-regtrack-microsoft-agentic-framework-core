using DurableTask.Core;
using Insights.Agents;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ComposeFreehandDimensionInput(
    string Dimension, IReadOnlyList<Assertion> Assertions, IReadOnlyList<Finding> Findings,
    string DimensionRowsJson, string DimensionControlTotalsJson, string DataQualityJson,
    LlmCallPriority Priority = LlmCallPriority.Interactive);

public sealed record ComposeFreehandDimensionOutput(CompositionPlan Plan, long TotalTokens);

/// <summary>
/// Node 4b - runs instead of DimensionSelectionComposition.Build for exactly the four dimensions
/// in <see cref="FreehandDimensions"/>. Keyed by dimension name (Input.Dimension) into
/// IReadOnlyDictionary&lt;string, IFreehandDimensionCompositionAgent&gt; - same "plain dictionary,
/// not keyed DI" reasoning as RenderHtmlActivity's own htmlAgentsByReportType. Throws on an
/// unregistered dimension rather than silently falling through, same fail-closed stance as
/// RenderHtmlActivity's own missing-agent check.
/// </summary>
public sealed class ComposeFreehandDimensionActivity(IReadOnlyDictionary<string, IFreehandDimensionCompositionAgent> agentsByDimension)
    : AsyncTaskActivity<ComposeFreehandDimensionInput, ComposeFreehandDimensionOutput>
{
    protected override Task<ComposeFreehandDimensionOutput> ExecuteAsync(TaskContext context, ComposeFreehandDimensionInput input) =>
        RunAsync(input, context.OrchestrationInstance.InstanceId);

    internal async Task<ComposeFreehandDimensionOutput> RunAsync(ComposeFreehandDimensionInput input, string? runId = null)
    {
        if (!agentsByDimension.TryGetValue(input.Dimension, out var agent))
        {
            throw new InvalidOperationException(
                $"No freehand composition agent registered for dimension '{input.Dimension}'. Registered: {string.Join(", ", agentsByDimension.Keys)}.");
        }

        using var _priority = LlmCallPriorityContext.Push(input.Priority);
        using var _session = LangfuseSessionContext.Push(runId);
        var result = await agent.ComposeAsync(
            input.Assertions, input.Findings, input.DimensionRowsJson, input.DimensionControlTotalsJson, input.DataQualityJson, CancellationToken.None);
        return new ComposeFreehandDimensionOutput(result.Value, result.TotalTokens);
    }
}
