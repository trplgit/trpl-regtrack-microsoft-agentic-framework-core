using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// NOT part of the automated suite in spirit - spends real LLM tokens (narrate) against real UAT
/// dimension data, and runs the result through the real PublishGate. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~NarrativeAgentManualRunTests
/// Requires: ConnectionStrings__RegTrack, MAF_ENDPOINT, MAF_MODEL, MAF_API_KEY
/// [UPDATED 2026-09-11] Composition used to be a real LLM call here (MafCompositionAgent, since
/// deleted along with "compliance_health") - now deterministic (FixedHolisticComposition.Build,
/// zero tokens, zero I/O), matching the real orchestrator's own fixed_holistic path exactly.
/// </summary>
public sealed class NarrativeAgentManualRunTests(ITestOutputHelper output)
{
    private static string ConnectionString => RequireEnv("ConnectionStrings__RegTrack");

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test - see the class doc comment.");

    [Fact]
    public async Task NarrateAsync_WritesProse_ThatPassesTheRealPublishGate()
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
        output.WriteLine($"Composed hero: {plan.Hero.Block}");

        var narrativeInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "03_narrative.md"));
        var narrativeAgent = new MafNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeAgent", "Writes prose from typed assertions only.", narrativeInstructions));
        var narrative = (await narrativeAgent.NarrateAsync(plan, assertions, findings)).Value;

        output.WriteLine("");
        output.WriteLine("--- Narrative ---");
        foreach (var block in narrative.Blocks)
        {
            output.WriteLine($"[{block.Block}] assertion_ids_used: {string.Join(",", block.AssertionIdsUsed ?? [])}");
            output.WriteLine(block.Prose);
            output.WriteLine("");
        }

        var scopeRepository = new SqlScopeRepository(ConnectionString);
        var gate = new PublishGate(scopeRepository);
        var gateResult = await gate.EvaluateAsync(userId, customerId, narrative, assertions);

        output.WriteLine("--- Publish gate ---");
        output.WriteLine($"Approved: {gateResult.Approved}");
        foreach (var diagnostic in gateResult.InternalDiagnostics)
            output.WriteLine($"  {diagnostic}");

        Assert.NotEmpty(narrative.Blocks);
        Assert.True(gateResult.Approved, string.Join("; ", gateResult.InternalDiagnostics));
    }
}
