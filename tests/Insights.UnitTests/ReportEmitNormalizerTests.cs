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

    [Fact]
    public void Evaluate_Refuses_FontFaceDeclaration()
    {
        var html = ValidDocument.Replace(
            "<style>", "<style>@font-face { font-family: 'Custom'; src: url('data:font/woff2;base64,AAA'); }");

        var result = ReportEmitNormalizer.Evaluate(html);

        Assert.False(result.Approved);
        Assert.Contains(result.Violations, v => v.Contains("font-face", StringComparison.Ordinal));
    }
}
