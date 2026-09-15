using DurableTask.Core;
using Insights.Agents;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record VisionQaInput(string Html);
public sealed record VisionQaOutput(bool HasVisualDefect, string? Issue, long TotalTokens);

/// <summary>
/// [ADDED 2026-09-14] Real vision-model gate, runs inside the SAME render-retry loop
/// (InsightsReportOrchestrator) the structure gate already uses - a real visual defect (overlap,
/// content escaping its container, broken/collapsed structure) throws OrchestrationRefusedException
/// the same way a structure violation does, so it retries the whole render rather than shipping a
/// broken document. Unlike PlaywrightQaActivity (deliberately advisory-only, unchanged), this one
/// gates - narrow scope is what makes that safe (see prompts/06_vision_qa.md's own binding "what
/// you are NOT checking for" list).
///
/// Captures its OWN screenshots via IReportQaRunner rather than sharing PlaywrightQaActivity's -
/// two real screenshot passes per attempt, deliberately: keeps this activity's gating concern
/// completely independent of the advisory one, so nothing about tightening this check can ever
/// change PlaywrightQaActivity's own long-standing "never blocks" contract.
/// </summary>
public sealed class VisionQaActivity(IReportQaRunner qaRunner, IVisionQaAgent visionAgent)
    : AsyncTaskActivity<VisionQaInput, VisionQaOutput>
{
    protected override Task<VisionQaOutput> ExecuteAsync(TaskContext context, VisionQaInput input) => RunAsync(input);

    internal async Task<VisionQaOutput> RunAsync(VisionQaInput input)
    {
        var qaResult = await qaRunner.RunAsync(input.Html, CancellationToken.None);
        var result = await visionAgent.ReviewAsync(qaResult.Screenshots, CancellationToken.None);
        return new VisionQaOutput(result.Value.HasVisualDefect, result.Value.Issue, result.TotalTokens);
    }
}
