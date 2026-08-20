using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// NOT part of the automated suite in spirit - spends real LLM tokens (two calls: compose, then
/// reflect) against real UAT dimension data. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~CompositionReflectionAgentManualRunTests
/// Requires: ConnectionStrings__RegTrack, MAF_ENDPOINT, MAF_MODEL, MAF_API_KEY
/// </summary>
public sealed class CompositionReflectionAgentManualRunTests(ITestOutputHelper output)
{
    private static string ConnectionString => RequireEnv("ConnectionStrings__RegTrack");

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test - see the class doc comment.");

    [Fact]
    public async Task ReflectAsync_CritiquesARealComposedPlan()
    {
        var endpoint = RequireEnv("MAF_ENDPOINT");
        var model = RequireEnv("MAF_MODEL");
        var apiKey = RequireEnv("MAF_API_KEY");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");

        var repository = new SqlDimensionRepository(ConnectionString);
        var location = await repository.GetLocationAsync(userId: 36, customerId: 23);
        var risk = await repository.GetRiskAsync(userId: 36, customerId: 23);
        var dimensionResults = new Dictionary<string, object> { ["Location"] = location, ["Risk"] = risk };

        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "01_composition.md"));
        var compositionAgent = new MafCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "CompositionAgent",
            "Decides report structure and emphasis from dimension data.", compositionInstructions));

        var plan = await compositionAgent.ComposeAsync(dimensionResults, tenantShape: "multi_entity", reportType: "compliance_health");
        output.WriteLine($"Composed hero: {plan.Hero.Block} - {plan.Hero.Reason}");
        output.WriteLine($"Composed {plan.Blocks.Count} blocks, {plan.Omitted.Count} omitted");
        output.WriteLine("");

        // The full pool the composition agent actually drew from - what the reflection critic
        // needs to check claims like "is the highest-severity finding the hero" against.
        var assertions = location.Assertions.Concat(risk.Assertions).ToList();
        var findings = location.Findings.Concat(risk.Findings).ToList();

        var reflectionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "02_composition_reflection.md"));
        var reflectionAgent = new MafCompositionReflectionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "CompositionReflectionAgent",
            "Critiques a composition plan before narrative runs.", reflectionInstructions));

        var reflection = await reflectionAgent.ReflectAsync(plan, assertions, findings, tenantShape: "multi_entity");

        output.WriteLine($"Verdict: {reflection.Verdict}");
        foreach (var issue in reflection.Issues)
            output.WriteLine($"  [{issue.Check}] {issue.Block}: {issue.Problem} -> {issue.Fix}");

        Assert.True(reflection.Verdict is ReflectionVerdict.Approve or ReflectionVerdict.Revise);
    }
}
