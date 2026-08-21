using Insights.Agents;
using Insights.Data;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// NOT part of the automated suite in spirit - this spends real LLM tokens (GPT-5.2 via Azure AI
/// Foundry) against real UAT dimension data. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~CompositionAgentManualRunTests
/// Requires (none read from any committed file):
///   ConnectionStrings__RegTrack, MAF_ENDPOINT, MAF_MODEL, MAF_API_KEY
/// </summary>
public sealed class CompositionAgentManualRunTests(ITestOutputHelper output)
{
    private static string ConnectionString => RequireEnv("ConnectionStrings__RegTrack");

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test - see the class doc comment.");

    [Fact]
    public async Task ComposeAsync_ProducesAPlan_FromRealLocationAndRiskData()
    {
        var endpoint = RequireEnv("MAF_ENDPOINT");
        var model = RequireEnv("MAF_MODEL");
        var apiKey = RequireEnv("MAF_API_KEY");

        var instructions = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "prompts", "01_composition.md"));

        var repository = new SqlDimensionRepository(ConnectionString);
        var location = await repository.GetLocationAsync(userId: 36, customerId: 23);
        var risk = await repository.GetRiskAsync(userId: 36, customerId: 23);

        var dimensionResults = new Dictionary<string, object>
        {
            ["Location"] = location,
            ["Risk"] = risk,
        };

        var mafAgent = MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "CompositionAgent",
            "Decides report structure and emphasis from dimension data (prompts/01_composition.md).",
            instructions);
        var compositionAgent = new MafCompositionAgent(mafAgent);

        var plan = await compositionAgent.ComposeAsync(dimensionResults, tenantShape: "multi_entity", reportType: "compliance_health");

        output.WriteLine($"Hero: {plan.Hero.Block} - {plan.Hero.Reason}");
        output.WriteLine("--- Blocks ---");
        foreach (var block in plan.Blocks)
            output.WriteLine($"  {block.Block} [{block.Emphasis}] findings: {string.Join(",", block.FindingIds ?? [])}");
        output.WriteLine("--- Omitted ---");
        foreach (var omitted in plan.Omitted ?? [])
            output.WriteLine($"  {omitted.Block}: {omitted.Reason}");
        output.WriteLine("--- Data quality to surface ---");
        output.WriteLine(string.Join(", ", plan.DataQualityToSurface ?? []));

        Assert.False(string.IsNullOrWhiteSpace(plan.Hero.Block));
        Assert.NotEmpty(plan.Blocks);
    }
}
