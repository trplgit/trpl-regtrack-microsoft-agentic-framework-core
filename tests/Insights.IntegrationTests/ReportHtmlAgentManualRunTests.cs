using Insights.Agents;
using Insights.Data;
using Insights.Presentation;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// NOT part of the automated suite in spirit - spends real LLM tokens (compose, narrate, render)
/// against real UAT dimension data, and saves the rendered HTML to the scratchpad so it can
/// actually be opened in a browser. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~ReportHtmlAgentManualRunTests
/// Requires: ConnectionStrings__RegTrack, MAF_ENDPOINT, MAF_MODEL, MAF_API_KEY
/// Optional: REPORT_HTML_OUTPUT_PATH (defaults to a scratch file under the test output directory)
/// </summary>
public sealed class ReportHtmlAgentManualRunTests(ITestOutputHelper output)
{
    private static string ConnectionString => RequireEnv("ConnectionStrings__RegTrack");

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test - see the class doc comment.");

    [Fact]
    public async Task RenderAsync_ProducesHtml_ThatPassesTheEmitNormalizer()
    {
        var endpoint = RequireEnv("MAF_ENDPOINT");
        var model = RequireEnv("MAF_MODEL");
        var apiKey = RequireEnv("MAF_API_KEY");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
        const int userId = 36, customerId = 23;

        var dimensionRepository = new SqlDimensionRepository(ConnectionString);
        var location = await dimensionRepository.GetLocationAsync(userId, customerId);
        var risk = await dimensionRepository.GetRiskAsync(userId, customerId);
        var dimensionResults = new Dictionary<string, object> { ["Location"] = location, ["Risk"] = risk };
        var assertions = location.Assertions.Concat(risk.Assertions).ToList();
        var findings = location.Findings.Concat(risk.Findings).ToList();

        async Task<string> LoadPromptAsync(string fileName) =>
            await File.ReadAllTextAsync(Path.Combine(promptsDir, fileName));

        var compositionAgent = new MafCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "CompositionAgent", "Decides report structure.", await LoadPromptAsync("01_composition.md")));
        var plan = await compositionAgent.ComposeAsync(dimensionResults, tenantShape: "multi_entity", reportType: "compliance_health");

        var narrativeAgent = new MafNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeAgent", "Writes prose from typed assertions only.", await LoadPromptAsync("03_narrative.md")));
        var narrative = await narrativeAgent.NarrateAsync(plan, assertions, findings);
        output.WriteLine($"Composed + narrated {narrative.Blocks.Count} blocks");

        var htmlAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "ReportHtmlAgent", "Renders the approved report as self-contained HTML.", await LoadPromptAsync("05_report_html.md")));
        var html = await htmlAgent.RenderAsync(plan, narrative, tenantName: "Tenant 23 (UAT)", reportType: "compliance_health", generatedAt: DateTime.UtcNow);

        var outputPath = Environment.GetEnvironmentVariable("REPORT_HTML_OUTPUT_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "rendered-report.html");
        await File.WriteAllTextAsync(outputPath, html);
        output.WriteLine($"HTML written to: {outputPath}");
        output.WriteLine($"Length: {html.Length} chars");

        var normalizerResult = ReportEmitNormalizer.Evaluate(html);
        output.WriteLine($"Normalizer approved: {normalizerResult.Approved}");
        foreach (var violation in normalizerResult.Violations)
            output.WriteLine($"  VIOLATION: {violation}");

        Assert.True(normalizerResult.Approved, string.Join("; ", normalizerResult.Violations));
    }
}
