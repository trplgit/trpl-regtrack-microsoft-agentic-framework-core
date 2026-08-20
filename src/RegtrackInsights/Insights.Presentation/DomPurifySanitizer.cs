using Microsoft.Playwright;

namespace Insights.Presentation;

public interface IDomPurifySanitizer
{
    /// <summary>
    /// Runs the vendored real DOMPurify library (vendor/dompurify-3.4.14.min.js) against
    /// normalizer-approved HTML, inside an actual headless Chromium page - not a .NET
    /// reimplementation, and not the vendored file's default config loosened. This is a second,
    /// independent net behind ReportEmitNormalizer: the normalizer's regex checks catch the
    /// violation classes the prompt names by pattern; DOMPurify catches genuine malformed/
    /// obfuscated markup a regex can miss, using an actual DOM parser's own tokenizer.
    /// </summary>
    Task<string> SanitizeAsync(string html, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDomPurifySanitizer"/>
public sealed class DomPurifySanitizer(IBrowser browser) : IDomPurifySanitizer
{
    private static readonly string VendoredScriptPath =
        Path.Combine(AppContext.BaseDirectory, "vendor", "dompurify-3.4.14.min.js");

    public async Task<string> SanitizeAsync(string html, CancellationToken cancellationToken = default)
    {
        var page = await browser.NewPageAsync();
        try
        {
            // Blank page, not the report itself - DOMPurify runs on a throwaway document and
            // sanitizes the STRING we hand it, so the (untrusted) report content is never
            // rendered/executed by this page at all.
            await page.SetContentAsync("<!DOCTYPE html><html><head></head><body></body></html>");
            await page.AddScriptTagAsync(new PageAddScriptTagOptions { Path = VendoredScriptPath });

            // WHOLE_DOCUMENT: true - our input is a full <!DOCTYPE html>...</html> document, not
            // a body fragment, so DOMPurify must preserve the <html>/<head>/<body> structure
            // rather than sanitizing as if it were a snippet. Default profile otherwise -
            // deliberately not loosened with a custom ALLOWED_TAGS/ALLOWED_ATTR list, matching
            // the prompt's own "write as if the sandbox were not there" posture. DOMPurify's
            // default behaviour of stripping <script> entirely is the correct outcome here, not
            // a limitation: the real reports never emit one (no interactivity, per the prompt's
            // own design rules), so any that appeared would be exactly what should be removed.
            var sanitized = await page.EvaluateAsync<string>(
                "html => DOMPurify.sanitize(html, { WHOLE_DOCUMENT: true })", html);

            return ReattachDoctypeIfMissing(sanitized);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// [TRAP] Confirmed via a live run (2026-08-20): DOMPurify's WHOLE_DOCUMENT serialization
    /// drops &lt;!DOCTYPE html&gt; entirely - a known DOMPurify behaviour, not a bug in the
    /// input. Without it a real browser renders in quirks mode, and the output would fail
    /// ReportEmitNormalizer's own rule 1 if re-checked. SanitizeAsync only ever calls this on
    /// input that already passed the normalizer, so the doctype was real; this re-attaches it
    /// rather than trusting DOMPurify's serializer to have kept it.
    /// </summary>
    internal static string ReattachDoctypeIfMissing(string sanitizedHtml) =>
        sanitizedHtml.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            ? sanitizedHtml
            : "<!DOCTYPE html>\n" + sanitizedHtml;
}
