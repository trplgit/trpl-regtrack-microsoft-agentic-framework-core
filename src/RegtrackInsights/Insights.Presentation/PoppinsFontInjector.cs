using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// Deterministically embeds the REAL vendored Poppins glyph data (vendor/README.md "fonts/"
/// entry) into generated report HTML as a self-hosted @font-face block, so the render agent
/// never has to author font binary itself.
///
/// [TRAP - found and fixed] a render agent asked to declare its own @font-face with an
/// embedded font produces a syntactically valid but functionally EMPTY face: decoding one such
/// attempt found only 6 real bytes (the WOFF2 magic number, "wOF2") followed by fabricated
/// filler - no LLM can emit real compressed glyph data. The browser silently falls back to the
/// system stack, so the report *looks* like it tried Poppins and failed, worse than never
/// declaring it. This class is the fix: the vendored files are the real thing, fetched
/// once from Google Fonts (OFL-licensed, free to self-host) and checked in - see
/// vendor/README.md for provenance (400/600 from 2026-08-27, 500 added 2026-09-09 once a
/// design system that actually uses it - Sambram's AI-INSIGHTS-BRAND-HANDOFF.md - arrived).
/// The render agent's job (per the prompt) is only ever to WRITE THE NAME "Poppins" in
/// font-family/font-family="" - this class supplies the bytes that make that name resolve to
/// something real, post-generation.
/// </summary>
public static class PoppinsFontInjector
{
    private static readonly string Poppins400Path =
        Path.Combine(AppContext.BaseDirectory, "vendor", "fonts", "poppins-400.woff2");
    private static readonly string Poppins500Path =
        Path.Combine(AppContext.BaseDirectory, "vendor", "fonts", "poppins-500.woff2");
    private static readonly string Poppins600Path =
        Path.Combine(AppContext.BaseDirectory, "vendor", "fonts", "poppins-600.woff2");

    // Loaded and base64-encoded once per process, not per call - these files never change at
    // runtime (checked-in, CopyToOutputDirectory PreserveNewest), so re-encoding them on every
    // report would just be wasted work on the hot path.
    private static readonly Lazy<string> Poppins400Base64 = new(() => Convert.ToBase64String(File.ReadAllBytes(Poppins400Path)));
    private static readonly Lazy<string> Poppins500Base64 = new(() => Convert.ToBase64String(File.ReadAllBytes(Poppins500Path)));
    private static readonly Lazy<string> Poppins600Base64 = new(() => Convert.ToBase64String(File.ReadAllBytes(Poppins600Path)));

    private static readonly Regex HeadOpenTag = new(@"<head[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Inserts a self-hosted @font-face &lt;style&gt; block immediately after the document's
    /// opening &lt;head&gt; tag - earliest possible position, so nothing else in &lt;head&gt;
    /// can reference the family before it exists. Pure string transform: no network, no clock,
    /// no randomness - the two file reads are of static, checked-in, never-changing assets, the
    /// same treatment PartialDimensionPlaceholder's transform already gets in the orchestrator
    /// (see InsightsReportOrchestrator's doc comment on that call).
    ///
    /// Fails LOUDLY (CLAUDE.md non-negotiable 2) if &lt;head&gt; is missing - never silently
    /// skips the injection and lets a report ship still claiming "Poppins" with nothing backing
    /// it.
    /// </summary>
    /// <summary>
    /// Same check <see cref="Inject"/> itself uses to decide whether it can proceed - exposed so a
    /// caller (MafReportHtmlAgent.RenderAsync) can fail fast on a malformed/truncated render
    /// BEFORE it reaches this class, inside the same activity ScheduleWithRetry wraps
    /// (InsightsReportOrchestrator), rather than one activity later where a retry can no longer
    /// reach it. One regex, never two copies to drift out of sync.
    /// </summary>
    public static bool HasHeadTag(string html) => HeadOpenTag.IsMatch(html);

    public static string Inject(string html)
    {
        var match = HeadOpenTag.Match(html);
        if (!match.Success)
            throw new InvalidOperationException("PoppinsFontInjector: no <head> tag found in generated HTML - cannot inject the self-hosted font face.");

        var fontFace =
            "<style>" +
            "@font-face{font-family:'Poppins';font-style:normal;font-weight:400;font-display:swap;" +
            $"src:url(data:font/woff2;base64,{Poppins400Base64.Value}) format('woff2');}}" +
            "@font-face{font-family:'Poppins';font-style:normal;font-weight:500;font-display:swap;" +
            $"src:url(data:font/woff2;base64,{Poppins500Base64.Value}) format('woff2');}}" +
            "@font-face{font-family:'Poppins';font-style:normal;font-weight:600;font-display:swap;" +
            $"src:url(data:font/woff2;base64,{Poppins600Base64.Value}) format('woff2');}}" +
            "</style>";

        return html.Insert(match.Index + match.Length, fontFace);
    }
}
