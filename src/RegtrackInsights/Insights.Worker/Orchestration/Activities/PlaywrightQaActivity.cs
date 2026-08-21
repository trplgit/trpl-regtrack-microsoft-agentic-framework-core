using DurableTask.Core;
using Insights.Domain;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record PlaywrightQaInput(string Html);
public sealed record PlaywrightQaOutput(ReportQaResult Result);

/// <summary>
/// Node 11 - cosmetic only (CLAUDE.md/spec [TRAP]: malicious markup renders perfectly in a
/// headless browser). Never throws OrchestrationRefusedException - a QA issue is logged for item
/// 16/17 to surface later, not a publish blocker.
/// </summary>
public sealed class PlaywrightQaActivity(IReportQaRunner qaRunner) : AsyncTaskActivity<PlaywrightQaInput, PlaywrightQaOutput>
{
    protected override Task<PlaywrightQaOutput> ExecuteAsync(TaskContext context, PlaywrightQaInput input) => RunAsync(input);

    internal async Task<PlaywrightQaOutput> RunAsync(PlaywrightQaInput input)
    {
        var result = await qaRunner.RunAsync(input.Html, CancellationToken.None);
        return new PlaywrightQaOutput(result);
    }
}
