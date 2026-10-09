using DurableTask.Core;
using Insights.Agents;
using Insights.Presentation;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record PatchRenderInput(string Html, IReadOnlyList<TileFinding> Findings);
public sealed record PatchRenderOutput(string Html, long TotalTokens);

/// <summary>[ADDED 2026-10-07] Thin DTFx wrapper over IPatchRenderAgent - see that interface's own
/// doc comment and InsightsReportOrchestrator's patch loop for how this fits the pipeline.</summary>
public sealed class PatchRenderActivity(IPatchRenderAgent agent, ILogger<PatchRenderActivity> logger)
    : AsyncTaskActivity<PatchRenderInput, PatchRenderOutput>
{
    protected override Task<PatchRenderOutput> ExecuteAsync(TaskContext context, PatchRenderInput input) => RunAsync(input);

    internal async Task<PatchRenderOutput> RunAsync(PatchRenderInput input)
    {
        var result = await agent.PatchAsync(input.Html, input.Findings);
        logger.LogInformation("Patch render attempted for {Count} finding(s), billed {Tokens} tokens.", input.Findings.Count, result.TotalTokens);
        return new PatchRenderOutput(result.Value, result.TotalTokens);
    }
}
