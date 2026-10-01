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
                + "<label for=\"" + id + "\" class=\"hr-i mi-nf\">" + value + "</label>"
                + "<aside class=\"hr-panel mi-panel\" role=\"dialog\" aria-label=\"How this number is worked out\">"
                + "<label for=\"" + id + "\" class=\"hr-close\" aria-label=\"Close\">&times;</label>"
                + "<h4 class=\"hr-title\">How this number is worked out</h4>"
                + "<p class=\"hr-intro\">" + text + "</p>"
                + "</aside></span></div>";
        }));

        return SharedStyle + "<section class=\"mi-block\"><h3>How your numbers are worked out</h3><div class=\"mi-grid\">" + rows + "</div></section>" + SharedScript;
    }

    /// <summary>
    /// The `.hr`/`.hr-panel`/`.hr-close` checkbox+panel mechanism (plus this class's own
    /// `.mi-*` bottom-strip styling) - minimal, self-contained CSS so this works even on a
    /// document with zero charts of its own. Exposed so other deterministic injectors that build
    /// their OWN inline hover-links (e.g. <c>EntityScoreFormulaInjector</c>) can reuse the exact
    /// same, already-bugfixed mechanism instead of carrying a second copy that could drift.
    /// </summary>
    internal const string SharedStyle = """
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
        .pf{display:inline;width:auto;height:auto;padding:0;margin:0;border-radius:0;background:none;
          color:inherit;font:inherit;font-weight:inherit;border-bottom:1.5px dotted #125aab;cursor:help}
        .pf:hover,.hr-toggle:checked+.pf{background:#e8f2fd}
        .pf-panel{display:block;right:auto;bottom:auto;width:min(340px,calc(100vw - 32px));max-height:calc(100vh - 24px);overflow-y:auto;padding:18px 20px 20px}
        .pf-panel::before{content:"";position:absolute;top:-8px;left:20px;width:14px;height:14px;background:#fff;
          border-left:1px solid #e6e9ef;border-top:1px solid #e6e9ef;transform:rotate(45deg);border-radius:2px}
        .pf-title{display:block;margin:0 40px 6px 0;font-size:20px;font-weight:700;color:#1f2937}
        .pf-intro{display:block;margin:0 0 14px;font-size:14px;line-height:1.55;color:#585858}
        .pf-formula{display:block;background:#e8f2fd;border-radius:10px;padding:14px 16px 16px;margin-top:4px}
        .pf-formula-label{display:block;font-size:11px;font-weight:700;color:#125aab;letter-spacing:.03em;margin:0 0 10px}
        .pf-diff{display:flex;flex-direction:column;align-items:center;gap:2px}
        .pf-diff-row{display:flex;align-items:baseline;gap:8px;justify-content:center}
        .pf-diff-value{font-size:15px;font-weight:700;color:#1f2937;white-space:nowrap}
        .pf-diff-label{font-size:11px;color:#585858;white-space:nowrap}
        .pf-diff-op{display:block;text-align:center;font-size:14px;font-weight:600;color:#1f2937;margin:2px 0}
        .pf-diff-result .pf-diff-value{color:#125aab}
        .pf-frac{display:flex;align-items:center;justify-content:center;gap:10px}
        .pf-frac-stack{display:flex;flex-direction:column;align-items:center}
        .pf-num{display:flex;flex-direction:column;align-items:center;padding-bottom:6px;border-bottom:1.5px solid #1f2937}
        .pf-den{display:flex;flex-direction:column;align-items:center;padding-top:6px}
        .pf-num-value,.pf-den-value{font-size:15px;font-weight:700;color:#1f2937;white-space:nowrap}
        .pf-num-label,.pf-den-label{font-size:11px;color:#585858;white-space:nowrap}
        .pf-times{font-size:14px;font-weight:600;color:#1f2937}
        </style>
        """;

    /// <summary>
    /// The Escape-to-close and close-button-actually-closes-it handlers - see
    /// <see cref="SharedStyle"/>'s own doc comment on why this is shared, and the
    /// [FIX 2026-09-30] comments below for why the close handler works the way it does.
    /// </summary>
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
    // [ADDED 2026-10-01] .pf-panel must track the trigger's own on-screen position (it is NOT
    // right-docked like a chart's .hr-panel) - same pfPlace script every dimension_selection
    // render prompt's own section 7b already requires, centralized here so EntityScoreFormulaInjector
    // does not need a third copy. `.pf-panel` stays position:fixed (inherited from the shared
    // .hr-panel rule above) specifically so this script's own top/left assignment is never
    // silently clipped by a card's own `overflow:hidden` - see the dimension_selection prompts'
    // own [FIX] note on this exact failure mode for why `position:absolute` cannot be used instead.
    // [FIX 2026-10-01, found live via a user screenshot] pfPlace always placed the panel BELOW the
    // trigger (`top = r.bottom + 10`) with no check for whether that fit - on a tile near the
    // bottom of the viewport the panel rendered mostly off-screen, visibly cut by the browser
    // window. No ancestor of these tiles uses `transform`/`filter`/`perspective` (checked against
    // the real rendered CSS), so `position:fixed` already places this relative to the viewport, not
    // a clipping container - the missing piece was purely the below/above choice and a vertical
    // clamp. Also added `.pf-panel`'s own `max-height`+`overflow-y:auto` (CSS above) as a last-resort
    // safety net for a panel taller than the viewport itself.
    internal const string SharedScript = """
        <script>
        document.addEventListener('keydown',function(e){if(e.key==='Escape'){document.querySelectorAll('.hr-toggle').forEach(function(t){t.checked=false;});}});
        document.querySelectorAll('.hr-close').forEach(function(btn){btn.addEventListener('click',function(){var hr=btn.closest('.hr');if(!hr)return;hr.classList.add('hr-just-closed');hr.addEventListener('mouseleave',function onLeave(){hr.classList.remove('hr-just-closed');hr.removeEventListener('mouseleave',onLeave);});});});
        function pfPlace(el){
          var panel=el.closest('.hr').querySelector('.pf-panel');if(!panel)return;
          var margin=12,r=el.getBoundingClientRect(),vw=window.innerWidth,vh=window.innerHeight;
          var w=panel.offsetWidth||340,h=panel.offsetHeight||200;
          var left=Math.min(Math.max(margin,r.left),vw-w-margin);
          var spaceBelow=vh-r.bottom-margin,spaceAbove=r.top-margin;
          var top=(h<=spaceBelow||spaceBelow>=spaceAbove)?r.bottom+10:r.top-h-10;
          top=Math.max(margin,Math.min(top,vh-margin-Math.min(h,vh-2*margin)));
          panel.style.left=left+'px';panel.style.top=top+'px';
        }
        document.querySelectorAll('.pf').forEach(function(el){el.addEventListener('mouseenter',function(){pfPlace(el);});el.addEventListener('focus',function(){pfPlace(el);});el.addEventListener('click',function(){pfPlace(el);});});
        window.addEventListener('resize',function(){document.querySelectorAll('.hr-toggle:checked').forEach(function(cb){var hr=cb.closest('.hr'),trig=hr&&hr.querySelector('.pf');if(trig)pfPlace(trig);});});
        </script>
        """;
}
