using DurableTask.Core;
using Insights.Domain;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record InjectCoverageGridInput(string Html, IReadOnlyList<LocationRow>? LocationRows);
public sealed record InjectCoverageGridOutput(string Html);

/// <summary>
/// Node 8b (runs BEFORE InjectCoverageScriptActivity - the driving script only wires itself when
/// it finds a real di-covgrid/di-covtile grid already present, so the grid must exist first).
/// Wraps CoverageGridInjector.Inject - see that class's own [BUG FOUND LIVE] note: the render
/// agent was asked to hand-author one tile per real leaf branch (up to 177) and silently sampled
/// instead of completing the population, exactly the reasoning that already moved the font and
/// the driving script to deterministic post-generation injection.
/// </summary>
public sealed class InjectCoverageGridActivity : AsyncTaskActivity<InjectCoverageGridInput, InjectCoverageGridOutput>
{
    protected override Task<InjectCoverageGridOutput> ExecuteAsync(TaskContext context, InjectCoverageGridInput input) => RunAsync(input);

    internal Task<InjectCoverageGridOutput> RunAsync(InjectCoverageGridInput input) =>
        Task.FromResult(new InjectCoverageGridOutput(CoverageGridInjector.Inject(input.Html, input.LocationRows)));
}
