using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Presentation;

/// <summary>
/// Deterministically generates the Coverage-pane store grid (one real &lt;button&gt; tile per LEAF
/// branch, grouped into real per-state regions) and injects it into the render agent's own
/// `&lt;div id="di-covgrid-root"&gt;&lt;/div&gt;` placeholder - same treatment PoppinsFontInjector
/// gives the self-hosted font and CoverageScriptInjector gives the driving script.
///
/// [BUG FOUND LIVE, 2026-09-02] Confirmed live: asked to hand-author one tile per real branch (up
/// to 177 for tenant 29), the render agent silently drew a SAMPLE (10 of 177) while still stating
/// the true full counts in the chip/legend/KPI text elsewhere in the same document - exactly the
/// same class of problem as the font (real WOFF2 bytes an LLM cannot emit) and the driving script
/// (DOMPurify strips content an LLM cannot reliably avoid writing): a large, mechanical, per-row
/// generation task is not something to gamble on an LLM completing every time, especially under
/// output-token pressure. Removing this from the render agent's job entirely closes the gap by
/// construction rather than by asking more clearly.
///
/// Classification uses LocationCoverageClassifier (real sql/05_dimension_location.sql Flags field)
/// - never re-derives thresholds. Leaf-only by construction: filters NodeType internally, so a
/// caller that forgets to pre-filter still gets the correct population (same defensive posture as
/// MafReportHtmlAgent.FilterToLeafStores, which independently does the same filtering for the
/// aggregate counts sent to the render agent - two call sites, one real rule, applied twice rather
/// than trusted to travel correctly through a shared mutable list).
/// </summary>
public static partial class CoverageGridInjector
{
    public static string Inject(string html, IReadOnlyList<LocationRow>? locationRows)
    {
        if (!PlaceholderToken().IsMatch(html))
            return html; // render agent did not scaffold the placeholder (or Coverage pane not present) - nothing to do.

        var leafRows = locationRows?.Where(r => r.NodeType == EntityNodeType.Leaf).ToList() ?? [];
        if (leafRows.Count == 0)
            return html; // Location degraded, or no leaf branches - never inject an empty/fabricated grid.

        var markup = BuildRegions(leafRows);
        // MatchEvaluator overload, not the string-replacement overload - the latter treats '$' in
        // the replacement specially (backreferences); a branch name containing a literal '$' would
        // otherwise corrupt the injected markup. This is a pure literal substitution.
        return PlaceholderToken().Replace(html, _ => markup, 1);
    }

    private static string BuildRegions(List<LocationRow> leafRows)
    {
        var sb = new StringBuilder();
        var regions = leafRows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.StateName) ? "Unassigned state" : r.StateName!)
            .OrderByDescending(g => g.Count());

        foreach (var region in regions)
        {
            sb.Append("<div class=\"di-covregion\"><div class=\"di-covregion__name\">")
              .Append(WebUtility.HtmlEncode(region.Key))
              .Append("<small>").Append(region.Count()).Append(" stores</small></div><div class=\"di-covgrid\">");

            foreach (var row in region)
                AppendTile(sb, row, region.Key);

            sb.Append("</div></div>");
        }
        return sb.ToString();
    }

    private static void AppendTile(StringBuilder sb, LocationRow row, string stateName)
    {
        var status = LocationCoverageClassifier.Classify(row);
        var name = WebUtility.HtmlEncode(row.BranchName ?? "");
        var performer = row.DistinctPerformers >= 1 ? "true" : "false";

        sb.Append("<button type=\"button\" class=\"di-covtile di-covtile--").Append(status)
          .Append("\" data-st=\"").Append(status)
          .Append("\" data-branch-id=\"").Append(row.BranchID)
          .Append("\" data-branch-name=\"").Append(name)
          .Append("\" data-state=\"").Append(WebUtility.HtmlEncode(stateName))
          .Append("\" data-instances=\"").Append(row.Instances)
          .Append("\" data-overdue=\"").Append(row.Overdue)
          .Append("\" data-ownerless=\"").Append(row.Ownerless)
          .Append("\" data-performer=\"").Append(performer)
          .Append("\" title=\"").Append(name)
          .Append("\"></button>");
    }

    [GeneratedRegex(@"<div\s+id=""di-covgrid-root""[^>]*>\s*</div>")]
    private static partial Regex PlaceholderToken();
}
