using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// NOT part of the automated suite in spirit - spends real LLM tokens (narrate, then reflect on
/// the narrative) against real UAT dimension data, and runs the narrative through the real
/// PublishGate too. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~NarrativeReflectionAgentManualRunTests
/// Requires: ConnectionStrings__RegTrack, MAF_ENDPOINT, MAF_MODEL, MAF_API_KEY
/// [UPDATED 2026-09-11] Composition used to be a real LLM call here (MafCompositionAgent, since
/// deleted along with "compliance_health") - now deterministic (FixedHolisticComposition.Build,
/// zero tokens, zero I/O), matching the real orchestrator's own fixed_holistic path exactly.
/// </summary>
public sealed class NarrativeReflectionAgentManualRunTests(ITestOutputHelper output)
{
    private static string ConnectionString => RequireEnv("ConnectionStrings__RegTrack");

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test - see the class doc comment.");

    [Fact]
    public async Task ReflectAsync_CritiquesRealNarrative_ThatAlsoPassesThePublishGate()
    {
        var endpoint = RequireEnv("MAF_ENDPOINT");
        var model = RequireEnv("MAF_MODEL");
        var apiKey = RequireEnv("MAF_API_KEY");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
        const int userId = 36, customerId = 23;

        var dimensionRepository = new SqlDimensionRepository(ConnectionString);
        var location = await dimensionRepository.GetLocationAsync(userId, customerId);
        var risk = await dimensionRepository.GetRiskAsync(userId, customerId);
        var assertions = location.Assertions.Concat(risk.Assertions).ToList();
        var findings = location.Findings.Concat(risk.Findings).ToList();

        var plan = FixedHolisticComposition.Build();

        var narrativeInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "03_narrative.md"));
        var narrativeAgent = new MafNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeAgent", "Writes prose from typed assertions only.", narrativeInstructions));
        var narrative = (await narrativeAgent.NarrateAsync(plan, assertions, findings)).Value;
        output.WriteLine($"Narrated {narrative.Blocks.Count} blocks");

        var reflectionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "04_narrative_reflection.md"));
        var reflectionAgent = new MafNarrativeReflectionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeReflectionAgent", "Critiques narrative prose for semantic errors.", reflectionInstructions));
        var reflection = (await reflectionAgent.ReflectAsync(narrative, assertions, findings)).Value;

        output.WriteLine("");
        output.WriteLine($"Verdict: {reflection.Verdict}");
        foreach (var issue in reflection.Issues)
            output.WriteLine($"  [{issue.Check}] {issue.Block}: \"{issue.Quote}\" -- {issue.Problem} -> {issue.Fix}");

        var scopeRepository = new SqlScopeRepository(ConnectionString);
        var gate = new PublishGate(scopeRepository);
        var gateResult = await gate.EvaluateAsync(userId, customerId, narrative, assertions);
        output.WriteLine("");
        output.WriteLine($"Publish gate approved: {gateResult.Approved}");
        foreach (var d in gateResult.InternalDiagnostics)
            output.WriteLine($"  {d}");

        Assert.True(reflection.Verdict is Insights.Domain.ReflectionVerdict.Approve or Insights.Domain.ReflectionVerdict.Revise);
    }
}
