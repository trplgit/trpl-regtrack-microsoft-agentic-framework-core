using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// One approving baseline document plus one failing case per rule - pure logic, no DB, no LLM.
/// Deliberately does not try to be a full HTML/CSS parser (see ReportEmitNormalizer's own doc
/// comment); these tests exercise exactly the violation classes the prompt names.
/// </summary>
public sealed class ReportEmitNormalizerTests
{
    private const string ValidDocument = """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8">
        <style>body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }</style>
        </head>
        <body>
        <h1>Compliance Health Report</h1>
        <svg role="img"><title>Overdue by location</title><rect width="10" height="10"/></svg>
        <script>console.log('static, no network calls');</script>
        </body></html>
        """;

    [Fact]
    public void Evaluate_Approves_AWellFormedSelfContainedDocument()
    {
        var result = ReportEmitNormalizer.Evaluate(ValidDocument);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
        Assert.Empty(result.Violations);
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] The prompt has said "the very first element inside head must
    /// be &lt;meta charset="utf-8"&gt;" since this same day - a real render still omitted it
    /// entirely (confirmed: grep for "charset" over the saved output found zero matches). Prose
    /// alone did not work, so it is enforced here too - the file's real UTF-8 bytes (confirmed:
    /// C2 B7 for ·, E2 80 94 for —, not double-encoded) mojibake into "Â·"/"â€"" the moment
    /// anything serves the document without an explicit charset header to fall back on.
    /// </summary>
    [Fact]
    public void Evaluate_Refuses_MissingCharsetMeta()
    {
        var html = ValidDocument.Replace("<meta charset=\"utf-8\">", "");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("charset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_Approves_CharsetMetaWithSingleQuotes()
    {
        var html = ValidDocument.Replace("<meta charset=\"utf-8\">", "<meta charset='utf-8'>");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    /// <summary>
    /// Confirmed via a live run (2026-08-20): GPT-5.2 wrapped an otherwise-correct document in a
    /// markdown code fence. DOCTYPE/</html> both existed exactly once, so a count-only check
    /// would have wrongly approved this - the tightened rule also verifies the document actually
    /// starts/ends correctly.
    /// </summary>
    [Fact]
    public void Evaluate_Refuses_DocumentWrappedInMarkdownCodeFence()
    {
        var fenced = "```html\n" + ValidDocument + "\n```";

        var result = ReportEmitNormalizer.Evaluate(fenced);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("START", StringComparison.Ordinal));
        Assert.Contains(result.Violations, v => v.Contains("END", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_Refuses_TwoDoctypesConcatenated()
    {
        var result = ReportEmitNormalizer.Evaluate(ValidDocument + ValidDocument);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("DOCTYPE", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_Refuses_ExternalStylesheetLink()
    {
        var html = ValidDocument.Replace(
            "<style>", "<link rel=\"stylesheet\" href=\"https://fonts.googleapis.com/css\"><style>");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("stylesheet", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_Refuses_ExternalScriptSrc()
    {
        var html = ValidDocument.Replace(
            "<script>", "<script src=\"https://cdn.example.com/chart.js\"></script><script>");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("external <script", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<img src=\"https://example.com/logo.png\">")]
    [InlineData("<img src=\"//example.com/logo.png\">")]
    public void Evaluate_Refuses_ExternalImageReference(string externalImgTag)
    {
        var html = ValidDocument.Replace("<h1>", externalImgTag + "<h1>");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("external reference", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_Approves_DataUriImage()
    {
        var html = ValidDocument.Replace("<h1>", "<img src=\"data:image/png;base64,iVBORw0KGgo=\"><h1>");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    [Fact]
    public void Evaluate_Refuses_PreconnectHint()
    {
        var html = ValidDocument.Replace(
            "<style>", "<link rel=\"preconnect\" href=\"https://fonts.gstatic.com\"><style>");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("preconnect", StringComparison.Ordinal));
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] The Coverage-tile reference script builds a pill indicator via
    /// pill.innerHTML = '&lt;span class="di-covdetail__dot"&gt;&lt;/span&gt;' - confirmed live
    /// (real vendored DOMPurify, real headless Chromium) that DOMPurify silently deletes the
    /// ENTIRE &lt;script&gt; tag whenever its text content contains anything shaped like a
    /// complete HTML tag, regardless of quoting - even a bare '&lt;span&gt;&lt;/span&gt;' with no
    /// attributes at all. Confirmed NOT triggered by quote style, string concatenation, or a lone
    /// '&lt;' character - only a genuine tag-shaped substring. DOMPurify's own defensive posture
    /// for a tag it does not normally allow (ADD_TAGS: ['script'] - see DomPurifySanitizer's own
    /// [BUG FOUND LIVE] note), not a bug to fight - the fix is to never write this pattern.
    /// Catching it here, before Sanitize ever runs, turns a silent multi-iteration failure (script
    /// vanishes, structure gate refuses with no explanation of why) into one specific, actionable
    /// refusal reason the render agent can actually act on.
    /// </summary>
    [Fact]
    public void Evaluate_Refuses_ScriptContentContainingAnHtmlTagShapedSubstring()
    {
        var html = ValidDocument.Replace(
            "console.log('static, no network calls');",
            "document.body.innerHTML = '<span class=\"x\"></span>';");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("<script", StringComparison.Ordinal) && v.Contains("tag", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>DOMPurify's own posture, confirmed live: a bare '&lt;' with no matching tag shape does not trigger it - never refuse ordinary comparisons like a &lt; b.</summary>
    [Fact]
    public void Evaluate_Approves_ScriptContentWithALoneLessThanCharacter()
    {
        var html = ValidDocument.Replace(
            "console.log('static, no network calls');",
            "if (a < b) { console.log('ok'); }");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    /// <summary>The confirmed-safe alternative (createElement/className/appendChild, no HTML-tag-shaped string literal) must still pass.</summary>
    [Fact]
    public void Evaluate_Approves_ScriptBuildingElementsViaCreateElementInsteadOfInnerHtml()
    {
        var html = ValidDocument.Replace(
            "console.log('static, no network calls');",
            "var s = document.createElement('span'); s.className = 'x'; document.body.appendChild(s);");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    [Fact]
    public void Evaluate_Refuses_FetchCall()
    {
        var html = ValidDocument.Replace("console.log", "fetch('/api/data'); console.log");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("fetch(", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_Refuses_FormWithRealAction()
    {
        var html = ValidDocument.Replace("<h1>", "<form action=\"https://example.com/submit\" method=\"post\"></form><h1>");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("form", StringComparison.Ordinal));
    }

    /// <summary>
    /// [v2 handoff, 2026-08-26] Self-hosted @font-face (brand contract requires self-hosted
    /// Poppins) is now permitted - Rule 4 (CheckNoExternalReferences) already governs whether the
    /// font's own src: url() is self-hosted or not, so a data: URI here passes cleanly.
    /// </summary>
    [Fact]
    public void Evaluate_Approves_SelfHostedFontFaceDeclaration()
    {
        var html = ValidDocument.Replace(
            "<style>", "<style>@font-face { font-family: 'Poppins'; src: url('data:font/woff2;base64,AAA') format('woff2'); }");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.True(result.Approved, string.Join("; ", result.Violations));
    }

    /// <summary>An @font-face pointed at an external CDN still fails - via Rule 4, not a dedicated font rule.</summary>
    [Fact]
    public void Evaluate_Refuses_ExternallyHostedFontFaceDeclaration()
    {
        var html = ValidDocument.Replace(
            "<style>", "<style>@font-face { font-family: 'Poppins'; src: url('https://fonts.gstatic.com/poppins.woff2') format('woff2'); }");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("external reference", StringComparison.Ordinal));
    }
}
