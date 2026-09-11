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
///      figure per paragraph that matters (06_freetier_digest.md "Emphasis").
/// </summary>
public sealed class FreeDigestEmailRendererTests
{
    private static FreeDigestEmailRenderer Renderer() => new("templates");

    [Fact]
    public async Task RenderHtmlAsync_ConvertsBoldMarkdownToStrongTags()
    {
        var html = await Renderer().RenderHtmlAsync(
            "**66 obligations** are due this week.", "ABC Training",
            new DateTime(2026, 8, 23), "https://example.com/upgrade", "https://example.com/unsubscribe");

        Assert.Contains("<strong>66 obligations</strong>", html);
        Assert.DoesNotContain("**", html);
    }

    [Fact]
    public async Task RenderHtmlAsync_EncodesHtmlSpecialCharactersInTheBody()
    {
        var html = await Renderer().RenderHtmlAsync(
            "Section 4 & 5 apply where risk < threshold.", "ABC Training",
            new DateTime(2026, 8, 23), "https://example.com/upgrade", "https://example.com/unsubscribe");

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
            new DateTime(2026, 8, 23), "https://example.com/upgrade", "https://example.com/unsubscribe");

        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
    }

    [Fact]
    public async Task RenderHtmlForArtifactAsync_StillFindsExactlyOneUnsubscribeSentinel_WithBoldMarkdownInTheBody()
    {
        var html = await Renderer().RenderHtmlForArtifactAsync(
            "**214 obligations** were completed last week.", "ABC Training",
            new DateTime(2026, 8, 23), "https://example.com/upgrade");

        var withRealUrl = FreeDigestEmailRenderer.SubstituteUnsubscribeUrl(html, "https://example.com/unsubscribe?token=abc");

        Assert.Contains("<strong>214 obligations</strong>", withRealUrl);
        Assert.Contains("https://example.com/unsubscribe?token=abc", withRealUrl);
    }
}
