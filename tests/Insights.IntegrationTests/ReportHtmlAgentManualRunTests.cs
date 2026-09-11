using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// NOT part of the automated suite in spirit - spends real LLM tokens (narrate, render) against
/// real UAT dimension data, and saves the rendered HTML to the scratchpad so it can actually be
/// opened in a browser. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~ReportHtmlAgentManualRunTests
/// Requires: ConnectionStrings__RegTrack, MAF_ENDPOINT, MAF_MODEL, MAF_API_KEY
/// Optional: REPORT_HTML_OUTPUT_PATH (defaults to a scratch file under the test output directory)
/// Optional: INSIGHTS_USER_ID, INSIGHTS_CUSTOMER_ID (default 36, 23 - validated (userId, customerId)
/// pairs for other tenants are listed in DimensionRepositoryTests.ValidatedTenants)
/// [UPDATED 2026-09-11] Composition used to be a real LLM call here (MafCompositionAgent, since
/// deleted along with "compliance_health") - now deterministic (FixedHolisticComposition.Build,
/// zero tokens, zero I/O). Render now uses fixed_holistic's real prompt
/// (05_report_html_fixed_holistic.md) - the old 05_report_html.md ("compliance_health"'s prompt)
/// was itself already deleted 2026-09-01, so this test's real render call would have failed with a
/// file-not-found before this fix regardless of the composition change.
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
        var userId = int.Parse(Environment.GetEnvironmentVariable("INSIGHTS_USER_ID") ?? "36");
        var customerId = int.Parse(Environment.GetEnvironmentVariable("INSIGHTS_CUSTOMER_ID") ?? "23");

        var dimensionRepository = new SqlDimensionRepository(ConnectionString);
        var location = await dimensionRepository.GetLocationAsync(userId, customerId);
        var risk = await dimensionRepository.GetRiskAsync(userId, customerId);
        var assertions = location.Assertions.Concat(risk.Assertions).ToList();
        var findings = location.Findings.Concat(risk.Findings).ToList();

        async Task<string> LoadPromptAsync(string fileName) =>
            await File.ReadAllTextAsync(Path.Combine(promptsDir, fileName));

        var plan = FixedHolisticComposition.Build();

        var narrativeAgent = new MafNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeAgent", "Writes prose from typed assertions only.", await LoadPromptAsync("03_narrative.md")));
        var narrative = (await narrativeAgent.NarrateAsync(plan, assertions, findings)).Value;
        output.WriteLine($"Composed + narrated {narrative.Blocks.Count} blocks");

        var htmlAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "ReportHtmlAgent", "Renders the approved report as self-contained HTML.", await LoadPromptAsync("05_report_html_fixed_holistic.md")));
        var html = (await htmlAgent.RenderAsync(plan, narrative, assertions, tenantName: $"Tenant {customerId} (UAT)", reportType: FixedHolisticComposition.ReportType, generatedAt: DateTime.UtcNow, locationRows: location.Rows)).Value;

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
