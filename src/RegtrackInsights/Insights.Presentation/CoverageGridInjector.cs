using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Presentation;

/// <summary>
/// Deterministically generates the ENTIRE Coverage pane body (KPI card, status-filter chips, the
/// region-grouped store grid, legend, and the detail-aside skeleton) and replaces whatever the
/// render agent wrote inside `&lt;section id="di-pane-3"&gt;...&lt;/section&gt;` - same treatment
/// PoppinsFontInjector gives the self-hosted font and CoverageScriptInjector gives the driving
/// script, now extended to the whole pane rather than just the grid tiles.
///
/// [BUG FOUND LIVE, 2026-09-02] Confirmed live: asked to hand-author one tile per real branch (up
/// to 177 for tenant 29), the render agent silently drew a SAMPLE (10 of 177) while still stating
/// the true full counts in the chip/legend/KPI text elsewhere in the same document.
///
/// [BUG FOUND LIVE AGAIN, 2026-09-07] Narrowing the fix to "replace only the `di-covgrid-root`
/// placeholder, leave the rest of the pane to the render agent" (the original shape of this class)
/// was NOT sufficient: FixedHolisticStructureGate's own item 3 already documents the render agent
/// skipping the placeholder outright and substituting a plain KPI-only summary card instead - and
/// that happened again, twice more, on two consecutive manual runs for tenant 29 (identical
/// violation both times, ruling out one-off sampling luck). Every number and every markup shape
/// this pane needs (`coverage_status_counts`, the region/tile grid) is already known deterministically
/// from `locationRows` before the render agent is ever called - there is no real judgement call left
/// for it to make here, unlike a narrative-prose pane. Leaving ANY of this pane's markup to a
/// prose-generation call - even "just" the wrapper around the grid - is exactly the same class of
/// problem the grid/script/CSS fixes already solved for their own pieces, just not carried far
/// enough: a large, mechanical, exact-shape task is not something to gamble on an LLM getting right
/// every time, especially under output-token pressure on a long document. Replacing the WHOLE pane
/// body unconditionally (not just a placeholder that may or may not exist) closes this by
/// construction - the render agent's own attempt at this pane's inner markup, correct or not, is
/// simply discarded.
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
        // [TEMP DIAGNOSTIC 2026-09-07] remove once tenant 29 manual runs are confirmed clean.
        if (!PaneOpenToken().IsMatch(html))
        {
            Console.Error.WriteLine("[DIAG] CoverageGridInjector: no-op - di-pane-3 open tag not found in html.");
            return html; // not a fixed-holistic-shaped document, or Coverage pane genuinely not present - nothing to do.
        }

        if (PaneBlockedToken().IsMatch(html))
        {
            Console.Error.WriteLine("[DIAG] CoverageGridInjector: no-op - di-pane-3 is marked data-blocked=\"true\".");
            return html; // defensive only - Coverage always has real data in practice, never legitimately blocked.
        }

        var leafRows = locationRows?.Where(r => r.NodeType == EntityNodeType.Leaf).ToList() ?? [];
        Console.Error.WriteLine($"[DIAG] CoverageGridInjector: locationRows={(locationRows is null ? "null" : locationRows.Count.ToString())}, leafRows={leafRows.Count}.");
        if (leafRows.Count == 0)
            return html; // Location degraded, or no leaf branches - never inject an empty/fabricated grid.

        var counts = LocationCoverageClassifier.ComputeCounts(leafRows);
        var paneBody = BuildPaneBody(counts, leafRows);

        var match = PaneContentToken().Match(html);
        if (!match.Success)
        {
            Console.Error.WriteLine("[DIAG] CoverageGridInjector: no-op - PaneContentToken failed to match despite PaneOpenToken matching (malformed section?).");
            return html; // malformed document shape - ReportEmitNormalizer's own rule 1 catches that, not this injector.
        }
        Console.Error.WriteLine($"[DIAG] CoverageGridInjector: injecting {paneBody.Length} chars of deterministic pane body.");

        // Index-based splice, not Regex.Replace/string.Replace with the new body as a replacement
        // string - both treat certain characters specially ('$' as a backreference, or a search
        // string that happens to recur) and neither is needed here: the inner group's exact span in
        // `html` is already known, so slicing around it can only ever touch that one location.
        // Replaces the section's ENTIRE inner content - whatever the render agent wrote there,
        // correct or not, is discarded rather than merged with.
        var inner = match.Groups["inner"];
        return string.Concat(html.AsSpan(0, inner.Index), paneBody, html.AsSpan(inner.Index + inner.Length));
    }

    private static string BuildPaneBody(CoverageStatusCounts counts, List<LocationRow> leafRows)
    {
        var sb = new StringBuilder();
        sb.Append("""<div class="di-pane__head"><span class="di-secnum" aria-hidden="true">03</span><h2 class="di-pane__title">Key indicators</h2></div>""");
        sb.Append("""<div class="di-kpigrid"><article class="di-kpi di-kpi--span12">""")
          .Append("""<div class="di-kpi__head"><div class="di-kpi__headtext"><div class="di-kpi__eyebrow">Coverage</div><h3 class="di-kpi__title">Store mapping</h3></div></div>""")
          .Append("""<div class="di-kpi__pairs">""")
          .Append($"""<div class="di-kpi__pair"><div class="di-kpi__pair-lbl">Mapped</div><div class="di-kpi__pair-val tnum">{counts.Healthy}</div></div>""")
          .Append($"""<div class="di-kpi__pair"><div class="di-kpi__pair-lbl">Has ownerless</div><div class="di-kpi__pair-val tnum">{counts.HasOwnerless}</div></div>""")
          .Append($"""<div class="di-kpi__pair"><div class="di-kpi__pair-lbl">Unmapped</div><div class="di-kpi__pair-val tnum">{counts.Unmapped}</div></div>""")
          .Append("</div></article></div>");

        sb.Append("""<div class="di-covwrap"><div class="di-covmap"><div class="di-covfilter" role="toolbar" aria-label="Coverage status counts">""")
          .Append($"""<button type="button" class="di-covchip" data-filter="all">All <b class="tnum">{counts.Total}</b></button>""")
          .Append($"""<button type="button" class="di-covchip" data-filter="healthy"><i class="di-covchip__sw di-covchip__sw--healthy"></i>Mapped <b class="tnum">{counts.Healthy}</b></button>""")
          .Append($"""<button type="button" class="di-covchip" data-filter="under_configured"><i class="di-covchip__sw di-covchip__sw--under_configured"></i>Under-configured <b class="tnum">{counts.UnderConfigured}</b></button>""")
          .Append($"""<button type="button" class="di-covchip" data-filter="has_ownerless"><i class="di-covchip__sw di-covchip__sw--has_ownerless"></i>Has ownerless <b class="tnum">{counts.HasOwnerless}</b></button>""")
          .Append($"""<button type="button" class="di-covchip" data-filter="unmapped"><i class="di-covchip__sw di-covchip__sw--unmapped"></i>Unmapped <b class="tnum">{counts.Unmapped}</b></button>""")
          .Append("</div>");

        sb.Append(BuildRegions(leafRows));

        sb.Append("""<div class="di-covlegend">""")
          .Append($"""<span class="di-covlegend__item"><i class="di-covchip__sw di-covchip__sw--healthy"></i>Mapped <b class="tnum">{counts.Healthy}</b></span>""")
          .Append($"""<span class="di-covlegend__item"><i class="di-covchip__sw di-covchip__sw--under_configured"></i>Under-configured <b class="tnum">{counts.UnderConfigured}</b></span>""")
          .Append($"""<span class="di-covlegend__item"><i class="di-covchip__sw di-covchip__sw--has_ownerless"></i>Has ownerless <b class="tnum">{counts.HasOwnerless}</b></span>""")
          .Append($"""<span class="di-covlegend__item"><i class="di-covchip__sw di-covchip__sw--unmapped"></i>Unmapped <b class="tnum">{counts.Unmapped}</b></span>""")
          .Append("</div></div>");

        // Detail aside - all values left empty by design; CoverageScriptInjector's driving script
        // fills every field at load time from the just-injected tiles' own data-* attributes.
        sb.Append("""<aside class="di-covdetail di-covdetail--side" aria-live="polite">""")
          .Append("""<div class="di-covdetail__head"><span class="di-covdetail__pill"><span class="di-covdetail__dot"></span></span><span class="di-covdetail__ref"></span></div>""")
          .Append("""<h4 class="di-covdetail__title"></h4><p class="di-covdetail__summary"></p>""")
          .Append("""<div class="di-covdetail__metrics">""")
          .Append("""<div class="di-covdetail__m"><div class="di-covdetail__ml">Mapped</div><div class="di-covdetail__mv tnum"></div></div>""")
          .Append("""<div class="di-covdetail__m"><div class="di-covdetail__ml">Coverage</div><div class="di-covdetail__mv tnum"></div></div>""")
          .Append("""<div class="di-covdetail__m"><div class="di-covdetail__ml">Ownerless</div><div class="di-covdetail__mv tnum"></div></div>""")
          .Append("""<div class="di-covdetail__m"><div class="di-covdetail__ml">Overdue</div><div class="di-covdetail__mv tnum"></div></div>""")
          .Append("""<div class="di-covdetail__m"><div class="di-covdetail__ml">Performer</div><div class="di-covdetail__mv"></div></div>""")
          .Append("""<div class="di-covdetail__m"><div class="di-covdetail__ml">Peer gap</div><div class="di-covdetail__mv tnum"></div></div>""")
          .Append("</div>")
          .Append("""<div class="di-covdetail__action"><div class="di-covdetail__action-h">Recommended action</div><p></p></div>""")
          .Append("</aside></div>");

        return sb.ToString();
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

    // Same patterns FixedHolisticStructureGate.CheckCoveragePaneHasInteractiveGrid uses to FIND the
    // pane (duplicated deliberately, same reasoning as MafReportHtmlAgent.FilterToLeafStores's own
    // doc comment: one real rule, applied at two call sites, rather than trusted to travel
    // correctly through a shared reference) - that gate now runs AFTER this injector and mostly
    // exists as defense-in-depth once this class replaces the pane body unconditionally.
    [GeneratedRegex(@"<section\b[^>]*\bid=""di-pane-3""[^>]*>", RegexOptions.Singleline)]
    private static partial Regex PaneOpenToken();

    [GeneratedRegex(@"<section\b[^>]*\bid=""di-pane-3""[^>]*\bdata-blocked=""true""", RegexOptions.Singleline)]
    private static partial Regex PaneBlockedToken();

    [GeneratedRegex(@"<section\b[^>]*\bid=""di-pane-3""[^>]*>(?<inner>.*?)</section>", RegexOptions.Singleline)]
    private static partial Regex PaneContentToken();
}
