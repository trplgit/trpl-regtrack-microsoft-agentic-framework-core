using DurableTask.Core;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record InjectFontInput(string Html);
public sealed record InjectFontOutput(string Html);

/// <summary>
/// Node 8a, runs between RenderHtml (and PartialDimensionPlaceholder's placeholder insertion)
/// and the first Normalize call. Wraps PoppinsFontInjector.Inject - see that class's doc
/// comment for why this exists as a deterministic post-generation step rather than something
/// the render agent does itself.
///
/// Deliberately its OWN activity, not folded into RenderHtmlActivity or NormalizeActivity:
/// RenderHtmlActivity is the LLM call boundary (CLAUDE.md 6 - LLM calls live in their own
/// activity), and NormalizeActivity only VALIDATES, it never transforms html (see its doc
/// comment / RunAsync body) - this is the one step that actually changes the document, so it
/// gets its own name in the orchestrator's sequence and its own place to fail loudly.
/// </summary>
public sealed class InjectFontActivity : AsyncTaskActivity<InjectFontInput, InjectFontOutput>
{
    protected override Task<InjectFontOutput> ExecuteAsync(TaskContext context, InjectFontInput input) => RunAsync(input);

    internal Task<InjectFontOutput> RunAsync(InjectFontInput input) =>
        Task.FromResult(new InjectFontOutput(PoppinsFontInjector.Inject(input.Html)));
}
