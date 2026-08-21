using Insights.Agents;
using Insights.Presentation;
using Insights.Worker.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Insights.UnitTests;

public class PaidReportAgentsRegistrationTests
{
    private static IConfiguration BuildConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Maf:Endpoint"] = "https://example.invalid/openai/v1",
            ["Llm:Maf:Model"] = "gpt-5.2",
            ["Llm:Maf:ApiKey"] = "test-key",
            ["Agents:PromptDirectory"] = "./prompts",
        })
        .Build();

    [Fact]
    public void AddInsightsPaidReportAgents_RegistersEveryAgentInterface()
    {
        var services = new ServiceCollection();
        services.AddInsightsPaidReportAgents(BuildConfiguration());
        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ICompositionAgent>());
        Assert.NotNull(provider.GetRequiredService<ICompositionReflectionAgent>());
        Assert.NotNull(provider.GetRequiredService<INarrativeAgent>());
        Assert.NotNull(provider.GetRequiredService<INarrativeReflectionAgent>());
        Assert.NotNull(provider.GetRequiredService<IReportHtmlAgent>());
    }

    [Fact]
    public void AddInsightsPaidReportAgents_MissingEndpoint_ThrowsAtRegistrationNotFirstUse()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agents:PromptDirectory"] = "./prompts" }).Build();

        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddInsightsPaidReportAgents(configuration));
    }
}
