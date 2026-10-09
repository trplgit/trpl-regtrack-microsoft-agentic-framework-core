using DurableTask.Core;
using Insights.Presentation;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record InteractiveTileQaInput(string Html);
public sealed record InteractiveTileQaOutput(IReadOnlyList<TileFinding> Findings);

/// <summary>
/// [ADDED 2026-10-07] Runs InteractiveTileChecker against the final rendered HTML, after the
/// existing structure/vision gates both pass - see InsightsReportOrchestrator's own insertion point
/// doc comment. Does not itself decide retry/patch/refuse behaviour; that is the orchestrator's new
/// patch loop (design spec Section 4), which reads this activity's Findings list.
/// </summary>
public sealed class InteractiveTileQaActivity(IInteractiveTileChecker checker, ILogger<InteractiveTileQaActivity> logger)
    : AsyncTaskActivity<InteractiveTileQaInput, InteractiveTileQaOutput>
{
    protected override Task<InteractiveTileQaOutput> ExecuteAsync(TaskContext context, InteractiveTileQaInput input) => RunAsync(input);

    internal async Task<InteractiveTileQaOutput> RunAsync(InteractiveTileQaInput input)
    {
        var findings = await checker.FindIssuesAsync(input.Html);
        if (findings.Count > 0)
            logger.LogWarning("Interactive tile QA found {Count} issue(s).", findings.Count);
        return new InteractiveTileQaOutput(findings);
    }
}
