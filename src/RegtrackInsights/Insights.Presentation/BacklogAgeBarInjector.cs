using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Presentation;

/// <summary>
/// Deterministically generates the segmented backlog age-bar (`.di-agebar`) and replaces the
/// `&lt;div id="di-agebar-root"&gt;&lt;/div&gt;` placeholder the render agent leaves inside
/// Tab 2 Card 2 - same treatment CoverageGridInjector already gives the Coverage pane, applied
/// to a smaller, nested piece rather than a whole pane.
///
/// [BUG FOUND LIVE, 2026-09-09] Confirmed on two consecutive real tenant-29 runs: even with real
/// BacklogAging bucket data available both times, and an explicit prompt instruction to render
/// the segmented bar, the render agent silently fell back to the bigNumber-only shape both
/// times - the exact same failure family CoverageGridInjector's own doc comment documents for
/// the Coverage grid ("a large, mechanical, exact-shape task is not something to gamble on an
/// LLM getting right every time"). Every number and every markup shape this bar needs
/// (`BacklogAgingRow`/`BacklogAgingControlTotals`) is already known deterministically before the
/// render agent is ever called - there is no real judgement call left for it to make.
///
/// Real bucket values (`sql/22_dimension_backlog_aging.sql`): `current_fy`, `previous_fy`,
/// `older` - always exactly 3 rows. The SQL's own real result-set order is newest-first
/// (current_fy, previous_fy, older); this class re-sorts to the age-bar's own real convention,
/// OLDEST-first, left to right (older/bad, previous_fy/warn, current_fy/neu) - never trusts the
/// given row order to already match what the bar needs.
/// </summary>
public static partial class BacklogAgeBarInjector
{
    public static string Inject(string html, IReadOnlyList<BacklogAgingRow>? rows, BacklogAgingControlTotals? controlTotals)
    {
        if (!PlaceholderToken().IsMatch(html))
            return html; // not a fixed-holistic-shaped document, or Card 2 genuinely omitted it - nothing to do.

        // No real data to bucket (BacklogAging degraded, or a real zero-overdue tenant) - leave
        // the placeholder empty rather than fabricate an empty/misleading bar. Matches the
        // prompt's own "zero_overdue" handling - a real 0 still belongs in the narrative, just
        // never as three zero-width segments.
        if (rows is not { Count: > 0 } || controlTotals is null || controlTotals.SumOfRows == 0)
            return PlaceholderToken().Replace(html, "");

        var ordered = rows
            .OrderBy(r => r.Bucket switch { "older" => 0, "previous_fy" => 1, "current_fy" => 2, _ => 3 })
            .ToList();

        var barHtml = BuildAgeBar(ordered, controlTotals);
        return PlaceholderToken().Replace(html, barHtml);
    }

    // Swatch fill per age tone - the FILL register (brand handoff Sec.3.4), inline so the legend
    // is self-contained regardless of which stylesheet reached it.
    private static string ToneFill(string tone) => tone switch { "bad" => "#d24a3a", "warn" => "#e0a106", _ => "#8a8f99" };

    private static string BuildAgeBar(List<BacklogAgingRow> orderedBuckets, BacklogAgingControlTotals controlTotals)
    {
        // [CHANGED 2026-09-10, tenant rule] Bar segments carry NO visible text - every one gets
        // di-agebar__seg--minor (which hides any in-segment label) and only a hover tooltip. The
        // names and counts live in the di-stacklegend beneath instead: "Bar = proportion,
        // legend = names and counts, tooltip = hover" (white text sitting on the neutral-grey
        // segment was an illegible duplicate of the legend). This replaces the old
        // di-agebar__axis that repeated the period names as a bare axis under the bar.
        var sb = new StringBuilder();
        sb.Append("""<div class="di-agebar"><div class="di-agebar__scale">""");

        foreach (var bucket in orderedBuckets)
        {
            var tone = bucket.Bucket switch { "older" => "bad", "previous_fy" => "warn", _ => "neu" };
            // The "older" bucket's own FYLabel is null by construction (it spans everything
            // before the previous FY, not one specific FY) - "pre-{PreviousFyLabel}" names it
            // relative to the real previous-FY label instead, matching the prompt's own real
            // convention rather than leaving the segment unlabeled.
            var label = bucket.FYLabel ?? $"pre-{controlTotals.PreviousFyLabel}";
            var tip = $"{bucket.OverdueCount:N0} · {WebUtility.HtmlEncode(label)}";

            sb.Append($"""<div class="di-agebar__seg di-agebar__seg--minor di-agebar__seg--{tone}" style="flex:{bucket.OverdueCount}">""")
              .Append($"""<span class="di-agebar__tip">{tip}</span>""")
              .Append("</div>");
        }

        sb.Append("</div><div class=\"di-stacklegend\">");
        foreach (var bucket in orderedBuckets)
        {
            var tone = bucket.Bucket switch { "older" => "bad", "previous_fy" => "warn", _ => "neu" };
            var label = bucket.FYLabel ?? $"pre-{controlTotals.PreviousFyLabel}";
            sb.Append($"""<span class="di-stacklegend__item"><i class="di-stacklegend__sw" style="background:{ToneFill(tone)}"></i>""")
              .Append(WebUtility.HtmlEncode(label))
              .Append($""" <b class="tnum">{bucket.OverdueCount:N0}</b></span>""");
        }
        sb.Append("</div></div>");

        return sb.ToString();
    }

    [GeneratedRegex(@"<div\s+id=""di-agebar-root""\s*>\s*</div>", RegexOptions.Singleline)]
    private static partial Regex PlaceholderToken();
}
