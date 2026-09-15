using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// Deterministically embeds the Forward-look card's CSS (the `.di-kpi--fwd` margin rhythm, the
/// `.di-kpi__narr--alert` figure-only red, and the whole `.di-fwd` bucket-chart recipe) - same
/// treatment CoverageCssInjector gives the Coverage pane and BacklogAgeBarCssInjector gives the
/// age bar.
///
/// [BUG FOUND LIVE, 2026-09-10] When Tab 5 became "fully injected, author only the shell"
/// (ForwardLookInjector now owns the entire pane markup), the render agent stopped emitting the
/// Tab 5 CSS block too - it no longer felt responsible for a section it barely touches. Confirmed
/// live on a real tenant-1300 run: `.di-fwd`, `.di-fwd__col`, `.di-fwd__bar`, `.di-fwd__axis`,
/// `.di-kpi--fwd` and `.di-kpi__narr--alert` were all absent, so the injected bucket chart had
/// zero height and the axis labels ran together as one line. The markup is injected, so its CSS
/// must be too. Inserted last inside &lt;head&gt; so it wins any cascade conflict. Copied verbatim
/// from `reference/holistic-insights-tenant1300.html`.
/// </summary>
public static partial class ForwardLookCssInjector
{
    private const string Css = """
        <style>
        .di-kpi--fwd{gap:0}
        .di-kpi .di-kpi__big{margin:10px 0 0}
        .di-kpi .di-kpi__narr{margin:.55rem 0 0}
        .di-kpi .di-stackbar{margin:10px 0 0}
        .di-kpi .di-stacklegend{margin:6px 0 0}
        .di-kpi__narr--alert{color:var(--c-text-2)}
        .di-kpi__narr--alert b{color:#b3261e}
        .di-kpi .di-fwd{margin:12px 0 0;height:56px}
        .di-kpi .di-fwd__axis{margin:4px 0 0}
        .di-kpi .di-fwd__axis + .di-kpi__narr{margin-top:.55rem}
        .di-fwd{display:grid;grid-template-columns:repeat(5,1fr);gap:6px;height:64px;margin-top:12px;border-bottom:1px solid var(--c-border)}
        .di-fwd__col{position:relative;min-width:0}
        .di-fwd__bar{position:absolute;left:22%;right:22%;bottom:0;border-radius:3px 3px 0 0;background:#8a8f99}
        .di-fwd__bar--ok{background:#2e9e5b}
        .di-fwd__bar--warn{background:#e0a106}
        .di-fwd__bar--bad{background:#d24a3a}
        .di-fwd__axis{display:grid;grid-template-columns:repeat(5,1fr);gap:6px;text-align:center;font-size:var(--fs-meta);color:var(--c-grey);margin-top:6px}
        </style>
        """;

    public static string Inject(string html)
    {
        if (!html.Contains("di-kpi--fwd", StringComparison.Ordinal) && !html.Contains("class=\"di-fwd\"", StringComparison.Ordinal))
            return html; // no forward card rendered this run (both sources degraded) - nothing to style.

        var match = HeadCloseTag().Match(html);
        if (!match.Success)
            throw new InvalidOperationException("ForwardLookCssInjector: no </head> tag found in generated HTML - cannot inject the Forward-look CSS.");

        return html.Insert(match.Index, Css);
    }

    [GeneratedRegex(@"</head\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadCloseTag();
}
