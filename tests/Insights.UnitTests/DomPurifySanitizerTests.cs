using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// ReattachDoctypeIfMissing in isolation - confirmed necessary via a live run (2026-08-20) where
/// DOMPurify's own WHOLE_DOCUMENT serialization dropped &lt;!DOCTYPE html&gt; from otherwise-
/// correctly-sanitized output.
/// </summary>
public sealed class DomPurifySanitizerTests
{
    [Fact]
    public void ReattachDoctypeIfMissing_AddsDoctype_WhenDomPurifyDroppedIt()
    {
        const string sanitized = "<html lang=\"en\"><head></head><body>content</body></html>";

        var result = DomPurifySanitizer.ReattachDoctypeIfMissing(sanitized);

        Assert.StartsWith("<!DOCTYPE html>", result, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(sanitized, result, StringComparison.Ordinal);
    }

    [Fact]
    public void ReattachDoctypeIfMissing_LeavesInputUnchanged_WhenDoctypeAlreadyPresent()
    {
        const string sanitized = "<!DOCTYPE html><html lang=\"en\"><head></head><body>content</body></html>";

        var result = DomPurifySanitizer.ReattachDoctypeIfMissing(sanitized);

        Assert.Equal(sanitized, result);
    }
}
