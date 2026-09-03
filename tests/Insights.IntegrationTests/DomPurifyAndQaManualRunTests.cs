using Insights.Presentation;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// NOT part of the automated suite in spirit - launches a real headless Chromium via Playwright.
/// Requires the browser binary installed first (dotnet build then run the generated
/// playwright.ps1/.sh install chromium, or PLAYWRIGHT_BROWSERS_PATH pointed at an existing
/// install). Reuses the HTML already saved by ReportHtmlAgentManualRunTests rather than spending
/// more LLM tokens regenerating it. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~DomPurifyAndQaManualRunTests
/// Requires: REPORT_HTML_INPUT_PATH (the file ReportHtmlAgentManualRunTests wrote)
/// Optional: REPORT_QA_SCREENSHOT_PATH (defaults to a scratch file next to the input)
/// </summary>
public sealed class DomPurifyAndQaManualRunTests(ITestOutputHelper output)
{
    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] Every real render showed Poppins declared in font-family but
    /// the glyphs never actually loaded, AND (separately, found while hunting the first issue)
    /// every render's second Normalize call was refusing on a missing charset meta tag it had
    /// gone in WITH. Root cause for both, confirmed via a real chain run against a real rendered
    /// report: DOMPurify's WHOLE_DOCUMENT serialization drops "meta charset=utf-8" the same way
    /// it already drops "!DOCTYPE html" (see DomPurifySanitizer.ReattachDoctypeIfMissing's own
    /// doc comment) - ReportEmitNormalizer.CheckCharsetMeta (added the same day) then refused
    /// EVERY sanitize pass, exhausting every retry and falling back to raw, never-sanitized,
    /// never-font-injected output - which is why the font looked missing even though Inject
    /// itself works fine (proven separately by this same test, pre-fix). Fixed via
    /// DomPurifySanitizer.ReattachCharsetIfMissing, same shape as the doctype fix.
    /// </summary>
    [Fact]
    public async Task SanitizeAsync_PreservesTheInjectedPoppinsFontFaceBlockAndTheCharsetMeta()
    {
        const string minimalDocument =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><h1>Report</h1></body></html>";
        var withFont = PoppinsFontInjector.Inject(minimalDocument);
        Assert.Contains("@font-face", withFont, StringComparison.Ordinal); // sanity - Inject itself worked
        output.WriteLine($"Before DOMPurify: {withFont.Length} chars, contains @font-face: {withFont.Contains("@font-face")}");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var sanitizer = new DomPurifySanitizer(browser);

        var sanitized = await sanitizer.SanitizeAsync(withFont);
        output.WriteLine($"After DOMPurify: {sanitized.Length} chars, contains @font-face: {sanitized.Contains("@font-face")}, contains woff2: {sanitized.Contains("woff2")}, contains charset: {sanitized.Contains("charset")}");

        Assert.Contains("@font-face", sanitized, StringComparison.Ordinal);
        Assert.Contains("woff2", sanitized, StringComparison.Ordinal);
        // Closes the loop with ReportEmitNormalizer's own rule rather than just grepping for the
        // substring - proves the SECOND Normalize call (the one that was refusing every real run)
        // now actually passes on real sanitized output, not just that some "charset" text exists.
        var postSanitizeCheck = ReportEmitNormalizer.Evaluate(sanitized);
        Assert.True(postSanitizeCheck.Approved, string.Join("; ", postSanitizeCheck.Violations));
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] Coverage tiles were clickable before, stopped working - the
    /// freshest render had zero "&lt;script" occurrences anywhere, even though the prompt
    /// (Coverage pane section) explicitly authors real inline JS for the chip-filter/tile-select
    /// interaction and this codebase's own rule 3 is "no script SRC", not "no script". Confirmed
    /// live (real vendored DOMPurify, real headless Chromium): its default profile strips
    /// &lt;script&gt; entirely, even a fully inert one with no src and no network call - proven
    /// with the exact minimal case below before touching the fix. SanitizeAsync's own doc comment
    /// used to claim "the real reports never emit one" - true when written, false once the
    /// coverage-tile interactivity was added to the prompt later the same session. Confirmed safe
    /// to allow: the real production CSP already expects inline scripts to run (design doc Sec.8 -
    /// sandbox="allow-scripts", script-src 'self', an already-approved 472KB sample with 7 real
    /// inline &lt;script&gt; blocks) - connect-src 'none' is the actual security wall, unchanged
    /// and enforced separately (ReportEmitNormalizer.CheckNoRuntimeNetworkCalls) before DOMPurify
    /// ever runs. Fixed via ADD_TAGS: ['script'] - DOMPurify's own documented escape hatch for
    /// tags it forbids by default.
    /// </summary>
    [Fact]
    public async Task SanitizeAsync_PreservesARealInlineScriptTagWithNoSrcAndNoNetworkCall()
    {
        const string withScript =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><h1>Report</h1>" +
            "<script>document.body.classList.add('js-ran');</script></body></html>";

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var sanitizer = new DomPurifySanitizer(browser);

        var sanitized = await sanitizer.SanitizeAsync(withScript);
        output.WriteLine($"After DOMPurify: {sanitized}");

        Assert.Contains("<script>", sanitized, StringComparison.Ordinal);
        Assert.Contains("js-ran", sanitized, StringComparison.Ordinal);
        // The rest of the safety net still applies to whatever IS allowed through - proves this
        // fix does not silently also let an external script slip past ReportEmitNormalizer's own
        // pre-check (that check runs before Sanitize in the real pipeline, this just confirms
        // DOMPurify itself does not undo it).
        var postCheck = ReportEmitNormalizer.Evaluate(sanitized);
        Assert.True(postCheck.Approved, string.Join("; ", postCheck.Violations));
    }

    /// <summary>
    /// [CORRECTED, 2026-09-02] Written expecting DOMPurify's own layer to independently strip
    /// src= off an allowed &lt;script&gt; tag - it does NOT, confirmed live: the external URL
    /// survives DOMPurify unchanged once ADD_TAGS permits the tag itself. This is not a real
    /// production gap - Normalize runs BEFORE Sanitize in the actual pipeline
    /// (RenderAndReviewAsync/InsightsReportOrchestrator both order it that way) and
    /// ReportEmitNormalizer.CheckNoExternalScripts already refuses a literal "script src=" before
    /// SanitizeAsync ever sees it. But it does mean DOMPurify is no longer an independent second
    /// check for THIS SPECIFIC, already-regex-catchable pattern - only for genuinely malformed/
    /// obfuscated variants a regex could miss (its actually-documented job, per
    /// IDomPurifySanitizer's own doc comment). Recorded here so that stays an honest, tested fact
    /// rather than an assumption.
    /// </summary>
    [Fact]
    public async Task SanitizeAsync_DoesNotIndependentlyStripSrcFromAnAllowedScriptTag()
    {
        const string withExternalScript =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" +
            "<script src=\"https://evil.example.com/x.js\"></script></body></html>";

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var sanitizer = new DomPurifySanitizer(browser);

        var sanitized = await sanitizer.SanitizeAsync(withExternalScript);
        output.WriteLine($"After DOMPurify: {sanitized}");

        // Documents reality, not a desired outcome - see the [CORRECTED] note above for why this
        // is safe (ReportEmitNormalizer already refuses this shape earlier in the real pipeline).
        Assert.Contains("evil.example.com", sanitized, StringComparison.Ordinal);
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] The Coverage-tile reference script built its pill indicator
    /// via pill.innerHTML = '&lt;span class="di-covdetail__dot"&gt;&lt;/span&gt;' - confirmed
    /// live that DOMPurify silently deletes the ENTIRE &lt;script&gt; tag whenever its text
    /// content contains anything shaped like a complete HTML tag, regardless of quote style,
    /// string concatenation, or a lone '&lt;' character (only a genuine tag shape triggers it -
    /// see the four cases below, isolated one variable at a time against the real vendored
    /// DOMPurify). DOMPurify's own defensive posture for a tag it does not allow by default
    /// (ADD_TAGS: ['script']), not a bug to fight. ReportEmitNormalizer.
    /// CheckNoHtmlTagShapedTextInsideScripts now catches this deterministically before Sanitize
    /// ever runs (see its own [BUG FOUND LIVE] note) - this test pins DOMPurify's actual behaviour
    /// so that gate's premise stays true against a future library upgrade, and the prompt's
    /// reference script itself was rewritten to the safe createElement pattern (case 4).
    /// </summary>
    [Theory]
    [InlineData("html-tag-in-single-quoted-js-string", "document.body.setAttribute('x', '<span class=\"y\"></span>');", false)]
    [InlineData("html-tag-with-no-attributes-at-all", "document.body.setAttribute('x', '<span></span>');", false)]
    [InlineData("lone-less-than-in-a-comparison-is-fine", "if (1 < 2) { document.body.setAttribute('x', 'ok'); }", true)]
    [InlineData("createElement-based-construction-is-fine", "var s=document.createElement('span'); s.className='dot'; document.body.appendChild(s);", true)]
    public async Task SanitizeAsync_DeletesTheWholeScriptOnlyWhenItsTextContainsAnHtmlTagShape(
        string label, string candidateScript, bool expectedToSurvive)
    {
        var doc = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" +
            $"<script>{candidateScript}</script></body></html>";

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var sanitizer = new DomPurifySanitizer(browser);
        var sanitized = await sanitizer.SanitizeAsync(doc);

        var survived = sanitized.Contains("<script", StringComparison.Ordinal);
        output.WriteLine($"[{label}] survived={survived}");
        Assert.Equal(expectedToSurvive, survived);
    }

    [Fact]
    public async Task SanitizeAndQa_RunAgainstARealRenderedReport()
    {
        var inputPath = Environment.GetEnvironmentVariable("REPORT_HTML_INPUT_PATH")
            ?? throw new InvalidOperationException("Set REPORT_HTML_INPUT_PATH to the HTML file written by ReportHtmlAgentManualRunTests.");
        var html = await File.ReadAllTextAsync(inputPath);
        output.WriteLine($"Input: {inputPath} ({html.Length} chars)");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();

        var sanitizer = new DomPurifySanitizer(browser);
        var sanitized = await sanitizer.SanitizeAsync(html);

        output.WriteLine("");
        output.WriteLine($"After DOMPurify: {sanitized.Length} chars (delta: {sanitized.Length - html.Length:+#;-#;0})");

        // Closes the loop: the normalizer approved the input BEFORE DOMPurify ran; re-checking
        // the sanitized output proves DOMPurify's own serialization (e.g. dropping the doctype or
        // the charset meta tag - see DomPurifySanitizer.ReattachDoctypeIfMissing/
        // ReattachCharsetIfMissing) did not silently produce something the normalizer would now
        // reject.
        var postSanitizeCheck = ReportEmitNormalizer.Evaluate(sanitized);
        output.WriteLine($"Normalizer re-check after sanitization: {postSanitizeCheck.Approved}");
        foreach (var violation in postSanitizeCheck.Violations)
            output.WriteLine($"  VIOLATION: {violation}");
        Assert.True(postSanitizeCheck.Approved, string.Join("; ", postSanitizeCheck.Violations));
        if (sanitized.Length != html.Length)
        {
            var sanitizedPath = Environment.GetEnvironmentVariable("REPORT_HTML_SANITIZED_PATH")
                ?? Path.Combine(Path.GetDirectoryName(inputPath)!, "rendered-report-sanitized.html");
            await File.WriteAllTextAsync(sanitizedPath, sanitized);
            output.WriteLine($"Length changed - sanitized copy written for diffing: {sanitizedPath}");
        }
        else
        {
            output.WriteLine("No length change - DOMPurify found nothing to strip.");
        }

        var qaRunner = new PlaywrightReportQa(browser);
        var qaResult = await qaRunner.RunAsync(sanitized);

        output.WriteLine("");
        output.WriteLine($"QA has issues: {qaResult.HasIssues}");
        output.WriteLine($"Horizontal overflow: {qaResult.HasHorizontalOverflow}");
        output.WriteLine($"Console errors: {qaResult.ConsoleErrors.Count}");
        foreach (var error in qaResult.ConsoleErrors)
            output.WriteLine($"  {error}");

        var screenshotPath = Environment.GetEnvironmentVariable("REPORT_QA_SCREENSHOT_PATH")
            ?? Path.Combine(Path.GetDirectoryName(inputPath)!, "rendered-report-screenshot.png");
        await File.WriteAllBytesAsync(screenshotPath, qaResult.Screenshot);
        output.WriteLine($"Screenshot written to: {screenshotPath} ({qaResult.Screenshot.Length} bytes)");

        // Cosmetic QA is advisory, never a security control (Presentation:RunPlaywrightQa,
        // ReportQaResult's own doc comment) - the assertions here only prove the pipeline ran
        // and produced a real result, not that the report is issue-free.
        Assert.NotNull(sanitized);
        Assert.NotEmpty(qaResult.Screenshot);
    }
}
