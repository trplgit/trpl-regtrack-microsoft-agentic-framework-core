using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-10-01] Deterministically wraps the fixed_holistic Hero's composite score (the
/// `.di-donut__num`) and each real score component's own number (`.di-comp__row`'s `b.tnum`) as an
/// inline hover-link to a "how this is worked out" panel - the SAME `.hr`/`.pf` mechanism every
/// dimension_selection render prompt now requires (section 7b), reusing its already-bugfixed CSS
/// and script verbatim via <see cref="NumberFormulaInjector.SharedStyle"/>/
/// <see cref="NumberFormulaInjector.SharedScript"/> rather than a second copy that could drift.
///
/// [WHY CODE, NOT PROMPT] `05_report_html_fixed_holistic_v3.md`'s own Hero section (line ~152) is
/// copied VERBATIM from the real Angular product's own component markup - asking the render LLM to
/// also wrap the score numbers in its own hover-link markup risks the exact same failure class
/// already proven twice this project (Licence's original attempt, and this file's own sibling
/// `NumberFormulaInjector`'s doc comment on 3 failed freehand trials): a free-text instruction
/// competing against a "copy this exactly" instruction for the same characters. The Hero's shape is
/// 100% fixed and known in advance (unlike freehand prose), so deterministic post-render wrapping
/// is strictly safer here, not just more convenient.
///
/// Scope, this pass: the Hero composite score + its score components only (the highest-visibility
/// "every tile has a score" surface). Tab 1 Snapshot/Tab 2/Tab 4 tile-level wrapping is a planned
/// follow-up pass, not yet built.
/// </summary>
public static class EntityScoreFormulaInjector
{
    public sealed record ScoreComponent(string Name, decimal Score, decimal Weight);

    private static readonly Regex DonutPattern = new(
        """<div class="di-donut__num tnum">(\d+(?:\.\d+)?)</div>""",
        RegexOptions.Compiled);

    // [FIX - found live against a real rendered fixed_holistic report] The real Hero markup shows
    // the component's weight as a PERCENTAGE inside <small> ("Weight 20%"), not the decimal weight
    // the formula span below it uses ("0.0 x 0.20") - an earlier version of this pattern required
    // capturing a decimal there and silently matched zero real components as a result. This
    // injector already has the real weight from `components` (the real ComparatorValue), so it
    // never needs to parse weight out of the HTML at all - <small>[^<]*</small> tolerates whatever
    // the render agent wrote there.
    private static readonly Regex ComponentPattern = new(
        """<div class="di-comp">\s*<div class="di-comp__name">([^<]+)<small>[^<]*</small></div>.*?<div class="di-comp__row"><b class="tnum">(\d+(?:\.\d+)?)</b>.*?</div>\s*</div>""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static string Inject(string html, decimal? compositeScore, IReadOnlyList<ScoreComponent> components)
    {
        if (compositeScore is null || string.IsNullOrEmpty(html))
            return html;

        var nextId = 0;
        var wrapped = false;

        var donutMatch = DonutPattern.Match(html);
        if (donutMatch.Success)
        {
            var id = $"pf-composite-{nextId++}";
            // The REAL displayed text (donutMatch.Groups[1]), never a reformatted value - this
            // guarantees the trigger label stays byte-identical to what the render agent actually
            // wrote (matters because components.Score has its own precision, e.g. "94.7" vs "0.0",
            // that can differ in trailing-zero formatting from a value recomputed in code).
            var displayText = donutMatch.Groups[1].Value;
            var panel = BuildCompositePanel(id, displayText, components);
            html = html.Remove(donutMatch.Index, donutMatch.Length)
                .Insert(donutMatch.Index, $"""<div class="di-donut__num tnum">{panel}</div>""");
            wrapped = true;
        }

        html = ComponentPattern.Replace(html, m =>
        {
            var name = m.Groups[1].Value.Trim();
            var component = components.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (component is null)
                return m.Value; // Real component with no matching score data this run - leave the tile bare, never guess.

            var id = $"pf-comp-{nextId++}";
            var displayText = m.Groups[2].Value;
            var wrappedScore = BuildComponentPanel(id, displayText, component);
            wrapped = true;
            return m.Value.Replace($"""<b class="tnum">{displayText}</b>""", $"""<b class="tnum">{wrappedScore}</b>""");
        });

        if (!wrapped)
            return html;

        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose < 0)
            return html;

        return html.Insert(bodyClose, NumberFormulaInjector.SharedStyle + NumberFormulaInjector.SharedScript);
    }

    // [FIX - found live, user caught it reading the actual popup] The box stated the two operands
    // (score, weight) but never the PRODUCT - a reader had to do "46.0 x 0.15" by hand to get the
    // 6.9 the box was supposedly explaining. "HOW IT IS CALCULATED" that stops short of the answer
    // is not calculated at all. Every row/panel below now states its own real result explicitly.
    private static decimal Contribution(ScoreComponent c) => decimal.Round(c.Score * c.Weight, 2);

    private static string BuildCompositePanel(string id, string displayText, IReadOnlyList<ScoreComponent> components)
    {
        var rows = string.Concat(components.Select(c =>
            $"""<span class="pf-diff-row"><span class="pf-diff-value">{c.Score} &times; {c.Weight} = {Contribution(c)}</span><span class="pf-diff-label">{System.Net.WebUtility.HtmlEncode(c.Name)}</span></span>"""));

        return $"""
            <span class="hr">
              <input type="checkbox" class="hr-toggle" id="{id}" aria-label="How this score is worked out">
              <label for="{id}" class="hr-i pf">{displayText}</label>
              <span class="hr-panel pf-panel" role="dialog" aria-label="How this score is worked out">
                <label for="{id}" class="hr-close" aria-label="Close">&times;</label>
                <span class="hr-title pf-title">Compliance-health score - {displayText} / 100</span>
                <span class="hr-intro pf-intro">A weighted sum of each real score component below, 0-100. Only components that scored this run are counted.</span>
                <span class="pf-formula"><span class="pf-formula-label">HOW IT IS CALCULATED</span><span class="pf-diff">{rows}</span></span>
              </span>
            </span>
            """;
    }

    private static string BuildComponentPanel(string id, string displayText, ScoreComponent component)
    {
        return $"""
            <span class="hr">
              <input type="checkbox" class="hr-toggle" id="{id}" aria-label="How this score is worked out">
              <label for="{id}" class="hr-i pf">{displayText}</label>
              <span class="hr-panel pf-panel" role="dialog" aria-label="How this score is worked out">
                <label for="{id}" class="hr-close" aria-label="Close">&times;</label>
                <span class="hr-title pf-title">{System.Net.WebUtility.HtmlEncode(component.Name)} score - {displayText}</span>
                <span class="hr-intro pf-intro">This pillar's own health score this run, 0-100 - contributes to the composite score above at weight {component.Weight}.</span>
                <span class="pf-formula"><span class="pf-formula-label">HOW IT IS CALCULATED</span>
                  <span class="pf-diff">
                    <span class="pf-diff-row"><span class="pf-diff-value">{component.Score}</span><span class="pf-diff-label">This pillar's own score</span></span>
                    <span class="pf-diff-op">&times;</span>
                    <span class="pf-diff-row"><span class="pf-diff-value">{component.Weight}</span><span class="pf-diff-label">Weight toward the composite score</span></span>
                    <span class="pf-diff-op">=</span>
                    <span class="pf-diff-row pf-diff-result"><span class="pf-diff-value">{Contribution(component)}</span><span class="pf-diff-label">Contribution to the composite score</span></span>
                  </span>
                </span>
              </span>
            </span>
            """;
    }
}
