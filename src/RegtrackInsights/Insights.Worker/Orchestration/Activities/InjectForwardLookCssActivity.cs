using DurableTask.Core;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record InjectForwardLookCssInput(string Html);
public sealed record InjectForwardLookCssOutput(string Html);

/// <summary>
/// Node 8h, runs right after InjectForwardLookActivity. Wraps ForwardLookCssInjector.Inject -
/// see that class's own [BUG FOUND LIVE] note: once Tab 5 became "fully injected, author only the
/// shell", the render agent stopped emitting the Tab 5 CSS block, so the injected `.di-fwd`
/// bucket chart rendered with zero height and its axis labels ran together. The CSS is 100%
/// static - injected markup needs injected CSS.
/// </summary>
public sealed class InjectForwardLookCssActivity : AsyncTaskActivity<InjectForwardLookCssInput, InjectForwardLookCssOutput>
{
    protected override Task<InjectForwardLookCssOutput> ExecuteAsync(TaskContext context, InjectForwardLookCssInput input) => RunAsync(input);

    internal Task<InjectForwardLookCssOutput> RunAsync(InjectForwardLookCssInput input) =>
        Task.FromResult(new InjectForwardLookCssOutput(ForwardLookCssInjector.Inject(input.Html)));
}
