using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// [BUG FOUND LIVE, 2026-09-10] Once Tab 5 became "author only the shell", the render agent
/// stopped emitting the Tab 5 CSS block, so the deterministically-injected `.di-fwd` bucket
/// chart rendered with zero height and its axis labels ran together. Injected markup needs
/// injected CSS - same fix as CoverageCssInjector / BacklogAgeBarCssInjector.
/// </summary>
public sealed class ForwardLookCssInjectorTests
{
    private const string DocWithForwardCard =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>body{color:red}</style></head><body>" +
        "<article class=\"di-kpi di-kpi--span12 di-kpi--fwd\"><div class=\"di-fwd\"><div class=\"di-fwd__col\"><i class=\"di-fwd__bar\"></i></div></div></article>" +
        "</body></html>";

    private const string DocWithNoForwardCard =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>no forward card here</body></html>";

    [Fact]
    public void Inject_DocumentWithForwardCard_AddsTheBucketChartAndAlertRules()
    {
        var result = ForwardLookCssInjector.Inject(DocWithForwardCard);

        Assert.Contains(".di-fwd{display:grid;grid-template-columns:repeat(5,1fr)", result, StringComparison.Ordinal);
        Assert.Contains(".di-fwd__bar{position:absolute", result, StringComparison.Ordinal);
        Assert.Contains(".di-fwd__axis{display:grid;grid-template-columns:repeat(5,1fr)", result, StringComparison.Ordinal);
        Assert.Contains(".di-kpi__narr--alert b{color:#b3261e}", result, StringComparison.Ordinal);
        Assert.Contains(".di-kpi--fwd{gap:0}", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_InsertsRightBeforeHeadClose_SoItWinsAnyCascadeConflict()
    {
        var result = ForwardLookCssInjector.Inject(DocWithForwardCard);

        var injectedIdx = result.IndexOf(".di-fwd__col{position:relative", StringComparison.Ordinal);
        var headCloseIdx = result.IndexOf("</head>", StringComparison.Ordinal);
        var agentStyleIdx = result.IndexOf("body{color:red}", StringComparison.Ordinal);

        Assert.True(agentStyleIdx < injectedIdx, "injected CSS must come after the render agent's own <style> block");
        Assert.True(injectedIdx < headCloseIdx, "injected CSS must still be inside <head>");
    }

    [Fact]
    public void Inject_DocumentWithNoForwardCard_LeavesDocumentUnchanged()
    {
        var result = ForwardLookCssInjector.Inject(DocWithNoForwardCard);

        Assert.Equal(DocWithNoForwardCard, result);
    }

    [Fact]
    public void Inject_NoHeadTag_ThrowsRatherThanShipSilently()
    {
        const string noHead = "<!DOCTYPE html><html><body><article class=\"di-kpi--fwd\"></article></body></html>";

        Assert.Throws<InvalidOperationException>(() => ForwardLookCssInjector.Inject(noHead));
    }
}
