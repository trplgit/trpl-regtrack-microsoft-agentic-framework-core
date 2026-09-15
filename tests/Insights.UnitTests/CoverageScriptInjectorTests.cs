using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// [BUG FOUND LIVE, 2026-09-02] The Coverage-tile driving script is fixed, tested, tenant-agnostic
/// boilerplate - it only ever reads data-* attributes the LLM already writes reliably (di-covtile/
/// di-covgrid have shown up correctly across every render this session; the SCRIPT specifically is
/// what has been unreliable, three different ways: DOMPurify's default strip, DOMPurify's
/// tag-shaped-content strip, and the LLM simply omitting it on a given attempt). Same reasoning as
/// PoppinsFontInjector's own doc comment: no reason to gamble on the LLM re-authoring known-correct
/// JS every render when it can be injected deterministically instead - the LLM's job becomes just
/// the grid markup with correct data attributes, which it already does reliably.
/// </summary>
public sealed class CoverageScriptInjectorTests
{
    private const string DocumentWithCoverageGrid = """
        <!DOCTYPE html><html><head><meta charset="utf-8"></head><body>
        <section class="di-pane" id="di-pane-3" aria-label="Coverage">
          <div class="di-covwrap"><div class="di-covmap"><div class="di-covregion"><div class="di-covgrid">
            <button type="button" class="di-covtile" data-st="healthy" data-branch-id="B1"></button>
          </div></div></div><aside class="di-covdetail di-covdetail--side"></aside></div>
        </section>
        </body></html>
        """;

    private const string DocumentWithNoCoverageGrid = """
        <!DOCTYPE html><html><head><meta charset="utf-8"></head><body>
        <h1>No coverage pane in this fixture</h1>
        </body></html>
        """;

    [Fact]
    public void Inject_DocumentWithCoverageGrid_AddsTheDrivingScriptRightBeforeBodyClose()
    {
        var result = CoverageScriptInjector.Inject(DocumentWithCoverageGrid);

        var scriptIndex = result.IndexOf("<script>", StringComparison.Ordinal);
        var bodyCloseIndex = result.IndexOf("</body>", StringComparison.Ordinal);
        Assert.True(scriptIndex >= 0, "expected a <script> tag to be injected");
        Assert.True(scriptIndex < bodyCloseIndex, "script must sit before </body>");
        Assert.Contains(".di-covmap", result, StringComparison.Ordinal);
    }

    /// <summary>The injected script must never use innerHTML with a literal HTML tag - see ReportEmitNormalizer.CheckNoHtmlTagShapedTextInsideScripts's own [BUG FOUND LIVE] note for why.</summary>
    [Fact]
    public void Inject_InjectedScript_NeverUsesInnerHtmlWithALiteralHtmlTag()
    {
        var result = CoverageScriptInjector.Inject(DocumentWithCoverageGrid);
        var normalizerResult = ReportEmitNormalizer.Evaluate(result);

        Assert.True(normalizerResult.Approved, string.Join("; ", normalizerResult.Violations));
    }

    [Fact]
    public void Inject_DocumentWithNoCoverageGrid_LeavesDocumentUnchanged()
    {
        // No di-covgrid/di-covtile at all - nothing for this script to drive. Never inject
        // dead code into a document that never rendered the grid (e.g. a non-fixed-holistic
        // report shape, or a lab fixture that legitimately has no Coverage pane).
        var result = CoverageScriptInjector.Inject(DocumentWithNoCoverageGrid);

        Assert.Equal(DocumentWithNoCoverageGrid, result);
    }

    [Fact]
    public void Inject_DocumentAlreadyHasADrivingScript_DoesNotInjectASecondOne()
    {
        // Idempotent - if the render agent (against updated instructions) still wrote its own
        // driving script, injecting a second one would double-bind every click handler.
        var alreadyWired = DocumentWithCoverageGrid.Replace(
            "</body>", "<script>document.querySelector('.di-covmap').addEventListener('click', function(){});</script></body>");

        var result = CoverageScriptInjector.Inject(alreadyWired);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "<script"));
    }

    [Fact]
    public void Inject_NoBodyCloseTag_ThrowsRatherThanShipSilently()
    {
        var html = DocumentWithCoverageGrid.Replace("</body></html>", "");

        Assert.Throws<InvalidOperationException>(() => CoverageScriptInjector.Inject(html));
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] The generated report used an invented 3-state model
    /// (healthy/dark "No obligations"/has_ownerless) that silently dropped RED entirely and
    /// renamed under_configured to something else - confirmed against the real reference
    /// (detailed-insights.data.ts's CovStatus type and detailed-insights.component.ts's
    /// covStatusLabel) AND this repo's own docs/PAID_TIER_SAMPLE_REFERENCE.md Sec.3.4
    /// (status_counts: healthy/under_configured/has_ownerless/unmapped, same 4 names) - the
    /// reference taxonomy is not a mock invention, it is this repo's own documented target.
    /// The four real labels/statuses must all be present in the injected script.
    /// </summary>
    [Theory]
    [InlineData("healthy", "Mapped")]
    [InlineData("under_configured", "Under-configured")]
    [InlineData("has_ownerless", "Has ownerless")]
    [InlineData("unmapped", "Unmapped")]
    public void Inject_UsesTheRealFourStateTaxonomy_NotAnInventedThreeStateOne(string statusKey, string label)
    {
        var result = CoverageScriptInjector.Inject(DocumentWithCoverageGrid);

        // Unquoted object-literal key (valid JS identifier), not a quoted string - "healthy:" not "'healthy':".
        Assert.Contains($"{statusKey}:", result, StringComparison.Ordinal);
        Assert.Contains($"'{label}'", result, StringComparison.Ordinal);
    }

    /// <summary>Confirmed real hex values, detailed-insights.component.css lines 1047-1050/1067-1070 - green/yellow/orange/red, in that severity order.</summary>
    [Fact]
    public void Inject_DrivingScriptDoesNotHardcodeColours_ColoursLiveInCssOnly()
    {
        // The script itself only ever toggles classNames (di-covtile--{status}) - colours are a
        // CSS concern (Tab 3's <style> block), not something the script hardcodes. This just
        // pins that the class-name pattern the script emits matches the CSS selectors that
        // actually carry the 4 real colours.
        var result = CoverageScriptInjector.Inject(DocumentWithCoverageGrid);

        Assert.Contains("di-covdetail__pill--' + st", result, StringComparison.Ordinal);
    }
}
