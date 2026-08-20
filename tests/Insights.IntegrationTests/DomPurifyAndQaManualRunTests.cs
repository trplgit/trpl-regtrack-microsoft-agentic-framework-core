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
        // the sanitized output proves DOMPurify's own serialization (e.g. dropping the doctype -
        // see DomPurifySanitizer.ReattachDoctypeIfMissing) did not silently produce something the
        // normalizer would now reject.
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
