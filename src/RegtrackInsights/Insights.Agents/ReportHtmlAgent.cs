using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Insights.Domain;
using Insights.Presentation;
using Microsoft.Agents.AI;

namespace Insights.Agents;

public interface IReportHtmlAgent
{
    /// <summary>
    /// Renders the approved plan and narrative into one self-contained HTML document
    /// (prompts/05_report_html_fixed_holistic.md - "Way 1"; the original 05_report_html.md,
    /// "compliance_health"'s dynamic/no-fixed-tabs prompt, was removed 2026-09-01). The output is
    /// raw HTML, not JSON - this
    /// agent must be built with <see cref="MafAgentFactory.CreateTextAgent"/>, never
    /// CreateJsonAgent. The 7 output constraints (inline CSS/JS only, zero external references,
    /// no runtime network calls, inline SVG, system fonts only) are NOT enforced here - that is
    /// <see cref="ReportEmitNormalizer"/>'s job, deterministically, after this call returns.
    ///
    /// <paramref name="assertions"/> [BUG FOUND LIVE, same class as the composite-score fabrication
    /// bug] - narrative Prose is free text; a KPI-tile-heavy layout has too many discrete numbers
    /// per tile to safely round-trip through a sentence and back out. This is the SAME typed-fact
    /// source Compose/Narrate already read, given directly to render too, so a number-heavy layout
    /// never has to be parsed back out of prose - it can be cited exactly like Narrate already does.
    ///
    /// <paramref name="locationRows"/> [UPDATED 2026-09-02] Raw per-branch Location rows - the
    /// render agent itself never sees these directly anymore. This method reduces them to 5
    /// aggregate numbers (<see cref="ComputeCoverageStatusCounts"/>, real Flags-based
    /// classification, leaf-only) for the fixed-holistic template's chip/KPI/legend text; the
    /// Coverage store grid itself (one real tile per leaf branch) is generated deterministically,
    /// post-generation, by CoverageGridInjector - not authored by the render agent at all (see
    /// that class's own [BUG FOUND LIVE] note: asked to hand-author up to 177 individual tiles,
    /// it silently sampled instead of completing the population). Null when Location degraded or
    /// the report type does not use it (every prompt except 05_report_html_fixed_holistic.md
    /// ignores this parameter entirely).
    /// </summary>
    Task<AgentCallResult<string>> RenderAsync(
        CompositionPlan plan,
        NarrativeResult narrative,
        IReadOnlyList<Assertion> assertions,
        string tenantName,
        string reportType,
        DateTime generatedAt,
        IReadOnlyList<LocationRow>? locationRows = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IReportHtmlAgent"/>
public sealed partial class MafReportHtmlAgent(AIAgent agent) : IReportHtmlAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<AgentCallResult<string>> RenderAsync(
        CompositionPlan plan,
        NarrativeResult narrative,
        IReadOnlyList<Assertion> assertions,
        string tenantName,
        string reportType,
        DateTime generatedAt,
        IReadOnlyList<LocationRow>? locationRows = null,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                composition_plan = plan,
                narrative,
                assertions,
                tenant_name = tenantName,
                report_type = reportType,
                generated_at = generatedAt,
                coverage_status_counts = ComputeCoverageStatusCounts(locationRows),
            },
            JsonOptions);

        var message = "Render this approved report as a single self-contained HTML document:\n" + payload;

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);

        var html = response.Text;
        if (string.IsNullOrWhiteSpace(html))
            throw new InvalidOperationException("Report HTML agent returned no text.");

        var stripped = StripMarkdownFence(html);

        /*  [BUG FOUND LIVE, 2026-09-01] A malformed/truncated render (no <head> tag at all) used
            to surface one activity later, inside InjectFontActivity/PoppinsFontInjector.Inject -
            by then the orchestrator's ScheduleWithRetry on THIS activity had already returned
            successfully, so a retry could never reach it; the run just failed outright. Checking
            here means the SAME exception PoppinsFontInjector already throws now happens inside
            the activity ScheduleWithRetry wraps, so a transient/one-off bad render gets a fresh
            attempt automatically instead of failing the whole orchestration. Reuses
            PoppinsFontInjector's own regex via HasHeadTag - one definition of "well-formed enough
            to proceed", never two copies that could drift apart.                                 */
        if (!PoppinsFontInjector.HasHeadTag(stripped))
            throw new InvalidOperationException("Report HTML agent returned a document with no <head> tag - malformed or truncated render.");

        var totalTokens = (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0);
        return new AgentCallResult<string>(stripped, totalTokens);
    }

    /// <summary>
    /// [TRAP] Confirmed via a live run (2026-08-20): GPT-5.2 wrapped a fully correct HTML
    /// document in a markdown code fence (```html ... ```), even though the prompt asks for a
    /// raw document and never mentions fencing. ReportEmitNormalizer's checks all search WITHIN
    /// the string, so they still passed - the DOCTYPE and closing tag both existed, just with
    /// stray backtick lines wrapped around them, which a real browser would render as visible
    /// garbage text before/after the report. This is a mechanical formatting habit, not a
    /// content problem - same "don't fail over something this predictable" reasoning as the free
    /// digest's fallback path - so it is stripped defensively here, not treated as a refusal.
    /// The normalizer's rule 1 was separately tightened to verify the document actually starts/
    /// ends correctly, as a second net for anything this stripping does not catch.
    /// </summary>
    internal static string StripMarkdownFence(string text)
    {
        var trimmed = text.Trim();
        var match = MarkdownFenceToken().Match(trimmed);
        return match.Success ? match.Groups["body"].Value.Trim() : trimmed;
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] sql/05_dimension_location.sql's own #rows output covers BOTH
    /// leaf and intermediate (rollup) branches - LocationRows was being threaded into the Coverage
    /// tile grid whole, so a live render used all 177 of tenant 29's branches (leaf and
    /// intermediate together) as if every one were a leaf store. The real reference design
    /// (docs/PAID_TIER_SAMPLE_REFERENCE.md Sec.3.4: "leaf_stores | 632 - leaf nodes only") and the
    /// real Angular reference component (one leaf store per grid tile) both require leaf-only.
    /// Scoped to just this render call's own payload, not the shared LocationRows the orchestrator
    /// also threads to ComputeScoreActivity - the composite score's coverage/backlog math
    /// intentionally still sees the full estate, matching this file's own
    /// "instances_on_intermediate_nodes" data-quality note (instances held directly on
    /// intermediate nodes are real and counted there, just never shown as their own grid tile).
    /// </summary>
    internal static IReadOnlyList<LocationRow>? FilterToLeafStores(IReadOnlyList<LocationRow>? rows) =>
        rows?.Where(r => r.NodeType == EntityNodeType.Leaf).ToList();

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] Replaces the old location_rows-in-the-prompt payload (see
    /// FilterToLeafStores's own [BUG FOUND LIVE] note for the leaf-only half of this story). Once
    /// CoverageGridInjector generates the grid deterministically and CoverageScriptInjector's own
    /// script fills the detail panel from the injected tiles' own data-* attributes, the render
    /// agent has no remaining real use for individual branch rows - only these 5 aggregate
    /// numbers, for the chip/KPI/legend text. Reuses LocationCoverageClassifier so this can never
    /// classify a branch differently than the grid injector does.
    /// </summary>
    internal static CoverageStatusCounts? ComputeCoverageStatusCounts(IReadOnlyList<LocationRow>? rows) =>
        FilterToLeafStores(rows) is { } leafRows ? LocationCoverageClassifier.ComputeCounts(leafRows) : null;

    [GeneratedRegex(@"\A```(?:html)?\s*\r?\n(?<body>.*?)\r?\n?```\z", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownFenceToken();
}

// [REMOVED 2026-09-01] IFixedHolisticReportHtmlAgent/FixedHolisticReportHtmlAgent lived here
// briefly - a marker interface + delegating wrapper so DI could register a second IReportHtmlAgent
// (05_report_html_fixed_holistic.md) alongside the original (05_report_html.md). The original
// ("compliance_health", no fixed tabs) was removed the same session, leaving one agent again -
// see PaidReportAgentsRegistration.cs and RenderHtmlActivity.cs's own notes.
