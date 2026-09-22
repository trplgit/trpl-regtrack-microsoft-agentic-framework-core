using DurableTask.Core;
using Insights.Agents;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record AnalyzeAndNarrateInput(
    CompositionPlan Plan, IReadOnlyList<Assertion> Assertions, IReadOnlyList<Finding> Findings,
    string DimensionName, string DimensionRowsJson, string? DimensionControlTotalsJson,
    NarrativeResult? PreviousNarrative, IReadOnlyList<NarrativeReflectionIssue>? Issues,
    LlmCallPriority Priority = LlmCallPriority.Interactive,
    string? ReqId = null);

public sealed record AnalyzeAndNarrateOutput(NarrativeResult Narrative, long TotalTokens);

/// <summary>
/// [ADDED 2026-09-20] Node 6 alternate - v2 of NarrateActivity, wired in behind
/// InsightsReportOrchestrationInput.UseAnalystNarrative for exactly the 5 freehand dimensions (see
/// that field's own doc comment and docs/superpowers/specs/2026-09-20-narrative-analyst-agent-design.md).
/// One call does both Narrate's and Reflect's jobs - self-reflection happens inside
/// prompts/v2/03_narrative_analyst.md itself, so the orchestrator's reflection loop is skipped
/// entirely on this path, not run with a trivial single iteration.
/// </summary>
public sealed class AnalyzeAndNarrateActivity(IAnalystNarrativeAgent analystAgent, IAgentReasoningRecorder? reasoningRecorder = null)
    : AsyncTaskActivity<AnalyzeAndNarrateInput, AnalyzeAndNarrateOutput>
{
    protected override Task<AnalyzeAndNarrateOutput> ExecuteAsync(TaskContext context, AnalyzeAndNarrateInput input) =>
        RunAsync(input, context.OrchestrationInstance.InstanceId);

    internal async Task<AnalyzeAndNarrateOutput> RunAsync(AnalyzeAndNarrateInput input, string? runId = null)
    {
        (NarrativeResult, IReadOnlyList<NarrativeReflectionIssue>)? revision =
            input.PreviousNarrative is not null && input.Issues is not null ? (input.PreviousNarrative, input.Issues) : null;

        using var _priority = LlmCallPriorityContext.Push(input.Priority);
        using var _session = LangfuseSessionContext.Push(input.ReqId ?? runId);
        var result = await analystAgent.AnalyzeAndNarrateAsync(
            input.Plan, input.Assertions, input.Findings, input.DimensionName, input.DimensionRowsJson,
            input.DimensionControlTotalsJson, revision, CancellationToken.None);

        // Same best-effort stance as NarrateActivity/ReflectOnNarrativeActivity's own recorder call.
        if (runId is not null)
        {
            try
            {
                await (reasoningRecorder ?? IAgentReasoningRecorder.Null).RecordAsync(runId, "analyze_and_narrate", result.ReasoningSummary);
            }
            catch
            {
                // Reasoning capture is not worth a response.
            }
        }

        return new AnalyzeAndNarrateOutput(result.Value, result.TotalTokens);
    }
}
