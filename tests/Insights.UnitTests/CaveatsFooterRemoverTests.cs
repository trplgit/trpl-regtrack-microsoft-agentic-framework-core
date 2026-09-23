using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// [ADDED 2026-09-23] The render agent kept writing a `di-caveats` footer even after
/// 05_report_html_fixed_holistic.md was updated to forbid it outright - confirmed live. This
/// strips it deterministically regardless, same "don't trust the LLM to honour a single negative
/// instruction" reasoning CoverageGridInjectorTests already pins for its own class.
/// </summary>
public sealed class CaveatsFooterRemoverTests
{
    [Fact]
    public void Remove_StripsARealCaveatsFooter_Entirely()
    {
        const string html =
            "<body><section>real content</section>" +
            "<footer class=\"di-caveats\" aria-label=\"Data-quality caveats\">" +
            "<div class=\"di-caveats__eyebrow\">Data-quality caveats</div>" +
            "<ul class=\"di-caveats__list\"><li class=\"di-caveats__item\">a real note</li></ul>" +
            "</footer></body>";

        var result = CaveatsFooterRemover.Remove(html);

        Assert.DoesNotContain("di-caveats", result, StringComparison.Ordinal);
        Assert.Contains("real content", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_NoFooterPresent_LeavesDocumentUnchanged()
    {
        const string html = "<body><section>real content</section></body>";

        Assert.Equal(html, CaveatsFooterRemover.Remove(html));
    }

    [Fact]
    public void Remove_OnlyStripsTheCaveatsFooter_LeavesOtherFootersAlone()
    {
        const string html =
            "<footer class=\"di-caveats\">caveats</footer>" +
            "<footer class=\"other-footer\">keep me</footer>";

        var result = CaveatsFooterRemover.Remove(html);

        Assert.DoesNotContain("di-caveats", result, StringComparison.Ordinal);
        Assert.Contains("keep me", result, StringComparison.Ordinal);
    }
}
