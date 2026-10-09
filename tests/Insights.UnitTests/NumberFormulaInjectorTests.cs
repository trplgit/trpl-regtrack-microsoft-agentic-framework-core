using Insights.Presentation;

namespace Insights.UnitTests;

public sealed class NumberFormulaInjectorTests
{
    private static readonly NumberFormulaInjector.Figure ActiveFigure = new(
        "Active licences", "21", "Counted from each licence's current recorded status: this is how many have the status 'Active' right now.");

    // [REGRESSION, found live via user-supplied screenshots 2026-10-01] The trigger label carried a
    // `title` attribute, so the browser's own native tooltip rendered as a second black box stacked
    // on top of the custom `.hr-panel` on every single hover-link - across every dimension that uses
    // this shared mechanism.
    [Fact]
    public void Trigger_NeverCarriesANativeTitleAttribute()
    {
        var result = NumberFormulaInjector.Inject("<html><body><p>t</p></body></html>", [ActiveFigure]);

        Assert.DoesNotMatch("""class="hr-i[^"]*"[^>]*\stitle=""", result);
    }

    [Fact]
    public void WithNoFigures_ReturnsHtmlUnchanged()
    {
        const string html = "<html><body><p>t</p></body></html>";

        var result = NumberFormulaInjector.Inject(html, []);

        Assert.Equal(html, result);
    }

    [Fact]
    public void WithNoClosingBodyTag_ReturnsHtmlUnchanged()
    {
        const string html = "<html><body><p>t</p>";

        var result = NumberFormulaInjector.Inject(html, [ActiveFigure]);

        Assert.Equal(html, result);
    }

    [Fact]
    public void InsertsTheBlockBeforeTheClosingBodyTag()
    {
        const string html = "<html><body><p>t</p></body></html>";

        var result = NumberFormulaInjector.Inject(html, [ActiveFigure]);

        var blockIndex = result.IndexOf("How your numbers are worked out", StringComparison.Ordinal);
        var bodyCloseIndex = result.IndexOf("</body>", StringComparison.Ordinal);
        Assert.True(blockIndex >= 0, "block missing");
        Assert.True(blockIndex < bodyCloseIndex, "block must sit before </body>");
    }

    [Fact]
    public void CarriesTheRealLabelValueAndPanelTextVerbatim()
    {
        var result = NumberFormulaInjector.Inject("<html><body></body></html>", [ActiveFigure]);

        Assert.Contains("Active licences", result);
        Assert.Contains(">21<", result);
        Assert.Contains(
            "Counted from each licence&#39;s current recorded status: this is how many have the status &#39;Active&#39; right now.",
            result);
    }

    [Fact]
    public void SkipsAFigureWithNoRealValue()
    {
        var blank = new NumberFormulaInjector.Figure("Terminated", "", "some text");

        var result = NumberFormulaInjector.Inject("<html><body></body></html>", [blank]);

        Assert.Equal("<html><body></body></html>", result);
    }

    [Fact]
    public void EachFigureGetsItsOwnUniqueId()
    {
        var second = new NumberFormulaInjector.Figure("Expired licences", "3", "some other text");

        var result = NumberFormulaInjector.Inject("<html><body></body></html>", [ActiveFigure, second]);

        Assert.Contains("id=\"mi-0\"", result);
        Assert.Contains("id=\"mi-1\"", result);
    }

    [Fact]
    public void WorksEvenWhenTheDocumentDeclaredNoChartCssAtAll()
    {
        // No .hr/.hr-panel CSS anywhere in the source document - this injector must carry its own.
        const string html = "<html><head><style>body{color:red}</style></head><body></body></html>";

        var result = NumberFormulaInjector.Inject(html, [ActiveFigure]);

        Assert.Contains(".hr-panel{position:fixed", result);
        Assert.Contains(".mi-nf{", result);
    }

    [Fact]
    public void EncodesHostileLabelAndPanelTextInsteadOfBreakingOutOfTheMarkup()
    {
        var hostile = new NumberFormulaInjector.Figure("</label><script>alert(1)</script>", "5", "</aside>bad");

        var result = NumberFormulaInjector.Inject("<html><body></body></html>", [hostile]);

        Assert.DoesNotContain("<script>alert(1)</script>", result);
        Assert.Contains("&lt;script&gt;", result);
    }

    // [ADDED 2026-10-08, user-reported] A real rendered panel ("Company-wide overdue percentage
    // (across all departments with scheduled work)") ran past the panel's own right edge instead
    // of wrapping - `.pf-diff-label` was `white-space:nowrap`, and flex items default to
    // `min-width:auto`, so a row holding unbreakable text is never clamped to its flex container's
    // width even when that container has a fixed max-width. Both halves of the fix are required:
    // `min-width:0` on the row (lets it actually shrink) and dropping `nowrap` on the label (lets
    // the now-shrinkable text wrap instead of overflowing).
    [Fact]
    public void PfDiffLabel_WrapsLongText_InsteadOfOverflowingThePanel()
    {
        var result = NumberFormulaInjector.Inject("<html><body></body></html>", [ActiveFigure]);

        Assert.Contains(".pf-diff-row{display:flex;align-items:baseline;gap:8px;justify-content:center;min-width:0;max-width:100%}", result);
        Assert.Contains(".pf-diff-label{font-size:11px;color:#585858;white-space:normal;overflow-wrap:break-word;text-align:left}", result);
    }

    /// <summary>A freehand render's real shape varies (status-strip/bubbles/heat-table layouts,
    /// varying outer container class names - see this injector's own doc comment on the 3 real
    /// tenant-1285 trials that motivated it) but always has multiple scripts, inline styles and a
    /// single closing body tag. No real tenant content here - see git history for why a real
    /// downloaded report is never committed as a fixture.</summary>
    [Fact]
    public void WorksAgainstAMultiScriptFreehandShapedDocument()
    {
        const string html = """
            <!DOCTYPE html><html><head><meta charset="utf-8"><style>:root{--c-brand:#125aab}</style></head>
            <body><section class="hero"><h1>Headline</h1></section>
            <script type="application/json" id="insights-data">{"rows":[]}</script>
            <script>var x = 1;</script>
            </body></html>
            """;

        var result = NumberFormulaInjector.Inject(html, [ActiveFigure]);

        Assert.Contains("How your numbers are worked out", result);
        Assert.True(result.Length > html.Length);
        // The document's own content must survive untouched - this injector only appends.
        Assert.Contains("<h1>Headline</h1>", result);
        Assert.Contains("var x = 1;", result);
    }
}
