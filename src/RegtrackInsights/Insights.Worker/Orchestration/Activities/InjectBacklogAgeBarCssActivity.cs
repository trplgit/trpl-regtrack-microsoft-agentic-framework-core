using DurableTask.Core;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record InjectBacklogAgeBarCssInput(string Html);
public sealed record InjectBacklogAgeBarCssOutput(string Html);

/// <summary>
/// Node 8f, runs right after InjectBacklogAgeBarActivity. Wraps BacklogAgeBarCssInjector.Inject -
/// see that class's own [BUG FOUND LIVE] note: the deterministically-injected age-bar markup had
/// no CSS anywhere in the pipeline (not in the render agent's prompt, not injected elsewhere), so
/// it rendered with zero colour and no bar chrome. The CSS is 100% static - no reason to gamble
/// on the render agent authoring it when it already isn't trusted to author the markup it
/// decorates.
/// </summary>
public sealed class InjectBacklogAgeBarCssActivity : AsyncTaskActivity<InjectBacklogAgeBarCssInput, InjectBacklogAgeBarCssOutput>
{
    protected override Task<InjectBacklogAgeBarCssOutput> ExecuteAsync(TaskContext context, InjectBacklogAgeBarCssInput input) => RunAsync(input);

    internal Task<InjectBacklogAgeBarCssOutput> RunAsync(InjectBacklogAgeBarCssInput input) =>
        Task.FromResult(new InjectBacklogAgeBarCssOutput(BacklogAgeBarCssInjector.Inject(input.Html)));
}
