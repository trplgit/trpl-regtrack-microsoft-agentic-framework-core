using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-09-23, LAB] Real gap found live: `05_report_html_fixed_holistic.md` was updated to
/// explicitly forbid writing a `&lt;footer class="di-caveats"&gt;` section at all (tenant decision -
/// caveats belong inline with the value they qualify, per hard rule 4, not in a second standalone
/// dump that eats real vertical space) - and the render agent kept writing one anyway on the very
/// next real run, confirmed live against Minda tenant 1008. Same class of problem this file's other
/// deterministic injectors already solve (CoverageGridInjector, BacklogAgeBarInjector,
/// ForwardLookInjector's own doc comments): a single negative instruction buried in a genuinely huge
/// prompt is not something to trust an LLM to honour every run. This strips whatever the render
/// agent wrote regardless, deterministically, rather than re-litigating the prompt wording.
/// </summary>
public static partial class CaveatsFooterRemover
{
    public static string Remove(string html) => FooterToken().Replace(html, "");

    [GeneratedRegex(@"<footer\b[^>]*\bclass=""di-caveats""[^>]*>.*?</footer>", RegexOptions.Singleline)]
    private static partial Regex FooterToken();
}
