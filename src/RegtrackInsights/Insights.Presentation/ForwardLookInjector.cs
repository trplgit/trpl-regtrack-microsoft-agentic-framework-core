using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Presentation;

/// <summary>
/// Deterministically renders the WHOLE Forward-look pane body - the `.di-kpi--fwd` card head,
/// the "N due in the next 90 days" figure, the carried_forward / clean_at_risk / healthy segment
/// bar, the jail-risk callout, AND the 5-window bucket chart - and replaces the
/// `&lt;div id="di-forward-root"&gt;&lt;/div&gt;` placeholder the render agent leaves in Tab 5.
/// Same treatment CoverageGridInjector gives the Coverage pane and BacklogAgeBarInjector gives
/// Tab 2's age-bar.
///
/// [WIDENED 2026-09-10] Was just the 3-segment ForwardRisk breakdown; the render agent kept
/// dropping the rest of the pane (whole-pane omission on a real tenant-1300 run, exactly the
/// failure family this injection pattern exists to close). Now the render agent authors NOTHING
/// in this pane but the section shell + the placeholder div - every number and every markup
/// shape is deterministic here:
///   - segment counts (carried / at-risk / clear) + jail-risk: `ForwardRiskControlTotals`
///     (usp_Insights_Dimension_ForwardRisk, sql/26 - deployed and live, reconciled by the proc).
///   - the 5 day-window bar heights + labels: `ForwardPipelineRow[]` (sql/24) `DueCount` /
///     `WindowLabel`. Heights are shares of the busiest window (no absolute value labels - the
///     bucket chart has no value axis, per the tenant rule).
/// None of this can round-trip through the render agent's assertion-only payload (control totals,
/// not typed assertions), so it is injected - the same reason the Coverage grid is.
///
/// No-ops (returns html unchanged) if the placeholder is absent. Emits just an empty replacement
/// (pane left as its head only) if BOTH sources are absent/empty - a genuinely degraded forward
/// run, never fabricated segments.
/// </summary>
public static partial class ForwardLookInjector
{
    public static string Inject(
        string html,
        ForwardRiskControlTotals? riskTotals,
        ForwardPipelineControlTotals? pipelineTotals = null,
        IReadOnlyList<ForwardPipelineRow>? pipelineRows = null)
    {
        if (!PlaceholderToken().IsMatch(html))
            return html; // not a fixed-holistic-shaped document, or Tab 5 genuinely omitted the placeholder.

        var haveRisk = riskTotals is { ForwardWindowEmpty: false, DueInWindow: > 0 };
        var haveBuckets = pipelineRows is { Count: > 0 } && pipelineRows.Any(r => r.DueCount > 0);

        if (!haveRisk && !haveBuckets)
            return PlaceholderToken().Replace(html, ""); // no real forward data at all this run - head-only pane.

        return PlaceholderToken().Replace(html, BuildCard(riskTotals, pipelineTotals, pipelineRows, haveRisk, haveBuckets));
    }

    private static string BuildCard(
        ForwardRiskControlTotals? risk,
        ForwardPipelineControlTotals? pipelineTotals,
        IReadOnlyList<ForwardPipelineRow>? rows,
        bool haveRisk,
        bool haveBuckets)
    {
        var due = haveRisk ? risk!.DueInWindow
            : pipelineTotals?.DueNext90d > 0 ? pipelineTotals.DueNext90d
            : rows!.Sum(r => r.DueCount);

        var sb = new StringBuilder();
        sb.Append("<article class=\"di-kpi di-kpi--span12 di-kpi--fwd\">");

        // --- head: eyebrow / title / verdict tag ---
        sb.Append("<div class=\"di-kpi__head\"><div class=\"di-kpi__headtext\">")
          .Append("<div class=\"di-kpi__eyebrow\">Forward pipeline</div>")
          .Append("<h3 class=\"di-kpi__title\">Due distribution (next 90 days)</h3>")
          .Append("</div>")
          .Append(haveRisk ? BuildTag(risk!) : "")
          .Append("</div>");

        // --- big number ---
        sb.Append("<div class=\"di-kpi__big\">")
          .Append($"<div class=\"di-kpi__num tnum\">{due:N0}</div>")
          .Append("<div class=\"di-kpi__unit\">obligations due in the next 90 days</div>")
          .Append("</div>");

        // --- ForwardRisk segment breakdown (only when real) ---
        if (haveRisk)
        {
            var carried = Math.Max(0, risk!.CarriedForward);
            var clean = Math.Max(0, risk.CleanAtRisk);
            var healthy = Math.Max(0, risk.Healthy);
            var denom = carried + clean + healthy;
            if (denom > 0)
            {
                var carriedPct = 100.0 * carried / denom;
                var cleanPct = 100.0 * clean / denom;
                var healthyPct = 100.0 * healthy / denom;
                var carriedShare = 100.0 * carried / due;

                sb.Append("<p class=\"di-kpi__narr\">")
                  .Append($"<b class=\"tnum\">{carried:N0}</b> of them ({carriedShare.ToString("0.0", CultureInfo.InvariantCulture)}%) are <b>already overdue today</b> and coming due again - old trouble returning, not new work.")
                  .Append("</p>");

                sb.Append("<div class=\"di-stackbar\" aria-hidden=\"true\">")
                  .Append($"<span style=\"width:{Pct(carriedPct)}%;background:#d24a3a\"></span>")
                  .Append($"<span style=\"width:{Pct(cleanPct)}%;background:#e0a106\"></span>")
                  .Append($"<span style=\"width:{Pct(healthyPct)}%;background:#8a8f99\"></span>")
                  .Append("</div>");

                sb.Append("<div class=\"di-stacklegend\">")
                  .Append($"<span class=\"di-stacklegend__item\"><i class=\"di-stacklegend__sw\" style=\"background:#d24a3a\"></i>Carried forward - already overdue &mdash; <b class=\"tnum\">{carried:N0}</b></span>")
                  .Append($"<span class=\"di-stacklegend__item\"><i class=\"di-stacklegend__sw\" style=\"background:#e0a106\"></i>At risk - a fixable warning sign &mdash; <b class=\"tnum\">{clean:N0}</b></span>")
                  .Append($"<span class=\"di-stacklegend__item\"><i class=\"di-stacklegend__sw\" style=\"background:#8a8f99\"></i>Clear - no warning signs &mdash; <b class=\"tnum\">{healthy:N0}</b></span>")
                  .Append("</div>");

                if (risk.ImprisonmentNeedingAttention > 0)
                {
                    // Tenant rule: a whole sentence is never painted a status colour - only the
                    // FIGURE is red. `.di-kpi__narr--alert` keeps the prose at body colour, <b> red.
                    sb.Append("<p class=\"di-kpi__narr di-kpi__narr--alert\">")
                      .Append($"<b class=\"tnum\">{risk.ImprisonmentNeedingAttention:N0}</b> of the obligations needing attention carry imprisonment exposure.")
                      .Append("</p>");
                }
            }
        }

        // --- 5-window bucket chart (heights are shares of the busiest window; NO value labels) ---
        if (haveBuckets)
        {
            var ordered = rows!.OrderBy(r => r.MinDaysOut).ToList();
            var max = ordered.Max(r => r.DueCount);
            var windowTotal = ordered.Sum(r => r.DueCount);
            var busiest = ordered.OrderByDescending(r => r.DueCount).First();
            // Share of UPCOMING work = this window's count over the sum of the 5 window counts
            // (not over DueInWindow, which is a wider ForwardRisk scope) - matches the reference.
            var busiestShare = windowTotal > 0 ? 100.0 * busiest.DueCount / windowTotal : 0;

            sb.Append("<div class=\"di-fwd\" aria-hidden=\"true\">");
            foreach (var r in ordered)
            {
                var h = max > 0 ? 100.0 * r.DueCount / max : 0;
                sb.Append($"<div class=\"di-fwd__col\"><i class=\"di-fwd__bar\" style=\"height:{Pct(h)}%\"></i></div>");
            }
            sb.Append("</div>");

            sb.Append("<div class=\"di-fwd__axis\">");
            foreach (var r in ordered)
                sb.Append("<span>").Append(WebUtility.HtmlEncode(r.WindowLabel)).Append("</span>");
            sb.Append("</div>");

            var busiestPhrase = Regex.IsMatch(busiest.WindowLabel, @"^\d+-\d+d$")
                ? busiest.WindowLabel[..^1] + " days"
                : busiest.WindowLabel;
            sb.Append("<p class=\"di-kpi__narr\">")
              .Append($"{due:N0} obligations are due in the next 90 days. Of upcoming work, {busiestShare.ToString("0.00", CultureInfo.InvariantCulture)}% falls due in {WebUtility.HtmlEncode(busiestPhrase)}.")
              .Append("</p>");
        }

        sb.Append("</article>");
        return sb.ToString();
    }

    // Verdict tag: carried-forward dominates -> bad "Carried forward"; otherwise the at-risk
    // segment leads -> warn "At risk"; else -> ok "On track". Chip register (bg + text colour),
    // inline so it does not depend on a `.di-kpi__tag--*` rule reaching this card.
    private static string BuildTag(ForwardRiskControlTotals ct)
    {
        var carried = Math.Max(0, ct.CarriedForward);
        var clean = Math.Max(0, ct.CleanAtRisk);
        var healthy = Math.Max(0, ct.Healthy);

        var (tone, label, bg, fg) =
            carried > 0 && carried >= clean && carried >= healthy ? ("bad", "Carried forward", "#fcebea", "#b3261e")
            : clean > 0 && clean >= healthy ? ("warn", "At risk", "#fdf3e2", "#b45708")
            : ("ok", "On track", "#e9f6ee", "#1e8a4a");

        return $"<span class=\"di-kpi__tag di-kpi__tag--{tone}\" style=\"background:{bg};color:{fg}\">"
             + "<span class=\"di-kpi__dot\"></span>" + label + "</span>";
    }

    private static string Pct(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"<div\s+id=""di-forward-root""\s*>\s*</div>", RegexOptions.Singleline)]
    private static partial Regex PlaceholderToken();
}
