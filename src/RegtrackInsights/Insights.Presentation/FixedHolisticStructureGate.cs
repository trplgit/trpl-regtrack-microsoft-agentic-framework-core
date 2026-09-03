using System.Text.RegularExpressions;
using Insights.Domain;
using System.Linq;

namespace Insights.Presentation;

/// <summary>
/// Deterministic post-render gate for two structural invariants of the "Holistic Insights" score
/// hero + fixed-tab template (05_report_html_holistic.md / 05_report_html_fixed_holistic.md) -
/// both invariants hold regardless of tenant data (CLAUDE.md Sec.11's "structural invariant, must
/// THROW" category, not a data-sanity warning), and both were found violated by a real render:
///
/// 1. The score-components row (.di-components) always has exactly 7 .di-comp cards, real or
///    muted - never fewer. A real render silently dropped 3 (Timeliness, Licence, Evidence)
///    instead of rendering them muted, despite the prompt already saying "never omit the card".
///    [FIX, 2026-09-02] The check itself used to look for &lt;article class="di-component"&gt; -
///    a fictional convention matching neither the real product markup nor the prompt's own hero
///    section (which correctly copies detailed-insights.component.html's real &lt;div class="di-comp"&gt;
///    verbatim) - refused every real, correct render outright. See DiComponentCardToken's own note.
/// 2. A pane marked data-blocked="true" (the fixed template's entirely-blocked tabs, e.g. Actions/
///    Forward look when no real data source exists) must show a "0" tab-nav badge - never a
///    literal carried over from the real product's mock demo data. A real render badged Actions
///    "6" while the pane itself said "This section has no real data source yet in this run".
///
/// 3. The Coverage pane (di-pane-3 - fixed position, never data-blocked, Location dimension
///    always has real data) must contain the real interactive store grid (di-covgrid, at least
///    one di-covtile, and its driving &lt;script&gt;) - never a plainer KPI-only summary
///    substituted in its place. Found live twice, two different ways: once with the grid present
///    but its &lt;script&gt; silently stripped by DOMPurify's default profile (see
///    DomPurifySanitizer's own [BUG FOUND LIVE] note - fixed there, ADD_TAGS: ['script']), and
///    once with the LLM skipping the whole grid shape and rendering a single di-kpi--span12
///    summary card instead (zero di-cov* classes anywhere) - same failure family as invariant 1
///    (prose instruction, not always followed), just for Coverage's markup shape rather than
///    score-card count.
///
/// Regex-based, not a real HTML/CSS parser - same deliberate posture as ReportEmitNormalizer
/// (see its own doc comment): a fast, auditable static check for the two specific violation
/// classes found live, not a general-purpose HTML validator. Both checks are no-ops (approve) on
/// a document that has neither a score hero nor any data-blocked pane - e.g. the dynamic
/// (non-fixed-template) report shape - so this gate is safe to run unconditionally on every
/// rendered report, not just the fixed-holistic variant.
/// </summary>
public static partial class FixedHolisticStructureGate
{
    private const int RequiredComponentCount = 7;

    public static FixedHolisticStructureResult Evaluate(string html)
    {
        var violations = new List<string>();

        CheckScoreComponentCount(html, violations);
        CheckBlockedTabBadges(html, violations);
        CheckCoveragePaneHasInteractiveGrid(html, violations);

        return violations.Count == 0 ? FixedHolisticStructureResult.Approve : FixedHolisticStructureResult.Refuse(violations);
    }

    /// <summary>
    /// Only enforced when a score hero actually exists (.di-components wrapper present) - a report
    /// with no composite score at all (every dimension degraded) legitimately never renders it.
    /// </summary>
    private static void CheckScoreComponentCount(string html, List<string> violations)
    {
        if (!DiComponentsWrapperToken().IsMatch(html))
            return;

        var count = DiComponentCardToken().Matches(html).Count;
        if (count != RequiredComponentCount)
            violations.Add($"score hero present but found {count} .di-component card(s), expected exactly {RequiredComponentCount} (real or muted, never fewer)");
    }

    /// <summary>
    /// For every pane carrying data-blocked="true", the matching tab-nav badge (di-tab-N's
    /// di-tab__count) must read "0". No blocked panes present is not a violation - this check is a
    /// no-op on the dynamic report shape, which never marks any pane blocked.
    /// </summary>
    private static void CheckBlockedTabBadges(string html, List<string> violations)
    {
        foreach (Match pane in BlockedPaneToken().Matches(html))
        {
            var paneId = pane.Groups["id"].Value;
            var tabNumber = pane.Groups["n"].Value;

            var labelMatch = TabLabelToken(tabNumber).Match(html);
            if (!labelMatch.Success)
                continue; // No matching tab label to check - nothing this gate can verify.

            var countMatch = TabCountSpanToken().Match(labelMatch.Groups["inner"].Value);
            if (!countMatch.Success)
                continue; // Label has no count badge at all - nothing to check.

            var badge = countMatch.Groups["count"].Value;
            if (badge != "0")
                violations.Add($"{paneId} is data-blocked=\"true\" but its tab-nav badge shows \"{badge}\" - a blocked pane has zero real items, the badge must read \"0\"");
        }
    }

    /// <summary>
    /// Only enforced when di-pane-3 actually exists (fixed-holistic-shaped document) and is not
    /// itself data-blocked="true" - see this method's own reasoning at the class doc comment,
    /// item 3. Coverage is fixed-position (di-pane-3) by FixedHolisticComposition's construction,
    /// never chosen dynamically, so hardcoding the pane id here is safe.
    ///
    /// [BUG FOUND LIVE, 2026-09-02] The driving &lt;script&gt; only needs to exist SOMEWHERE in
    /// the document, not nested inside di-pane-3's own &lt;section&gt; - a real render placed it
    /// at the end of &lt;body&gt; instead (ordinary, common placement), and this check originally
    /// scoped its script search to the pane's own inner content only, refusing a perfectly good
    /// render and exhausting every retry into a raw, never-processed fallback - twice. The grid
    /// markup itself (di-covgrid/di-covtile) IS a real structural requirement of the pane and
    /// stays pane-scoped; only the script's location was ever the false assumption.
    /// </summary>
    private static void CheckCoveragePaneHasInteractiveGrid(string html, List<string> violations)
    {
        if (!CoveragePaneOpenToken().IsMatch(html))
            return; // not a fixed-holistic-shaped document, or this pane genuinely is not present - nothing to check.

        if (CoveragePaneBlockedToken().IsMatch(html))
            return; // defensive only - Coverage always has real data in practice, never legitimately blocked.

        var contentMatch = CoveragePaneContentToken().Match(html);
        if (!contentMatch.Success)
            return; // malformed document shape - ReportEmitNormalizer's own rule 1 is what catches that, not this gate.

        var inner = contentMatch.Groups["inner"].Value;
        var hasGrid = inner.Contains("di-covgrid", StringComparison.Ordinal) && inner.Contains("di-covtile", StringComparison.Ordinal);

        if (!hasGrid)
        {
            violations.Add("di-pane-3 (Coverage) has no di-covgrid/di-covtile store grid - a plainer summary was rendered instead of the real interactive grid the prompt requires");
            return;
        }

        // Document-wide, not pane-scoped - see the [BUG FOUND LIVE] note above.
        var hasDrivingScript = AnyScriptContentToken().Matches(html)
            .Any(m => m.Groups["content"].Value.Contains(".di-covmap", StringComparison.Ordinal)
                   || m.Groups["content"].Value.Contains(".di-covtile", StringComparison.Ordinal));
        if (!hasDrivingScript)
            violations.Add("di-pane-3 (Coverage) has the store grid but no driving <script> anywhere in the document - tiles will render but will not be clickable");
    }

    [GeneratedRegex(@"class=""di-components""")]
    private static partial Regex DiComponentsWrapperToken();

    /*  [FIX, 2026-09-02] Was `<article class="di-component...">` - a fictional convention that
        matched neither the real product markup nor either draft of this prompt's own hero section
        (the pre-fix stub used di-scoremini; the real rebuild correctly copies the actual product's
        di-comp verbatim from detailed-insights.component.html, which uses a <div>, not <article>).
        Confirmed live: a real render with the correct real di-comp markup was refused 5/5 times by
        this exact regex never matching anything, reading as "found 0 .di-component card(s)". */
    [GeneratedRegex(@"<div\s+class=""di-comp(?:\s+di-comp--muted)?""")]
    private static partial Regex DiComponentCardToken();

    [GeneratedRegex(@"<section\b[^>]*\bid=""(?<id>di-pane-(?<n>\d+))""[^>]*\bdata-blocked=""true""")]
    private static partial Regex BlockedPaneToken();

    /// <summary>Per-tab-number pattern, built dynamically (a [GeneratedRegex] attribute cannot take an interpolated pattern) - matching only this one tab-N's label, never a neighbour's.</summary>
    private static Regex TabLabelToken(string tabNumber) =>
        new($"<label\\s+for=\"di-tab-{Regex.Escape(tabNumber)}\"[^>]*>(?<inner>.*?)</label>", RegexOptions.Singleline);

    [GeneratedRegex(@"<span\s+class=""di-tab__count\s+tnum"">(?<count>\d+)</span>")]
    private static partial Regex TabCountSpanToken();

    [GeneratedRegex(@"<section\b[^>]*\bid=""di-pane-3""[^>]*>", RegexOptions.Singleline)]
    private static partial Regex CoveragePaneOpenToken();

    [GeneratedRegex(@"<section\b[^>]*\bid=""di-pane-3""[^>]*\bdata-blocked=""true""", RegexOptions.Singleline)]
    private static partial Regex CoveragePaneBlockedToken();

    [GeneratedRegex(@"<section\b[^>]*\bid=""di-pane-3""[^>]*>(?<inner>.*?)</section>", RegexOptions.Singleline)]
    private static partial Regex CoveragePaneContentToken();

    [GeneratedRegex(@"<script\b[^>]*>(?<content>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnyScriptContentToken();
}
