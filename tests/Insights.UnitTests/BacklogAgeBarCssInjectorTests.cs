using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// [BUG FOUND LIVE, 2026-09-09] BacklogAgeBarInjector deterministically emits `.di-agebar`
/// markup (segments, tooltip, axis), but no CSS for any of those selectors was ever declared
/// anywhere - not in the render agent's prompt, not by any injector. The bar rendered
/// completely unstyled: no colours on the 3 age segments, no bar chrome, no tooltip. Same fix
/// as CoverageCssInjector - the CSS is 100% static (zero data substitution, identical every
/// render), so there is no reason to ask the render agent to author it when it already isn't
/// trusted to author the markup it decorates.
/// </summary>
public sealed class BacklogAgeBarCssInjectorTests
{
    private const string DocumentWithAgeBar =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>body{color:red}</style></head><body>" +
        "<div class=\"di-agebar\"><div class=\"di-agebar__scale\">" +
        "<div class=\"di-agebar__seg di-agebar__seg--bad\"><span class=\"di-agebar__seglabel\">12 &middot; pre-FY24</span></div>" +
        "</div></div>" +
        "</body></html>";

    private const string DocumentWithNoAgeBar =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>no age bar here</body></html>";

    [Fact]
    public void Inject_DocumentWithAgeBar_AddsAllThreeToneColours()
    {
        var result = BacklogAgeBarCssInjector.Inject(DocumentWithAgeBar);

        Assert.Contains(".di-agebar__seg--bad{background:#d24a3a}", result, StringComparison.Ordinal);
        Assert.Contains(".di-agebar__seg--warn{background:#e0a106;color:#5a3d00}", result, StringComparison.Ordinal);
        Assert.Contains(".di-agebar__seg--neu{background:#8a8f99}", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_DocumentWithAgeBar_AddsScaleAndTooltipShapeRules()
    {
        var result = BacklogAgeBarCssInjector.Inject(DocumentWithAgeBar);

        Assert.Contains(".di-agebar__scale{", result, StringComparison.Ordinal);
        Assert.Contains(".di-agebar__tip{", result, StringComparison.Ordinal);
        Assert.Contains(".di-agebar__seg:hover .di-agebar__tip{opacity:1;visibility:visible}", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_InsertsRightBeforeHeadClose_SoItWinsAnyCascadeConflict()
    {
        var result = BacklogAgeBarCssInjector.Inject(DocumentWithAgeBar);

        var injectedIdx = result.IndexOf(".di-agebar__seg--bad", StringComparison.Ordinal);
        var headCloseIdx = result.IndexOf("</head>", StringComparison.Ordinal);
        var agentStyleIdx = result.IndexOf("body{color:red}", StringComparison.Ordinal);

        Assert.True(agentStyleIdx < injectedIdx, "injected CSS must come after the render agent's own <style> block");
        Assert.True(injectedIdx < headCloseIdx, "injected CSS must still be inside <head>");
    }

    [Fact]
    public void Inject_DocumentWithNoAgeBar_LeavesDocumentUnchanged()
    {
        var result = BacklogAgeBarCssInjector.Inject(DocumentWithNoAgeBar);

        Assert.Equal(DocumentWithNoAgeBar, result);
    }

    [Fact]
    public void Inject_NoHeadTag_ThrowsRatherThanShipSilently()
    {
        const string noHead = "<!DOCTYPE html><html><body><div class=\"di-agebar\"></div></body></html>";

        Assert.Throws<InvalidOperationException>(() => BacklogAgeBarCssInjector.Inject(noHead));
    }
}
