using Insights.Presentation;
using Xunit;

namespace Insights.UnitTests;

public class PoppinsFontInjectorTests
{
    // Matches ReportEmitNormalizerTests.ValidDocument's shape.
    private const string ValidDocument = """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8">
        <style>body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }</style>
        </head>
        <body>
        <h1>Compliance Health Report</h1>
        </body></html>
        """;

    [Fact]
    public void Inject_ValidDocument_AddsFontFaceRightAfterHeadOpenTag()
    {
        var result = PoppinsFontInjector.Inject(ValidDocument);

        var headIndex = result.IndexOf("<head>", StringComparison.Ordinal);
        var styleIndex = result.IndexOf("<style>@font-face", StringComparison.Ordinal);
        Assert.True(headIndex >= 0);
        Assert.True(styleIndex > headIndex);
        // Nothing from the original document should sit between <head> and the injected block.
        Assert.Equal(headIndex + "<head>".Length, styleIndex);
    }

    [Fact]
    public void Inject_ValidDocument_EmbedsRealFontBytesNotAStub()
    {
        var result = PoppinsFontInjector.Inject(ValidDocument);

        // Pull the first data: URI's base64 payload back out and decode it - this is the same
        // check that caught the LLM-authored-font trap in the first place (a real WOFF2 starts
        // with the 4-byte magic number "wOF2" and is thousands of bytes long, not six).
        var marker = "base64,";
        var start = result.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = result.IndexOf(')', start);
        var base64 = result[start..end];
        var bytes = Convert.FromBase64String(base64);

        Assert.True(bytes.Length > 1000, $"expected real glyph data, got {bytes.Length} bytes");
        Assert.Equal("wOF2", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Inject_ValidDocument_EmbedsAllThreeWeights400And500And600()
    {
        // [ADDED 2026-09-09] Weight 500 joined 400/600 once a design system that actually uses
        // it (Sambram's AI-INSIGHTS-BRAND-HANDOFF.md - .di-band/.di-tonetag/.di-kpi__num small
        // are all weight 500) arrived. A declared weight with no embedded face doesn't error -
        // it silently renders as the nearest available weight instead, per this file's own
        // vendor/README.md note.
        var result = PoppinsFontInjector.Inject(ValidDocument);

        Assert.Contains("font-weight:400", result, StringComparison.Ordinal);
        Assert.Contains("font-weight:500", result, StringComparison.Ordinal);
        Assert.Contains("font-weight:600", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_NoHeadTag_ThrowsRatherThanShipSilently()
    {
        var html = "<!DOCTYPE html><html><body>no head here</body></html>";

        Assert.Throws<InvalidOperationException>(() => PoppinsFontInjector.Inject(html));
    }

    /// <summary>
    /// HasHeadTag is the SAME check Inject uses internally, exposed so MafReportHtmlAgent can
    /// fail fast on a malformed/truncated render inside the activity ScheduleWithRetry wraps -
    /// see ReportHtmlAgent.cs's own [BUG FOUND LIVE] note for why one call site was not enough.
    /// </summary>
    [Fact]
    public void HasHeadTag_ValidDocument_ReturnsTrue()
    {
        Assert.True(PoppinsFontInjector.HasHeadTag(ValidDocument));
    }

    [Fact]
    public void HasHeadTag_NoHeadTag_ReturnsFalse()
    {
        var html = "<!DOCTYPE html><html><body>no head here</body></html>";

        Assert.False(PoppinsFontInjector.HasHeadTag(html));
    }

    // [ADDED 2026-10-08, FOUND LIVE] A real Motul BacklogAging report shipped with its self-hosted
    // font silently broken: PatchRenderActivity re-sends the WHOLE document (by this point already
    // carrying the ~30KB of real, opaque base64 glyph data Inject() embeds) through an LLM call
    // that is asked to "patch the findings, preserve everything else" - LLMs cannot reliably copy
    // tens of thousands of characters of binary data byte-for-byte. The real report that shipped
    // was missing 2 of 3 weight blocks, and the ONE it kept was itself corrupted (one character
    // short of the real file, enough to break WOFF2 decoding) - the exact trap this class's own
    // top doc comment already named for the FIRST render, reopened by the patch loop. Strip() lets
    // the orchestrator remove the font block before a patch call (so the LLM never has to touch it
    // at all) and re-inject a guaranteed-correct copy via Inject() afterward.
    [Fact]
    public void Strip_DocumentWithInjectedFont_RemovesTheWholeFontStyleBlock()
    {
        var injected = PoppinsFontInjector.Inject(ValidDocument);

        var stripped = PoppinsFontInjector.Strip(injected);

        Assert.DoesNotContain("@font-face", stripped);
        Assert.DoesNotContain("Poppins", stripped);
        // Everything else the injected document carried must survive untouched.
        Assert.Contains("<h1>Compliance Health Report</h1>", stripped);
    }

    [Fact]
    public void Strip_ThenInject_ProducesByteIdenticalFontBlockToASingleInject()
    {
        // The real guarantee this round-trip exists for: strip-then-reinject must never leave a
        // degraded or duplicated font behind - it must come back out EXACTLY as if Inject() had
        // only ever been called once, on the original document.
        var oneShot = PoppinsFontInjector.Inject(ValidDocument);
        var roundTripped = PoppinsFontInjector.Inject(PoppinsFontInjector.Strip(PoppinsFontInjector.Inject(ValidDocument)));

        Assert.Equal(oneShot, roundTripped);
    }

    [Fact]
    public void Strip_DocumentWithNoInjectedFont_ReturnsInputUnchanged()
    {
        var result = PoppinsFontInjector.Strip(ValidDocument);

        Assert.Equal(ValidDocument, result);
    }
}
