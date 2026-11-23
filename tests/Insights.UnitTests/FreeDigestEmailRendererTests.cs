using Insights.Presentation;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// The body reaching RenderHtmlAsync is LLM output (or the deterministic fallback) inserted
/// straight into an HTML email. Two properties matter and neither was covered before:
///
///   1. It must be HTML-encoded - the body was previously inserted raw, so a literal "&lt;" or
///      "&amp;" in a composed sentence (or a prompt-injected tag) would have rendered as live
///      markup in a customer's inbox.
///   2. **bold** markdown in the body becomes &lt;strong&gt;, so the prompt can emphasise the one
///      figure per paragraph that matters (shared rules, "Form").
/// </summary>
public sealed class FreeDigestEmailRendererTests
{
    private static FreeDigestEmailRenderer Renderer() => new("templates");

    [Fact]
    public async Task RenderHtmlAsync_ConvertsBoldMarkdownToStrongTags()
    {
        var html = await Renderer().RenderHtmlAsync(
            "**66 obligations** are due this week.", "ABC Training",
            Insights.Domain.MonthlyDigestCalendar.For(new DateOnly(2026, 8, 23)), "https://example.com/upgrade", "https://example.com/unsubscribe");

        Assert.Contains("<strong>66 obligations</strong>", html);
        // Not a blanket "no ** anywhere in the page" - the shell's own doc comment legitimately
        // quotes "**bold**" when describing FormatBody. Only the BODY's own markdown must be gone.
        Assert.DoesNotContain("**66 obligations**", html);
    }

    [Fact]
    public async Task RenderHtmlAsync_EncodesHtmlSpecialCharactersInTheBody()
    {
        var html = await Renderer().RenderHtmlAsync(
            "Section 4 & 5 apply where risk < threshold.", "ABC Training",
            Insights.Domain.MonthlyDigestCalendar.For(new DateOnly(2026, 8, 23)), "https://example.com/upgrade", "https://example.com/unsubscribe");

        Assert.Contains("Section 4 &amp; 5 apply where risk &lt; threshold.", html);
    }

    /// <summary>
    /// A malicious or malformed body must not be able to inject markup - this is the regression
    /// the encoding step exists to prevent.
    /// </summary>
    [Fact]
    public async Task RenderHtmlAsync_DoesNotLetTheBodyInjectMarkup()
    {
        var html = await Renderer().RenderHtmlAsync(
            "<img src=x onerror=alert(1)>", "ABC Training",
            Insights.Domain.MonthlyDigestCalendar.For(new DateOnly(2026, 8, 23)), "https://example.com/upgrade", "https://example.com/unsubscribe");

        // Not a blanket "no <img anywhere in the page" - the shell itself has real logo/icon <img>
        // tags. The property under test is that the BODY's own malicious tag never lands unescaped.
        Assert.DoesNotContain("<img src=x onerror=alert(1)>", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
    }

    /// <summary>
    /// [FOUND LIVE 2026-11-02] Paragraphs used to depend on the body cell's CSS
    /// "white-space:pre-line". Gmail and Outlook drop it, so real recipients (tenant 1300's
    /// 2 Nov email) got "Good morning, Here is your..." as one run-on paragraph. Each paragraph
    /// must now be a real &lt;p&gt;, and nothing may depend on pre-line any more.
    /// </summary>
    [Fact]
    public async Task RenderHtmlAsync_TurnsBlankLineParagraphsIntoRealParagraphTags()
    {
        var html = await Renderer().RenderHtmlAsync(
            "Good morning,\r\n\r\nIn October, **2,597** compliances fell due.\n\n\n\nThis briefing names only what stands out.\nRegInsights Ultimate carries the full picture.",
            "ABC Training",
            Insights.Domain.MonthlyDigestCalendar.For(new DateOnly(2026, 8, 23)), "https://example.com/upgrade", "https://example.com/unsubscribe");

        Assert.Contains("<p style=\"margin:0 0 16px 0;\">Good morning,</p>", html);
        Assert.Contains("<p style=\"margin:0 0 16px 0;\">In October, <strong>2,597</strong> compliances fell due.</p>", html);
        Assert.Contains("<p style=\"margin:0 0 16px 0;\">This briefing names only what stands out.<br>RegInsights Ultimate carries the full picture.</p>", html);
        var slot = html[html.IndexOf("<!-- ============ BODY SLOT ============ -->", StringComparison.Ordinal)..html.IndexOf("<!-- ============ /BODY SLOT ============ -->", StringComparison.Ordinal)];
        Assert.Equal(3, slot.Split("<p ").Length - 1);                                  // extra blank lines never make an empty <p>
        Assert.DoesNotContain("white-space:pre-line;\">", html);                        // the body cell no longer relies on it
    }

    [Fact]
    public async Task RenderHtmlForArtifactAsync_StillFindsExactlyOneUnsubscribeSentinel_WithBoldMarkdownInTheBody()
    {
        var html = await Renderer().RenderHtmlForArtifactAsync(
            "**214 obligations** were completed last week.", "ABC Training",
            Insights.Domain.MonthlyDigestCalendar.For(new DateOnly(2026, 8, 23)), "https://example.com/upgrade");

        var withRealUrl = FreeDigestEmailRenderer.SubstituteUnsubscribeUrl(html, "https://example.com/unsubscribe?token=abc");

        Assert.Contains("<strong>214 obligations</strong>", withRealUrl);
        Assert.Contains("https://example.com/unsubscribe?token=abc", withRealUrl);
    }
}
