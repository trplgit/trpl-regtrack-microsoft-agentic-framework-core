using DurableTask.Core;
using Insights.Agents;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record RenderHtmlInput(
    CompositionPlan Plan, NarrativeResult Narrative, IReadOnlyList<Assertion> Assertions, string TenantName, string ReportType, DateTime GeneratedAt,
    LlmCallPriority Priority = LlmCallPriority.Interactive,
    IReadOnlyList<LocationRow>? LocationRows = null);
public sealed record RenderHtmlOutput(string Html, long TotalTokens);

/// <summary>
/// Node 7, runs AFTER the publish gate (spec 3) - no point rendering a refused narrative.
///
/// [REMOVED 2026-09-01] Briefly took two agents (plain + fixed-holistic) and picked between them
/// by input.ReportType - the plain "compliance_health"/05_report_html.md render path (no fixed
/// tabs, composition-agent-decided structure) was removed the same session it was added
/// alongside, so there is only one render agent again. See PaidReportAgentsRegistration.cs's own
/// note on IReportHtmlAgent for what it is now built from. Composition (ComposeActivity et al.)
/// still branches on ReportType in InsightsReportOrchestrator and was deliberately left in place -
/// if "compliance_health" is ever actually requested again, it will still run dynamic LLM
/// composition, then render through this now-single, FIXED-HOLISTIC-shaped prompt, which expects
/// FixedHolisticComposition's exact 6-block plan. A dynamically-composed plan will not match that
/// shape - flagged here, not silently fixed, since the composition side was explicitly kept as-is.
/// </summary>
public sealed class RenderHtmlActivity(IReportHtmlAgent htmlAgent) : AsyncTaskActivity<RenderHtmlInput, RenderHtmlOutput>
{
    protected override Task<RenderHtmlOutput> ExecuteAsync(TaskContext context, RenderHtmlInput input) => RunAsync(input);

    internal async Task<RenderHtmlOutput> RunAsync(RenderHtmlInput input)
    {
        using var _priority = LlmCallPriorityContext.Push(input.Priority);
        var result = await htmlAgent.RenderAsync(
            input.Plan, input.Narrative, input.Assertions, input.TenantName, input.ReportType, input.GeneratedAt,
            input.LocationRows, CancellationToken.None);
        return new RenderHtmlOutput(result.Value, result.TotalTokens);
    }
}
