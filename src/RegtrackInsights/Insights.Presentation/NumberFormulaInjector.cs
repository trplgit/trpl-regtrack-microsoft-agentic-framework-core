using System.Linq;
using System.Net;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-09-30, TRIAL] Deterministically appends a small "How your numbers are worked out"
/// reference block just before &lt;/body&gt;, one hover-link entry per real figure it is given.
/// Reuses the render prompt's own `.hr` checkbox/panel mechanism (section 7 of
/// prompts/05_report_html_dimension_selection_licence_v10.md - every chart already requires it),
/// but this class does not depend on that CSS/script actually having been emitted this run - it
/// carries its own minimal, self-contained copy so it renders correctly even on a document with
/// zero charts.
///
/// [WHY CODE, NOT PROMPT - FOUND LIVE 2026-09-30] Two earlier prompt-only attempts (attach an "i"
/// to a `.kpi` tile, then wrap the number inline in its own prose) were tried across 3 real
/// tenant-1285 renders and NONE of the 3 produced the markup - this freehand dimension's real
/// layout varies too much per run (status-strip, bubbles/heat-table, tag chips - never once a
/// stable `.kpi` grid, and even the outer container class varied: "report-stack"/"report"/
/// "sections" across the 3 runs) for a free-text instruction to reliably land anywhere. Same
/// lesson as CoverageGridInjector/DimensionDataInjector elsewhere in this project: a mechanical,
/// exact-shape requirement is not something to gamble on prose generation getting right every
/// time - inject it deterministically instead. `&lt;/body&gt;` is the one anchor guaranteed by the
/// render prompt's own technical constraint 1 ("Exactly one HTML document") on every real run,
/// unlike any class name or container the render agent chooses for itself.
/// </summary>
public static class NumberFormulaInjector
{
    public sealed record Figure(string Label, string DisplayValue, string PanelText);

    public static string Inject(string html, IReadOnlyList<Figure> figures)
    {
        var present = figures.Where(f => !string.IsNullOrWhiteSpace(f.DisplayValue)).ToList();
        if (present.Count == 0 || string.IsNullOrEmpty(html))
            return html;

        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose < 0)
            return html; // malformed document shape - not this injector's job to fix.

        return html.Insert(bodyClose, BuildBlock(present));
    }

    private static string BuildBlock(List<Figure> figures)
    {
        var rows = string.Concat(figures.Select((figure, i) =>
        {
            var id = $"mi-{i}";
            var label = WebUtility.HtmlEncode(figure.Label);
            var value = WebUtility.HtmlEncode(figure.DisplayValue);
            var text = WebUtility.HtmlEncode(figure.PanelText);
            return "<div class=\"mi-row\"><span class=\"mi-label\">" + label + "</span> "
                + "<span class=\"hr\"><input type=\"checkbox\" class=\"hr-toggle\" id=\"" + id + "\" aria-label=\"How this number is worked out\">"
                + "<label for=\"" + id + "\" class=\"hr-i mi-nf\" title=\"How this number is worked out\">" + value + "</label>"
                + "<aside class=\"hr-panel mi-panel\" role=\"dialog\" aria-label=\"How this number is worked out\">"
                + "<label for=\"" + id + "\" class=\"hr-close\" aria-label=\"Close\">&times;</label>"
                + "<h4 class=\"hr-title\">How this number is worked out</h4>"
                + "<p class=\"hr-intro\">" + text + "</p>"
                + "</aside></span></div>";
        }));

        // Minimal, self-contained CSS - duplicates only the subset of the render prompt's own `.hr`
        // mechanism this block actually uses (no `.hr-row`/`.hr-viz`/`.hr-ico`, this never has
        // sub-rows), plus the Escape-to-close script, so this works even when the render agent
        // emitted zero charts and therefore never declared that CSS/script itself.
        const string style = """
            <style>
            .mi-block{max-width:1100px;margin:28px auto 0;padding:20px 18px 26px;font-family:'Poppins',sans-serif}
            .mi-block h3{font-size:15px;font-weight:700;color:#585858;margin:0 0 12px}
            .mi-grid{display:flex;flex-wrap:wrap;gap:10px 24px}
            .mi-row{display:flex;align-items:center;gap:6px;font-size:14px;color:#3d3d3d}
            .mi-label{color:#585858}
            .mi-nf{display:inline;width:auto;height:auto;padding:0;margin:0;border-radius:0;background:none;
              color:#125aab;font:inherit;font-weight:700;border-bottom:1.5px dotted #125aab;cursor:help}
            .mi-nf:hover,.hr-toggle:checked+.mi-nf{background:#e8f2fd}
            .mi-panel{width:min(340px,calc(100vw - 32px))}
            .hr{position:static;display:inline-flex}
            .hr-toggle{position:absolute;opacity:0;width:1px;height:1px;margin:0}
            .hr-panel{position:fixed;top:16px;right:16px;bottom:16px;width:min(600px,calc(100vw - 32px));z-index:60;overflow-y:auto;
              background:#fff;border:1px solid #e6e9ef;border-radius:16px;box-shadow:0 18px 48px rgba(16,24,40,.18);padding:28px 26px 20px;
              opacity:0;visibility:hidden;transform:translateX(12px);
              transition:opacity .18s ease,transform .18s ease,visibility 0s linear .3s}
            .hr:hover .hr-panel,.hr:has(.hr-toggle:checked) .hr-panel{opacity:1;visibility:visible;transform:none;transition-delay:0s}
            .hr:hover .hr-panel{z-index:61}
            .hr.hr-just-closed .hr-panel{opacity:0!important;visibility:hidden!important;transition:none!important}
            .hr-close{position:absolute;top:16px;right:18px;width:32px;height:32px;display:grid;place-items:center;border-radius:8px;
              font-size:26px;line-height:1;color:#6b7280;cursor:pointer}
            .hr-close:hover{background:#f7f8fc;color:#1f2937}
            .hr-title{margin:0 40px 6px 0;font-size:24px;font-weight:700;color:#1f2937;letter-spacing:-.01em}
            .hr-intro{margin:0;font-size:15px;line-height:1.55;color:#585858}
            @media (max-width:52rem){.hr-panel{top:auto;left:8px;right:8px;bottom:8px;width:auto;max-height:82vh;transform:translateY(12px)}}
            @media print{.hr-i,.hr-panel{display:none}}
            </style>
            """;

        // [FIX 2026-09-30, found live via headless Playwright audit] The close [x] sits INSIDE
        // .hr-panel, so the mouse is still over .hr when it is clicked - the CSS ":hover" rule
        // above keeps the panel visually open regardless of the checkbox state until the pointer
        // leaves entirely, making the close button look broken. This handler forces it shut on
        // click via the hr-just-closed class (declared above).
        // [FIX, same audit, found on the SECOND pass] A fixed setTimeout to lift the suppression
        // (originally 400ms) reopens the panel out from under the user if their cursor is still
        // resting on the close button when the timer fires - which it always is, since that is
        // where they just clicked. The suppression must last until the pointer actually leaves
        // `.hr`, not until a clock runs out - only then is it safe to lift, because `:hover` has
        // also gone false by that point, so no reopen-flash is possible.
        const string script = """
            <script>
            document.addEventListener('keydown',function(e){if(e.key==='Escape'){document.querySelectorAll('.hr-toggle').forEach(function(t){t.checked=false;});}});
            document.querySelectorAll('.hr-close').forEach(function(btn){btn.addEventListener('click',function(){var hr=btn.closest('.hr');if(!hr)return;hr.classList.add('hr-just-closed');hr.addEventListener('mouseleave',function onLeave(){hr.classList.remove('hr-just-closed');hr.removeEventListener('mouseleave',onLeave);});});});
            </script>
            """;

        return style + "<section class=\"mi-block\"><h3>How your numbers are worked out</h3><div class=\"mi-grid\">" + rows + "</div></section>" + script;
    }
}
