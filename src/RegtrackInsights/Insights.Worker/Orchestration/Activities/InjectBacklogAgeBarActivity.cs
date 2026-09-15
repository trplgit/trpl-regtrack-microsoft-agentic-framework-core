using DurableTask.Core;
using Insights.Domain;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record InjectBacklogAgeBarInput(string Html, IReadOnlyList<BacklogAgingRow>? BacklogAgingRows, BacklogAgingControlTotals? BacklogAgingControlTotals);
public sealed record InjectBacklogAgeBarOutput(string Html);

/// <summary>
/// Node 8d (runs alongside the other deterministic Inject* activities, order-independent from
/// them - it only ever touches the `#di-agebar-root` placeholder inside Tab 2 Card 2, a
/// completely different part of the document from the font/Coverage-grid/Coverage-script/
/// Coverage-CSS pieces). Wraps BacklogAgeBarInjector.Inject - see that class's own [BUG FOUND
/// LIVE] note: confirmed on two consecutive real tenant-29 runs, the render agent silently fell
/// back to the bigNumber-only shape and never rendered the segmented age bar, even with real
/// bucket data available and an explicit instruction to use it.
/// </summary>
public sealed class InjectBacklogAgeBarActivity : AsyncTaskActivity<InjectBacklogAgeBarInput, InjectBacklogAgeBarOutput>
{
    protected override Task<InjectBacklogAgeBarOutput> ExecuteAsync(TaskContext context, InjectBacklogAgeBarInput input) => RunAsync(input);

    internal Task<InjectBacklogAgeBarOutput> RunAsync(InjectBacklogAgeBarInput input) =>
        Task.FromResult(new InjectBacklogAgeBarOutput(BacklogAgeBarInjector.Inject(input.Html, input.BacklogAgingRows, input.BacklogAgingControlTotals)));
}
