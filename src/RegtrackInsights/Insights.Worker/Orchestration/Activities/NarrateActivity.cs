using DurableTask.Core;
using Insights.Agents;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record NarrateInput(
    CompositionPlan Plan, IReadOnlyList<Assertion> Assertions, IReadOnlyList<Finding> Findings,
    NarrativeResult? PreviousNarrative, IReadOnlyList<NarrativeReflectionIssue>? Issues,
    LlmCallPriority Priority = LlmCallPriority.Interactive,
    // [ADDED 2026-09-14] InsightsReportOrchestrationInput.ReqId, forwarded through - LangFuse
    // session grouping key when present, falls back to the DTFx run id (see RunAsync) otherwise.
    string? ReqId = null);

public sealed record NarrateOutput(NarrativeResult Narrative, long TotalTokens);

/// <summary>Node 6.</summary>
public sealed class NarrateActivity(INarrativeAgent narrativeAgent, IAgentReasoningRecorder? reasoningRecorder = null)
    : AsyncTaskActivity<NarrateInput, NarrateOutput>
{
    protected override Task<NarrateOutput> ExecuteAsync(TaskContext context, NarrateInput input) =>
        RunAsync(input, context.OrchestrationInstance.InstanceId);

    internal async Task<NarrateOutput> RunAsync(NarrateInput input, string? runId = null)
    {
        (NarrativeResult, IReadOnlyList<NarrativeReflectionIssue>)? revision =
            input.PreviousNarrative is not null && input.Issues is not null ? (input.PreviousNarrative, input.Issues) : null;

        using var _priority = LlmCallPriorityContext.Push(input.Priority);
        using var _session = LangfuseSessionContext.Push(input.ReqId ?? runId);
        var result = await narrativeAgent.NarrateAsync(input.Plan, input.Assertions, input.Findings, revision, CancellationToken.None);

        // Best-effort, same stance as MeteredChatClient's own recorder call: this activity is
        // holding a response the tenant has already been billed for, so a logging failure must
        // never fail the run.
        if (runId is not null)
        {
            try
            {
                await (reasoningRecorder ?? IAgentReasoningRecorder.Null).RecordAsync(runId, "narrate", result.ReasoningSummary);
            }
            catch
            {
                // Reasoning capture is not worth a response.
            }
        }

        return new NarrateOutput(result.Value, result.TotalTokens);
    }
}
