using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// Deterministically embeds the Coverage pane's CSS (the 4 real status colours on chips/tiles/
/// pills, plus layout) - same treatment PoppinsFontInjector already gives the self-hosted font,
/// CoverageGridInjector gives the store grid, and CoverageScriptInjector gives the driving script.
///
/// [BUG FOUND LIVE, 2026-09-02] This CSS is 100% static - zero data substitution, byte-identical
/// every render - and was still asked of the render agent as "declare these rules verbatim".
/// Confirmed live: tiles rendered as unfilled outline boxes and the detail-panel pill had no
/// background colour at all, meaning the render agent's own &lt;style&gt; block silently dropped
/// or malformed the colour declarations. Same reasoning as every other Coverage-pane piece this
/// session: a large, mechanical, exact-copy task is not something to gamble on an LLM getting
/// right every time. Inserted as the LAST thing before &lt;/head&gt;, so it wins any cascade
/// conflict if the render agent's own &lt;style&gt; block still declares something for the same
/// selectors (later same-specificity rule wins, no !important needed).
/// </summary>
public static partial class CoverageCssInjector
{
    private const string Css = """
        <style>
        .di-covwrap{display:grid;grid-template-columns:minmax(0,1fr) 330px;gap:var(--gap-lg);align-items:start;margin-top:var(--gap-md)}
        .di-covmap{min-width:0}
        .di-covchip{display:inline-flex;align-items:center;gap:6px;padding:5px 11px;border:1px solid var(--c-border);border-radius:999px;background:var(--c-surface);color:var(--c-text-3);font-size:var(--fs-di-chip);cursor:pointer}
        .di-covchip:hover{background:#f3f6fc;color:var(--c-text)}
        .di-covchip--active{background:var(--c-brand);border-color:var(--c-brand);color:#fff}
        .di-covchip b{font-weight:600;opacity:.6}
        .di-covchip--active b{opacity:.85}
        .di-covfilter{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:var(--gap-md)}
        .di-covchip__sw{display:inline-block;width:9px;height:9px;border-radius:2px;flex-shrink:0}
        .di-covchip__sw--healthy{background:#2e9e5b}
        .di-covchip__sw--under_configured{background:#e0a106}
        .di-covchip__sw--has_ownerless{background:#e07a1f}
        .di-covchip__sw--unmapped{background:#c0392b}
        .di-covregion{padding:8px 0;border-top:1px solid var(--c-border)}
        .di-covregion:first-child{border-top:0;padding-top:2px}
        .di-covregion__name{font-size:var(--fs-di-chip);color:var(--c-grey);font-weight:500;letter-spacing:.02em;margin-bottom:6px}
        .di-covregion__name small{margin-left:6px;font-size:10px;color:var(--c-text-3);font-weight:500;letter-spacing:.04em;text-transform:uppercase}
        .di-covgrid{display:flex;flex-wrap:wrap;gap:3px}
        .di-covtile{width:14px;height:14px;border-radius:3px;border:0;padding:0;cursor:pointer;transition:transform .12s ease,box-shadow .12s ease}
        .di-covtile:hover{transform:scale(1.22);z-index:2;box-shadow:0 4px 10px rgba(20,28,48,.2)}
        .di-covtile--selected{outline:2px solid var(--c-text);outline-offset:-1px;z-index:3}
        .di-covtile--healthy{background:#2e9e5b}
        .di-covtile--under_configured{background:#e0a106}
        .di-covtile--has_ownerless{background:#e07a1f}
        .di-covtile--unmapped{background:#c0392b}
        .di-covmap[data-filter="healthy"] .di-covtile:not(.di-covtile--healthy),.di-covmap[data-filter="under_configured"] .di-covtile:not(.di-covtile--under_configured),.di-covmap[data-filter="has_ownerless"] .di-covtile:not(.di-covtile--has_ownerless),.di-covmap[data-filter="unmapped"] .di-covtile:not(.di-covtile--unmapped){opacity:.12;pointer-events:none}
        .di-covlegend{display:flex;flex-wrap:wrap;gap:14px;margin-top:12px;font-size:var(--fs-di-chip);color:var(--c-text-3)}
        .di-covlegend__item{display:inline-flex;align-items:center;gap:6px}
        .di-covlegend__item b{font-weight:600;color:var(--c-text)}
        .di-covdetail{background:var(--c-surface);border:1px solid var(--c-border);border-radius:var(--r-md);padding:16px 18px;display:flex;flex-direction:column;gap:12px}
        .di-covdetail--side{margin-top:0;position:sticky;top:4.4rem}
        .di-covdetail__head{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
        .di-covdetail__pill{display:inline-flex;align-items:center;gap:6px;font-size:var(--fs-di-chip);font-weight:500;padding:3px 9px;border-radius:999px;color:#fff}
        .di-covdetail__dot{width:6px;height:6px;border-radius:999px;background:rgba(255,255,255,.75)}
        .di-covdetail__pill--healthy{background:#2e9e5b}
        .di-covdetail__pill--under_configured{background:#e0a106;color:#3a2a00}
        .di-covdetail__pill--under_configured .di-covdetail__dot{background:rgba(40,30,0,.55)}
        .di-covdetail__pill--has_ownerless{background:#e07a1f}
        .di-covdetail__pill--unmapped{background:#c0392b}
        .di-covdetail__ref{font-size:var(--fs-di-chip);color:var(--c-grey);letter-spacing:.02em}
        .di-covdetail__title{font-size:var(--fs-di-snaphead);font-weight:600;letter-spacing:-.01em;margin:0;color:var(--c-text)}
        .di-covdetail__summary{margin:0;font-size:var(--fs-di-comp-meta);color:var(--c-text-3);line-height:1.55}
        .di-covdetail__metrics{display:grid;grid-template-columns:1fr 1fr;gap:1px;background:var(--c-border);border:1px solid var(--c-border);border-radius:var(--r-md);overflow:hidden}
        .di-covdetail__m{background:var(--c-surface);padding:8px 11px}
        .di-covdetail__ml{font-size:10px;text-transform:uppercase;letter-spacing:.08em;color:var(--c-grey);font-weight:500;margin-bottom:3px}
        .di-covdetail__mv{font-size:var(--fs-di-headline);font-weight:600;color:var(--c-text)}
        .di-covdetail__mv small{font-weight:400;color:var(--c-grey);font-size:.8em}
        .di-covdetail__mv--bad{color:#b3261e}
        .di-covdetail__action{background:#f6f7f9;border:1px solid var(--c-border);border-radius:8px;padding:10px 12px}
        .di-covdetail__action-h{font-size:10px;text-transform:uppercase;letter-spacing:.08em;color:var(--c-grey);font-weight:600;margin-bottom:4px}
        .di-covdetail__action p{margin:0;font-size:var(--fs-di-comp-meta);color:var(--c-text-2);line-height:1.5}
        @media (max-width: 900px) { .di-covwrap{grid-template-columns:1fr} .di-covdetail--side{position:static} }
        </style>
        """;

    public static string Inject(string html)
    {
        if (!html.Contains("di-covgrid", StringComparison.Ordinal))
            return html; // no Coverage pane rendered this run - nothing to style.

        var match = HeadCloseTag().Match(html);
        if (!match.Success)
            throw new InvalidOperationException("CoverageCssInjector: no </head> tag found in generated HTML - cannot inject the Coverage pane CSS.");

        return html.Insert(match.Index, Css);
    }

    [GeneratedRegex(@"</head\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadCloseTag();
}
