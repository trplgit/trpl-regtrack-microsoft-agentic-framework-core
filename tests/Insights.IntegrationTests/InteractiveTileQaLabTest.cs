using Insights.Agents;
using Insights.Presentation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// [LAB, 2026-10-07] Fast iteration loop for InteractiveTileChecker against a REAL, already-rendered
/// report HTML file, no live orchestration, no real tenant call - matches
/// FiveNewDimensionsFreehandLabTest.cs's own "NOT part of the automated suite, spends real LLM
/// tokens" posture, but needs no Compose/Narrate/Render chain since it starts from HTML already on
/// disk. Point ReportHtmlPath at any real generated report; run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~InteractiveTileQaLabTest
/// Requires: Llm:VisionQa:Endpoint/Model/ApiKey as environment variables (see RequireEnv below) and
/// the named HTML file to exist on disk.
/// </summary>
public sealed class InteractiveTileQaLabTest(ITestOutputHelper output) : IAsyncLifetime
{
    // [FOUND LIVE 2026-10-07] Entity's fixed_holistic template does NOT use section.card at all
    // (it has its own di-pane/di-snaptile/di-kpi__pair markup) - pointing this at
    // Motul-Entity-Insights.html produced a degenerate "0 findings" result (zero section.card
    // elements matched, not a real clean pass). Act is a real dimension_selection/freehand report,
    // which does use the shared section.card convention - this is the file that actually exercises
    // the checker's real selector logic.
    private const string ReportHtmlPath = @"D:\trpl-reginsights-dev\Motul-Insights-Reports\Motul-Act-Insights.html";

    private IPlaywright? playwright;
    private IBrowser? browser;
    private string? screenshotDir;

    public async Task InitializeAsync()
    {
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync();
        screenshotDir = Path.Combine(Path.GetTempPath(), "insights-tile-qa-lab", Guid.NewGuid().ToString());
        Directory.CreateDirectory(screenshotDir);
    }

    public async Task DisposeAsync()
    {
        if (browser is not null) await browser.DisposeAsync();
        playwright?.Dispose();
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Set {name} before running this lab test.");

    [Fact]
    public async Task FindIssuesAgainstARealSavedReport()
    {
        Assert.True(File.Exists(ReportHtmlPath), $"Expected a real report HTML file at {ReportHtmlPath} - point ReportHtmlPath at one you have on disk.");
        var html = await File.ReadAllTextAsync(ReportHtmlPath);

        var endpoint = RequireEnv("Llm__VisionQa__Endpoint");
        var model = RequireEnv("Llm__VisionQa__Model");
        var apiKey = RequireEnv("Llm__VisionQa__ApiKey");
        var reviewAgent = new MafTileGlitchReviewAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "TileGlitchReviewAgent", "Lab run.",
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "prompts", "07_tile_glitch_review.md"))));

        var checker = new InteractiveTileChecker(browser!, reviewAgent, screenshotDir!, NullLogger<InteractiveTileChecker>.Instance);
        var findings = await checker.FindIssuesAsync(html);

        output.WriteLine($"Found {findings.Count} finding(s).");
        foreach (var f in findings)
            output.WriteLine($"  [{f.Severity}] {f.CardTitle} - {f.Interaction}: {f.TechnicalDescription} (before={f.BeforeScreenshotPath}, after={f.AfterScreenshotPath})");
    }
}
