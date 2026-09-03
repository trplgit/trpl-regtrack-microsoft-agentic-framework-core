using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// [BUG FOUND LIVE, 2026-09-02] The Coverage CSS (4 status colours on chips/tiles/pills, layout)
/// was the one remaining piece still asked of the render agent as "declare these rules verbatim" -
/// confirmed live: tiles rendered as unfilled outline boxes and the detail pill had no background
/// colour at all, meaning the LLM's own &lt;style&gt; block silently dropped or malformed the
/// colour declarations this run. Same fix as the font, the grid, and the driving script - the CSS
/// is 100% static (zero data substitution, identical every render), so there is no reason to
/// gamble on the render agent copying it correctly. Injected last inside &lt;head&gt; so it wins
/// any cascade conflict with whatever the render agent's own &lt;style&gt; block still contains.
/// </summary>
public sealed class CoverageCssInjectorTests
{
    private const string DocumentWithGrid =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>body{color:red}</style></head><body>" +
        "<div class=\"di-covgrid\"><button class=\"di-covtile di-covtile--healthy\"></button></div>" +
        "</body></html>";

    private const string DocumentWithNoCoveragePane =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>no coverage here</body></html>";

    [Fact]
    public void Inject_DocumentWithCoverageGrid_AddsAllFourStatusColoursForTilesChipsAndPills()
    {
        var result = CoverageCssInjector.Inject(DocumentWithGrid);

        foreach (var status in new[] { "healthy", "under_configured", "has_ownerless", "unmapped" })
        {
            Assert.Contains($".di-covtile--{status}{{background:", result, StringComparison.Ordinal);
            Assert.Contains($".di-covchip__sw--{status}{{background:", result, StringComparison.Ordinal);
            Assert.Contains($".di-covdetail__pill--{status}{{background:", result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Inject_UsesTheRealReferenceHexValues()
    {
        var result = CoverageCssInjector.Inject(DocumentWithGrid);

        Assert.Contains(".di-covtile--healthy{background:#2e9e5b}", result, StringComparison.Ordinal);
        Assert.Contains(".di-covtile--under_configured{background:#e0a106}", result, StringComparison.Ordinal);
        Assert.Contains(".di-covtile--has_ownerless{background:#e07a1f}", result, StringComparison.Ordinal);
        Assert.Contains(".di-covtile--unmapped{background:#c0392b}", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_InsertsRightBeforeHeadClose_SoItWinsAnyCascadeConflict()
    {
        var result = CoverageCssInjector.Inject(DocumentWithGrid);

        var injectedIdx = result.IndexOf(".di-covtile--healthy", StringComparison.Ordinal);
        var headCloseIdx = result.IndexOf("</head>", StringComparison.Ordinal);
        var agentStyleIdx = result.IndexOf("body{color:red}", StringComparison.Ordinal);

        Assert.True(agentStyleIdx < injectedIdx, "injected CSS must come after the render agent's own <style> block");
        Assert.True(injectedIdx < headCloseIdx, "injected CSS must still be inside <head>");
    }

    [Fact]
    public void Inject_DocumentWithNoCoveragePane_LeavesDocumentUnchanged()
    {
        var result = CoverageCssInjector.Inject(DocumentWithNoCoveragePane);

        Assert.Equal(DocumentWithNoCoveragePane, result);
    }

    [Fact]
    public void Inject_NoHeadTag_ThrowsRatherThanShipSilently()
    {
        const string noHead = "<!DOCTYPE html><html><body><div class=\"di-covgrid\"></div></body></html>";

        Assert.Throws<InvalidOperationException>(() => CoverageCssInjector.Inject(noHead));
    }
}
