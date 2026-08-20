using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Presentation;

/// <summary>
/// Deterministic gate on the LLM-authored HTML document (prompts/05_report_html.md), enforcing
/// the 7 constraints the prompt itself says are "enforced downstream" - the document renders in
/// a sandboxed iframe with a strict CSP, and this is the check that runs BEFORE that sandbox ever
/// sees it. Regex-based, not a real HTML/CSS parser - deliberately: this is a fast, auditable
/// static check for the specific violation classes the prompt calls out, not a general-purpose
/// sanitizer. DOMPurify (build order step 13, not yet built) is the next, heavier layer; this
/// normalizer existing does not make that layer optional.
///
/// Any single violation refuses the WHOLE document - there is no partial-pass here, unlike
/// PublishGate. A document with one external script tag is not "mostly safe".
/// </summary>
public static partial class ReportEmitNormalizer
{
    public static ReportEmitResult Evaluate(string html)
    {
        var violations = new List<string>();

        CheckSingleDocument(html, violations);
        CheckNoExternalStylesheets(html, violations);
        CheckNoExternalScripts(html, violations);
        CheckNoExternalReferences(html, violations);
        CheckNoRuntimeNetworkCalls(html, violations);
        CheckNoFontFace(html, violations);

        return violations.Count == 0 ? ReportEmitResult.Approve : ReportEmitResult.Refuse(violations);
    }

    /// <summary>
    /// Rule 1 - exactly one document, not a duplicate or an escaped second copy appended after
    /// it. Also verifies the document actually STARTS with the doctype and ENDS with the closing
    /// tag - counting occurrences alone is not enough. A live run (2026-08-20) produced a
    /// perfectly correct document wrapped in a markdown code fence; the DOCTYPE/</html> counts
    /// were both exactly 1, so a count-only check passed something a real browser would render
    /// with visible garbage text around the actual page.
    /// </summary>
    private static void CheckSingleDocument(string html, List<string> violations)
    {
        var doctypeCount = DoctypeToken().Matches(html).Count;
        var closeHtmlCount = CloseHtmlToken().Matches(html).Count;

        if (doctypeCount != 1)
            violations.Add($"expected exactly one <!DOCTYPE html> declaration, found {doctypeCount}");
        if (closeHtmlCount != 1)
            violations.Add($"expected exactly one </html> closing tag, found {closeHtmlCount}");

        var trimmed = html.Trim();
        if (!DoctypeAtStartToken().IsMatch(trimmed))
            violations.Add("document must START with <!DOCTYPE html> (after trimming whitespace) - found extraneous leading content");
        if (!CloseHtmlAtEndToken().IsMatch(trimmed))
            violations.Add("document must END with </html> (after trimming whitespace) - found extraneous trailing content");
    }

    /// <summary>Rule 2 - no <link rel="stylesheet"> and no @import.</summary>
    private static void CheckNoExternalStylesheets(string html, List<string> violations)
    {
        if (StylesheetLinkToken().IsMatch(html))
            violations.Add("contains <link rel=\"stylesheet\"> - all CSS must be inline in <style> blocks");
        if (CssImportToken().IsMatch(html))
            violations.Add("contains @import - all CSS must be inline in <style> blocks");
    }

    /// <summary>Rule 3 - no <script src="...">.</summary>
    private static void CheckNoExternalScripts(string html, List<string> violations)
    {
        foreach (Match m in ScriptTagToken().Matches(html))
            if (ScriptSrcAttributeToken().IsMatch(m.Value))
                violations.Add($"contains an external <script src=...> tag - all JS must be inline: {Truncate(m.Value)}");
    }

    /// <summary>
    /// Rule 4 - every src/href/url() must be self-origin (# fragment, relative) or a data: URI.
    /// An absolute http(s):// or protocol-relative // reference is external. Also explicitly
    /// catches preconnect/prefetch <link> tags, which the prompt calls out by name.
    /// </summary>
    private static void CheckNoExternalReferences(string html, List<string> violations)
    {
        foreach (Match m in AttributeUrlToken().Matches(html))
        {
            var url = m.Groups["url"].Value;
            if (IsExternalReference(url))
                violations.Add($"external reference in {m.Groups["attr"].Value}=\"{Truncate(url)}\" - must be self-origin or a data: URI");
        }

        foreach (Match m in CssUrlToken().Matches(html))
            if (IsExternalReference(m.Groups["url"].Value))
                violations.Add($"external reference in url({Truncate(m.Groups["url"].Value)}) - must be self-origin or a data: URI");

        if (PreconnectOrPrefetchToken().IsMatch(html))
            violations.Add("contains a preconnect/prefetch <link> - zero external references allowed, including hints");
    }

    private static bool IsExternalReference(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>Rule 5 - no fetch/XHR/WebSocket, no form posts. CSP sets connect-src 'none' downstream; this is the pre-check.</summary>
    private static void CheckNoRuntimeNetworkCalls(string html, List<string> violations)
    {
        if (FetchCallToken().IsMatch(html)) violations.Add("contains fetch( - no network calls are permitted at runtime");
        if (XhrToken().IsMatch(html)) violations.Add("contains XMLHttpRequest - no network calls are permitted at runtime");
        if (WebSocketToken().IsMatch(html)) violations.Add("contains WebSocket - no network calls are permitted at runtime");

        foreach (Match m in FormTagToken().Matches(html))
        {
            var action = FormActionAttributeToken().Match(m.Value);
            if (action.Success && action.Groups["url"].Value.Trim() is { Length: > 0 } url && url != "#")
                violations.Add($"contains a <form> with a real action=\"{Truncate(url)}\" - form posts are not permitted");
        }
    }

    /// <summary>Rule 7 - system font stack only, no @font-face.</summary>
    private static void CheckNoFontFace(string html, List<string> violations)
    {
        if (FontFaceToken().IsMatch(html))
            violations.Add("contains @font-face - system font stack only, per the prompt contract");
    }

    private static string Truncate(string value) => value.Length <= 80 ? value : value[..80] + "...";

    [GeneratedRegex(@"<!DOCTYPE\s+html", RegexOptions.IgnoreCase)]
    private static partial Regex DoctypeToken();

    [GeneratedRegex(@"</html\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CloseHtmlToken();

    [GeneratedRegex(@"\A<!DOCTYPE\s+html", RegexOptions.IgnoreCase)]
    private static partial Regex DoctypeAtStartToken();

    [GeneratedRegex(@"</html\s*>\z", RegexOptions.IgnoreCase)]
    private static partial Regex CloseHtmlAtEndToken();

    [GeneratedRegex(@"<link\b[^>]*\brel\s*=\s*[""']?\s*stylesheet", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetLinkToken();

    [GeneratedRegex(@"@import\b", RegexOptions.IgnoreCase)]
    private static partial Regex CssImportToken();

    [GeneratedRegex(@"<link\b[^>]*\brel\s*=\s*[""']?\s*(preconnect|prefetch|dns-prefetch)", RegexOptions.IgnoreCase)]
    private static partial Regex PreconnectOrPrefetchToken();

    [GeneratedRegex(@"<script\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagToken();

    [GeneratedRegex(@"\bsrc\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptSrcAttributeToken();

    [GeneratedRegex(@"(?<attr>src|href)\s*=\s*[""'](?<url>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex AttributeUrlToken();

    [GeneratedRegex(@"url\(\s*[""']?(?<url>[^)""']*)[""']?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrlToken();

    [GeneratedRegex(@"\bfetch\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex FetchCallToken();

    [GeneratedRegex(@"\bXMLHttpRequest\b", RegexOptions.IgnoreCase)]
    private static partial Regex XhrToken();

    [GeneratedRegex(@"\bnew\s+WebSocket\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex WebSocketToken();

    [GeneratedRegex(@"<form\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FormTagToken();

    [GeneratedRegex(@"\baction\s*=\s*[""'](?<url>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex FormActionAttributeToken();

    [GeneratedRegex(@"@font-face\b", RegexOptions.IgnoreCase)]
    private static partial Regex FontFaceToken();
}
