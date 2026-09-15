using DurableTask.Core;
using Insights.Domain;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record InjectForwardLookInput(
    string Html,
    ForwardRiskControlTotals? ForwardRiskControlTotals,
    ForwardPipelineControlTotals? ForwardPipelineControlTotals = null,
    IReadOnlyList<ForwardPipelineRow>? ForwardPipelineRows = null);
public sealed record InjectForwardLookOutput(string Html);

/// <summary>
/// Node 8g, runs right after InjectBacklogAgeBarCssActivity. Wraps ForwardLookInjector.Inject -
/// renders the WHOLE Tab 5 pane body: the `.di-kpi--fwd` card, the "N due in the next 90 days"
/// figure, the carried_forward / clean_at_risk / healthy segment breakdown (the "already late"
/// figure the real live demo shows) from ForwardRisk's control totals, AND the 5 day-window
/// bucket chart from ForwardPipeline's rows. None of those are typed assertions, so none can
/// reach the render agent through its assertion-only payload - deterministic injection, same
/// treatment as the Coverage pane and the Tab 2 age-bar. [WIDENED 2026-09-10] Was just the
/// 3-segment breakdown; the render agent kept dropping the rest of the pane.
/// </summary>
public sealed class InjectForwardLookActivity : AsyncTaskActivity<InjectForwardLookInput, InjectForwardLookOutput>
{
    protected override Task<InjectForwardLookOutput> ExecuteAsync(TaskContext context, InjectForwardLookInput input) => RunAsync(input);

    internal Task<InjectForwardLookOutput> RunAsync(InjectForwardLookInput input) =>
        Task.FromResult(new InjectForwardLookOutput(ForwardLookInjector.Inject(
            input.Html, input.ForwardRiskControlTotals, input.ForwardPipelineControlTotals, input.ForwardPipelineRows)));
}
