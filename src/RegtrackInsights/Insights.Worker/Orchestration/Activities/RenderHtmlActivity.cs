using DurableTask.Core;
using Insights.Agents;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record RenderHtmlInput(
    CompositionPlan Plan, NarrativeResult Narrative, IReadOnlyList<Assertion> Assertions, string TenantName, string ReportType, DateTime GeneratedAt,
    LlmCallPriority Priority = LlmCallPriority.Interactive,
    IReadOnlyList<LocationRow>? LocationRows = null,
    IReadOnlyDictionary<string, string>? DimensionRowsJson = null);
public sealed record RenderHtmlOutput(string Html, long TotalTokens);

/// <summary>
/// Node 7, runs AFTER the publish gate (spec 3) - no point rendering a refused narrative.
///
/// [REMOVED 2026-09-01, REINTRODUCED 2026-09-08] Briefly took two agents (plain + fixed-holistic)
/// and picked between them by input.ReportType - the plain "compliance_health"/05_report_html.md
/// render path (no fixed tabs, composition-agent-decided structure) was removed the same session
/// it was added alongside, leaving one agent for a while. Two-agent selection is back for a
/// genuinely different reason this time: DimensionSelectionComposition.ReportType
/// ("dimension_selection") needs a render prompt with NO fixed 6-tab hero (a 1-3 dimension
/// selection cannot be forced into a template built assuming all fourteen are present - Coverage
/// fixed at pane 3, etc.), so it gets its own agent/prompt
/// (05_report_html_dimension_selection.md) - see PaidReportAgentsRegistration.cs. Keyed by
/// ReportType via a plain dictionary rather than .NET's keyed-DI attributes, to stay explicit and
/// avoid depending on a framework feature this codebase had not already exercised anywhere else.
/// Composition (ComposeActivity et al.) still branches on ReportType in InsightsReportOrchestrator,
/// unchanged by this - if "compliance_health" is ever actually requested, dynamic LLM composition
/// still runs, then renders through whichever agent htmlAgentsByReportType has registered for that
/// key today (fixed_holistic's), which still expects FixedHolisticComposition's exact 6-block plan
/// and will not match a dynamically-composed one - same pre-existing gap, not addressed here.
/// </summary>
public sealed class RenderHtmlActivity(IReadOnlyDictionary<string, IReportHtmlAgent> htmlAgentsByReportType)
    : AsyncTaskActivity<RenderHtmlInput, RenderHtmlOutput>
{
    protected override Task<RenderHtmlOutput> ExecuteAsync(TaskContext context, RenderHtmlInput input) => RunAsync(input);

    internal async Task<RenderHtmlOutput> RunAsync(RenderHtmlInput input)
    {
        if (!htmlAgentsByReportType.TryGetValue(input.ReportType, out var htmlAgent))
            throw new InvalidOperationException(
                $"No render agent registered for ReportType '{input.ReportType}'. Registered: {string.Join(", ", htmlAgentsByReportType.Keys)}.");

        using var _priority = LlmCallPriorityContext.Push(input.Priority);
        var result = await htmlAgent.RenderAsync(
            input.Plan, input.Narrative, input.Assertions, input.TenantName, input.ReportType, input.GeneratedAt,
            input.LocationRows, input.DimensionRowsJson, CancellationToken.None);
        return new RenderHtmlOutput(result.Value, result.TotalTokens);
    }
}
