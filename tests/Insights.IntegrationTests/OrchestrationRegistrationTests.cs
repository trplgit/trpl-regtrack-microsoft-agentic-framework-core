using DurableTask.Core;
using Insights.Data;
using Insights.Worker;
using Insights.Worker.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Insights.IntegrationTests;

/// <summary>
/// Resolving TaskHubClient triggers SqlOrchestrationService's eager CreateIfNotExistsAsync - a
/// real round trip to the task-hub database - so this lives here, not in Insights.UnitTests,
/// whose own doc comment promises "no database, no LLM, no email". Requires
/// ConnectionStrings__RegTrack, ConnectionStrings__DurableTaskHub, and the same Llm:Maf:* env vars
/// every other manual/integration test in this repo needs.
/// </summary>
public class OrchestrationRegistrationTests
{
    private static IConfiguration BuildConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:RegTrack"] = Environment.GetEnvironmentVariable("ConnectionStrings__RegTrack")
                ?? throw new InvalidOperationException("Set ConnectionStrings__RegTrack before running this test."),
            ["ConnectionStrings:DurableTaskHub"] = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
                ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running this test."),
            ["Llm:Maf:Endpoint"] = Environment.GetEnvironmentVariable("MAF_ENDPOINT")
                ?? throw new InvalidOperationException("Set MAF_ENDPOINT before running this test."),
            ["Llm:Maf:Model"] = Environment.GetEnvironmentVariable("MAF_MODEL")
                ?? throw new InvalidOperationException("Set MAF_MODEL before running this test."),
            ["Llm:Maf:ApiKey"] = Environment.GetEnvironmentVariable("MAF_API_KEY")
                ?? throw new InvalidOperationException("Set MAF_API_KEY before running this test."),
            ["Agents:PromptDirectory"] = "./prompts",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void AddInsightsOrchestration_ResolvesTaskHubClientAndWorker()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();

        services.AddInsightsData(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddInsightsOrchestration(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TaskHubClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TaskHubWorker>());
    }
}
