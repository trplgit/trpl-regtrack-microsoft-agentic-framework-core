using Insights.Agents;

namespace Insights.UnitTests;

/// <summary>
/// StripMarkdownFence in isolation - confirmed necessary via a live run (2026-08-20) where
/// GPT-5.2 wrapped a fully correct document in a ```html fence unprompted.
/// </summary>
public sealed class ReportHtmlAgentTests
{
    [Fact]
    public void StripMarkdownFence_RemovesHtmlFencedWrapper()
    {
        const string document = "<!DOCTYPE html><html><body>content</body></html>";
        var fenced = "```html\n" + document + "\n```";

        var result = MafReportHtmlAgent.StripMarkdownFence(fenced);

        Assert.Equal(document, result);
    }

    [Fact]
    public void StripMarkdownFence_RemovesBareFencedWrapper_WithNoLanguageTag()
    {
        const string document = "<!DOCTYPE html><html><body>content</body></html>";
        var fenced = "```\n" + document + "\n```";

        var result = MafReportHtmlAgent.StripMarkdownFence(fenced);

        Assert.Equal(document, result);
    }

    [Fact]
    public void StripMarkdownFence_LeavesAnUnfencedDocumentUnchanged()
    {
        const string document = "<!DOCTYPE html><html><body>content</body></html>";

        var result = MafReportHtmlAgent.StripMarkdownFence(document);

        Assert.Equal(document, result);
    }

    /// <summary>
    /// A stray ``` appearing INSIDE the document body (e.g. quoted in prose) must not trigger
    /// stripping - the pattern only matches when the ENTIRE trimmed input is one fenced block.
    /// </summary>
    [Fact]
    public void StripMarkdownFence_DoesNotStripPartialOrInternalFencing()
    {
        const string document = "<!DOCTYPE html><html><body>see the `code` example: ```not a real fence```</body></html>";

        var result = MafReportHtmlAgent.StripMarkdownFence(document);

        Assert.Equal(document, result);
    }
}
