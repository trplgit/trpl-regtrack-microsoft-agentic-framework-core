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

    /// <summary>
    /// [FIXED, STALE TEST FOUND LIVE 2026-09-09] Used to assert
    /// `provider.GetRequiredService&lt;IReportHtmlAgent&gt;()` directly - that registration shape
    /// was replaced by `IReadOnlyDictionary&lt;string, IReportHtmlAgent&gt;` back on 2026-09-08
    /// (see this file's own history comments on the `dimension_selection` entry), and nobody
    /// updated this test then, so it had been failing independent of any of today's changes.
    /// Asserts the real registered shape instead - every key `AddInsightsPaidReportAgents`
    /// actually registers, dimension-specific overrides included.
    /// </summary>
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

        var htmlAgents = provider.GetRequiredService<IReadOnlyDictionary<string, IReportHtmlAgent>>();
        Assert.NotNull(htmlAgents["fixed_holistic"]);
        Assert.NotNull(htmlAgents["dimension_selection"]);
        Assert.NotNull(htmlAgents["dimension_selection:Location"]);
        Assert.NotNull(htmlAgents["dimension_selection:Users"]);
        Assert.NotNull(htmlAgents["dimension_selection:Departments"]);
        Assert.NotNull(htmlAgents["dimension_selection:BacklogAging"]);
        Assert.NotNull(htmlAgents["dimension_selection:Act"]);
        Assert.NotNull(htmlAgents["dimension_selection:Licence"]);
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
