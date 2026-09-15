using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Insights.Presentation;

public interface IDomPurifySanitizer
{
    /// <summary>
    /// Runs the vendored real DOMPurify library (vendor/dompurify-3.4.14.min.js) against
    /// normalizer-approved HTML, inside an actual headless Chromium page - not a .NET
    /// reimplementation. Default profile plus one documented, narrow exception
    /// (ADD_TAGS: ['script'] - see SanitizeAsync's own [BUG FOUND LIVE] note), not a generally
    /// loosened ALLOWED_TAGS/ALLOWED_ATTR list. This is a second, independent net behind
    /// ReportEmitNormalizer: the normalizer's regex checks catch the violation classes the prompt
    /// names by pattern; DOMPurify catches genuine malformed/obfuscated markup a regex can miss,
    /// using an actual DOM parser's own tokenizer.
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
            // rather than sanitizing as if it were a snippet.
            //
            // [BUG FOUND LIVE, 2026-09-02] ADD_TAGS: ['script'] - DOMPurify's own documented
            // escape hatch for a tag it forbids by default. Coverage-tile interactivity (chip
            // filter + tile-select, added to the prompt earlier the same session) depends on a
            // real inline <script>; DOMPurify's default profile was silently deleting it
            // entirely, on every render, confirmed live (a fully inert script with no src and no
            // network call still vanished). Safe to allow: this codebase's own rule 3 already
            // permits inline script ("no script SRC", not "no script"), ReportEmitNormalizer's
            // CheckNoExternalScripts/CheckNoRuntimeNetworkCalls already refuse any src= or
            // fetch/XHR/WebSocket/form-post BEFORE this method ever runs (Normalize always
            // precedes Sanitize in the real pipeline), and the real production CSP already
            // expects inline scripts to execute (design doc Sec.8 - sandbox="allow-scripts",
            // script-src 'self', an already-approved sample with 7 real inline <script> blocks).
            // connect-src 'none' remains the actual security wall, unchanged by this. Everything
            // else stays the untouched default profile.
            //
            // [CONFIRMED] ADD_TAGS re-permits the TAG only - DOMPurify does NOT also strip src=
            // off an allowed <script> independently (confirmed live, see
            // SanitizeAsync_DoesNotIndependentlyStripSrcFromAnAllowedScriptTag). DOMPurify is
            // therefore no longer a redundant second check against a LITERAL external script src
            // specifically - only against genuinely malformed/obfuscated variants a regex could
            // miss, its actually-documented job (IDomPurifySanitizer's own doc comment). The
            // literal case stays fully blocked because CheckNoExternalScripts runs first.
            var sanitized = await page.EvaluateAsync<string>(
                "html => DOMPurify.sanitize(html, { WHOLE_DOCUMENT: true, ADD_TAGS: ['script'] })", html);

            return ReattachCharsetIfMissing(ReattachDoctypeIfMissing(sanitized));
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

    private static readonly Regex HeadOpenTag = new(@"<head[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CharsetMetaToken = new(@"<meta\s+charset\s*=\s*[""']?utf-8[""']?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] Same DOMPurify WHOLE_DOCUMENT serialization behaviour as
    /// ReattachDoctypeIfMissing above, confirmed via a real chain run: a document that went into
    /// SanitizeAsync with a real "meta charset=utf-8" (written by the render agent itself, per
    /// the prompt's Output Constraints rule) came out without it. ReportEmitNormalizer.
    /// CheckCharsetMeta (added the same day) then refused every single sanitize pass on the
    /// SECOND Normalize call - exhausting every retry and falling back to raw, never-sanitized
    /// output. Re-inserted as the first thing inside &lt;head&gt;, same insertion point
    /// PoppinsFontInjector.Inject already uses for the same reason.
    /// </summary>
    internal static string ReattachCharsetIfMissing(string sanitizedHtml)
    {
        if (CharsetMetaToken.IsMatch(sanitizedHtml))
            return sanitizedHtml;

        var match = HeadOpenTag.Match(sanitizedHtml);
        if (!match.Success)
            return sanitizedHtml; // nothing to anchor on - CheckSingleDocument/CheckCharsetMeta will refuse downstream and say why.

        return sanitizedHtml.Insert(match.Index + match.Length, "<meta charset=\"utf-8\">");
    }
}
