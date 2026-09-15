using DurableTask.Core;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record InjectCoverageCssInput(string Html);
public sealed record InjectCoverageCssOutput(string Html);

/// <summary>
/// Node 8d, runs right after InjectCoverageGridActivity. Wraps CoverageCssInjector.Inject - see
/// that class's own [BUG FOUND LIVE] note: tiles rendered as unfilled outline boxes and the
/// detail pill had no colour at all because the render agent's own "declare these rules verbatim"
/// CSS silently dropped or malformed the colour declarations. The CSS is 100% static - no reason
/// to gamble on the render agent copying it correctly every run.
/// </summary>
public sealed class InjectCoverageCssActivity : AsyncTaskActivity<InjectCoverageCssInput, InjectCoverageCssOutput>
{
    protected override Task<InjectCoverageCssOutput> ExecuteAsync(TaskContext context, InjectCoverageCssInput input) => RunAsync(input);

    internal Task<InjectCoverageCssOutput> RunAsync(InjectCoverageCssInput input) =>
        Task.FromResult(new InjectCoverageCssOutput(CoverageCssInjector.Inject(input.Html)));
}
