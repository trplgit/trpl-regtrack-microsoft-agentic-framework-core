using DurableTask.Core;
using Insights.Agents;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ReflectOnNarrativeInput(
    NarrativeResult Narrative, IReadOnlyList<Assertion> Assertions, IReadOnlyList<Finding> Findings,
    LlmCallPriority Priority = LlmCallPriority.Interactive);
public sealed record ReflectOnNarrativeOutput(NarrativeReflectionResult Result, long TotalTokens);

/// <summary>Node 6r.</summary>
public sealed class ReflectOnNarrativeActivity(INarrativeReflectionAgent reflectionAgent, IAgentReasoningRecorder? reasoningRecorder = null)
    : AsyncTaskActivity<ReflectOnNarrativeInput, ReflectOnNarrativeOutput>
{
    protected override Task<ReflectOnNarrativeOutput> ExecuteAsync(TaskContext context, ReflectOnNarrativeInput input) =>
        RunAsync(input, context.OrchestrationInstance.InstanceId);

    internal async Task<ReflectOnNarrativeOutput> RunAsync(ReflectOnNarrativeInput input, string? runId = null)
    {
        using var _priority = LlmCallPriorityContext.Push(input.Priority);
        var result = await reflectionAgent.ReflectAsync(input.Narrative, input.Assertions, input.Findings, CancellationToken.None);

        if (runId is not null)
        {
            try
            {
                await (reasoningRecorder ?? IAgentReasoningRecorder.Null).RecordAsync(runId, "reflect_narrative", result.ReasoningSummary);
            }
            catch
            {
                // Reasoning capture is not worth a response.
            }
        }

        return new ReflectOnNarrativeOutput(result.Value, result.TotalTokens);
    }
}
