using Insights.Agents;
using Insights.Data;
using Insights.Worker;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// NOT part of the automated suite in spirit - the most expensive manual test in the project:
/// up to 4 LLM calls just for composition (compose + up to 2 reflect/re-compose rounds) and up
/// to 4 more for narrative, against real UAT dimension data. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~ReportCompositionPipelineManualRunTests
/// Requires: ConnectionStrings__RegTrack, MAF_ENDPOINT, MAF_MODEL, MAF_API_KEY
/// </summary>
public sealed class ReportCompositionPipelineManualRunTests(ITestOutputHelper output)
{
    private static string ConnectionString => RequireEnv("ConnectionStrings__RegTrack");

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test - see the class doc comment.");

    [Fact]
    public async Task RunAsync_ProducesAGatedReport_WithTheBoundedRevisionLoopActuallyEngaging()
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
        var compositionReflectionAgent = new MafCompositionReflectionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "CompositionReflectionAgent", "Critiques a composition plan.", await LoadPromptAsync("02_composition_reflection.md")));
        var narrativeAgent = new MafNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeAgent", "Writes prose from typed assertions only.", await LoadPromptAsync("03_narrative.md")));
        var narrativeReflectionAgent = new MafNarrativeReflectionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeReflectionAgent", "Critiques narrative prose.", await LoadPromptAsync("04_narrative_reflection.md")));
        var publishGate = new PublishGate(new SqlScopeRepository(ConnectionString));

        var pipeline = new ReportCompositionPipeline(
            compositionAgent, compositionReflectionAgent, narrativeAgent, narrativeReflectionAgent,
            publishGate, maxReflectionIterations: 2);

        var result = await pipeline.RunAsync(
            userId, customerId, dimensionResults, assertions, findings,
            tenantShape: "multi_entity", reportType: "compliance_health");

        output.WriteLine($"Composition attempts: {result.CompositionAttempts}");
        output.WriteLine($"Narrative attempts: {result.NarrativeAttempts}");
        output.WriteLine($"Gate approved: {result.Approved}");
        foreach (var d in result.GateResult.InternalDiagnostics)
            output.WriteLine($"  gate diagnostic: {d}");
        output.WriteLine("");
        output.WriteLine($"Final hero: {result.FinalPlan.Hero.Block} - {result.FinalPlan.Hero.Reason}");
        output.WriteLine("");
        output.WriteLine("--- Final narrative ---");
        foreach (var block in result.FinalNarrative.Blocks)
        {
            output.WriteLine($"[{block.Block}]");
            output.WriteLine(block.Prose);
            output.WriteLine("");
        }

        Assert.True(result.Approved, string.Join("; ", result.GateResult.InternalDiagnostics));
        Assert.InRange(result.CompositionAttempts, 1, 3);
        Assert.InRange(result.NarrativeAttempts, 1, 3);
    }
}
