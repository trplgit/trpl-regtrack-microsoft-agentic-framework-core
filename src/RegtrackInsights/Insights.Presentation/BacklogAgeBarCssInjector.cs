using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// Deterministically embeds the backlog age-bar's CSS (3 age-tone segment colours, scale
/// chrome, hover tooltip) - same treatment CoverageCssInjector already gives the Coverage
/// pane, for the exact same reason.
///
/// [BUG FOUND LIVE, 2026-09-09] BacklogAgeBarInjector deterministically emits `.di-agebar`
/// markup (the render agent is explicitly told NOT to author it - see the prompt's own "Do
/// not write di-agebar..." rule), but no CSS for any `.di-agebar*` selector was ever declared
/// anywhere: not in the render agent's own &lt;style&gt; block instructions, not by any
/// injector. The bar rendered with real bucket counts and real widths but zero colour and no
/// bar chrome - confirmed by inspecting the prompt file directly, not assumed. This CSS is
/// 100% static (zero data substitution, identical every render), so - same reasoning as
/// Coverage - there is no reason to gamble on the render agent authoring it when it already
/// isn't trusted to author the markup it decorates. Inserted last inside &lt;head&gt;, so it
/// wins any cascade conflict if the render agent's own &lt;style&gt; block happens to declare
/// something for the same selectors.
/// </summary>
public static partial class BacklogAgeBarCssInjector
{
    private const string Css = """
        <style>
        .di-agebar{display:flex;flex-direction:column;gap:8px}
        .di-agebar__scale{height:18px;display:flex;border-radius:var(--r-md);border:1px solid var(--c-border)}
        .di-agebar__seg{position:relative;height:100%;display:flex;align-items:center;justify-content:center;font-size:var(--fs-di-chip);font-weight:500;color:#ffffff;border-right:1px solid rgba(255,255,255,.28);white-space:nowrap;padding:0 6px;min-width:0}
        .di-agebar__seg:first-child{border-top-left-radius:calc(var(--r-md) - 1px);border-bottom-left-radius:calc(var(--r-md) - 1px)}
        .di-agebar__seg:last-child{border-right:0;border-top-right-radius:calc(var(--r-md) - 1px);border-bottom-right-radius:calc(var(--r-md) - 1px)}
        .di-agebar__seg--minor{min-width:8px}
        .di-agebar__seg--minor .di-agebar__seglabel{display:none}
        .di-agebar__seglabel{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:0;max-width:100%}
        .di-agebar__seg--bad{background:#d24a3a}
        .di-agebar__seg--warn{background:#e0a106;color:#5a3d00}
        .di-agebar__seg--neu{background:#8a8f99}
        .di-agebar__tip{position:absolute;bottom:calc(100% + 7px);left:50%;transform:translateX(-50%);z-index:20;background:#2c2c2c;color:#ffffff;font-size:var(--fs-meta);font-weight:500;padding:4px 9px;border-radius:5px;white-space:nowrap;pointer-events:none;opacity:0;visibility:hidden;transition:opacity .12s ease;box-shadow:0 4px 12px rgba(20,28,48,.18)}
        .di-agebar__tip::after{content:'';position:absolute;top:100%;left:50%;transform:translateX(-50%);border:5px solid transparent;border-top-color:#2c2c2c}
        .di-agebar__seg:hover .di-agebar__tip{opacity:1;visibility:visible}
        .di-agebar__seg:first-child .di-agebar__tip{left:0;transform:none}
        .di-agebar__seg:first-child .di-agebar__tip::after{left:18px;transform:none}
        .di-agebar__seg:last-child .di-agebar__tip{left:auto;right:0;transform:none}
        .di-agebar__seg:last-child .di-agebar__tip::after{left:auto;right:18px;transform:none}
        .di-agebar .di-stacklegend{display:flex;flex-wrap:wrap;gap:14px;row-gap:6px;font-size:var(--fs-meta);color:var(--c-text-3);margin-top:2px}
        .di-agebar .di-stacklegend__item{display:inline-flex;align-items:center;gap:6px}
        .di-agebar .di-stacklegend__sw{width:8px;height:8px;border-radius:2px;flex-shrink:0}
        .di-agebar .di-stacklegend__item b{color:var(--c-text);font-weight:600}
        </style>
        """;

    public static string Inject(string html)
    {
        if (!html.Contains("di-agebar", StringComparison.Ordinal))
            return html; // no age-bar rendered this run (blocked/degraded/zero-overdue) - nothing to style.

        var match = HeadCloseTag().Match(html);
        if (!match.Success)
            throw new InvalidOperationException("BacklogAgeBarCssInjector: no </head> tag found in generated HTML - cannot inject the age-bar CSS.");

        return html.Insert(match.Index, Css);
    }

    [GeneratedRegex(@"</head\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadCloseTag();
}
