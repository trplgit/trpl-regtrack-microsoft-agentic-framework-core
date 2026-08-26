using DurableTask.Core;
using Insights.Agents;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record NarrateInput(
    CompositionPlan Plan, IReadOnlyList<Assertion> Assertions, IReadOnlyList<Finding> Findings,
    NarrativeResult? PreviousNarrative, IReadOnlyList<NarrativeReflectionIssue>? Issues,
    LlmCallPriority Priority = LlmCallPriority.Interactive);

public sealed record NarrateOutput(NarrativeResult Narrative, long TotalTokens);

/// <summary>Node 6.</summary>
public sealed class NarrateActivity(INarrativeAgent narrativeAgent) : AsyncTaskActivity<NarrateInput, NarrateOutput>
{
    protected override Task<NarrateOutput> ExecuteAsync(TaskContext context, NarrateInput input) => RunAsync(input);

    internal async Task<NarrateOutput> RunAsync(NarrateInput input)
    {
        (NarrativeResult, IReadOnlyList<NarrativeReflectionIssue>)? revision =
            input.PreviousNarrative is not null && input.Issues is not null ? (input.PreviousNarrative, input.Issues) : null;

        using var _priority = LlmCallPriorityContext.Push(input.Priority);
        var result = await narrativeAgent.NarrateAsync(input.Plan, input.Assertions, input.Findings, revision, CancellationToken.None);
        return new NarrateOutput(result.Value, result.TotalTokens);
    }
}
