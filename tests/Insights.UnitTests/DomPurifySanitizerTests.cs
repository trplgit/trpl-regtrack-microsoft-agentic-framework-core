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

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] Same class of DOMPurify WHOLE_DOCUMENT serialization quirk as
    /// the doctype-drop above, confirmed via a real chain run against a real rendered report: the
    /// document went in with a real "meta charset=utf-8" (the render agent's own doing, per the
    /// prompt's Output Constraints rule) and came out of the real vendored DOMPurify without it.
    /// Since ReportEmitNormalizer.CheckCharsetMeta now refuses any document missing it (added the
    /// same day), every single sanitize pass was failing the SECOND Normalize call - exhausting
    /// every retry and falling back to raw, never-sanitized, never-font-injected output. Same fix
    /// shape as ReattachDoctypeIfMissing.
    /// </summary>
    [Fact]
    public void ReattachCharsetIfMissing_AddsCharsetMeta_WhenDomPurifyDroppedIt()
    {
        const string sanitized = "<!DOCTYPE html>\n<html lang=\"en\"><head><title>t</title></head><body>content</body></html>";

        var result = DomPurifySanitizer.ReattachCharsetIfMissing(sanitized);

        Assert.Contains("<meta charset=\"utf-8\">", result, StringComparison.Ordinal);
        // Must be the FIRST thing inside <head>, per the prompt's own Output Constraints rule -
        // ahead of whatever else DOMPurify's serialization left in there (e.g. <title>).
        Assert.Contains("<head><meta charset=\"utf-8\">", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ReattachCharsetIfMissing_LeavesInputUnchanged_WhenCharsetAlreadyPresent()
    {
        const string sanitized = "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>t</title></head><body>content</body></html>";

        var result = DomPurifySanitizer.ReattachCharsetIfMissing(sanitized);

        Assert.Equal(sanitized, result);
    }
}
