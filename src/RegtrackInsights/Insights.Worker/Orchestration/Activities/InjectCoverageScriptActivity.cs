using DurableTask.Core;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record InjectCoverageScriptInput(string Html);
public sealed record InjectCoverageScriptOutput(string Html);

/// <summary>
/// Node 8b, runs right after InjectFontActivity (node 8a) and before the first Normalize call -
/// same deliberate-own-activity reasoning InjectFontActivity's own doc comment gives (this is the
/// one step that actually changes the document; NormalizeActivity only validates). Wraps
/// CoverageScriptInjector.Inject - see that class's own [BUG FOUND LIVE] note for why the
/// Coverage-tile driving script is injected deterministically rather than left to the render
/// agent: three separate real failure modes (DOMPurify's default strip, DOMPurify's tag-shaped-
/// content strip, the render agent simply omitting it on a given attempt) all vanish once the
/// script is never something the render agent has to get right.
/// </summary>
public sealed class InjectCoverageScriptActivity : AsyncTaskActivity<InjectCoverageScriptInput, InjectCoverageScriptOutput>
{
    protected override Task<InjectCoverageScriptOutput> ExecuteAsync(TaskContext context, InjectCoverageScriptInput input) => RunAsync(input);

    internal Task<InjectCoverageScriptOutput> RunAsync(InjectCoverageScriptInput input) =>
        Task.FromResult(new InjectCoverageScriptOutput(CoverageScriptInjector.Inject(input.Html)));
}
