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
    public void Inject_ValidDocument_EmbedsBothWeights400And600()
    {
        var result = PoppinsFontInjector.Inject(ValidDocument);

        Assert.Contains("font-weight:400", result, StringComparison.Ordinal);
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
}
