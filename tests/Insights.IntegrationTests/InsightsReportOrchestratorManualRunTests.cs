using DurableTask.Core;
using Insights.Data;
using Insights.Domain;
using Insights.Worker;
using Insights.Worker.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// The real end-to-end run: real UAT SQL, real LLM calls (compose, x2 reflect, narrate, x2
/// reflect, render), real headless-Chromium DOMPurify + Playwright, real Durable Task persistence
/// to the dedicated task-hub DB. Spends real tokens - run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~InsightsReportOrchestratorManualRunTests
/// Requires: ConnectionStrings__RegTrack, ConnectionStrings__DurableTaskHub, MAF_ENDPOINT,
/// MAF_MODEL, MAF_API_KEY (same env vars every other manual test in this repo already needs), AND
/// PLAYWRIGHT_BROWSERS_PATH (missing it does not fail fast - the rendering stage just sits there
/// indefinitely waiting on a browser launch that has nowhere to find Chromium; confirmed live
/// 2026-08-21 - a run stalled at stage 6/7 for 12+ minutes with this unset, then completed in
/// under 90 seconds once it was).
///
/// CONFIRMED COMPLETE 2026-08-21: tenant 29, full 12-activity/7-stage chain, real GPT-5.2 calls,
/// real DOMPurify+Playwright, RuntimeStatus=Completed, real ~37,000-char HTML artifact produced.
/// Getting there also surfaced two transient DurableTask.SqlServer connection errors on the
/// CLIENT's own status-polling calls (SqlException: Execution Timeout Expired; then a "Physical
/// connection is not usable" transport error) - both while the orchestration itself kept
/// progressing normally underneath, confirmed by querying dt.vInstances directly. Polling below
/// now retries past exactly this instead of giving up on one hiccup, matching what actually
/// happened rather than assuming a single WaitForOrchestrationAsync call is reliable enough.
/// </summary>
public sealed class InsightsReportOrchestratorManualRunTests(ITestOutputHelper output)
{
    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test - see the class doc comment.");

    private static IConfiguration BuildConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RegTrack"] = RequireEnv("ConnectionStrings__RegTrack"),
            ["ConnectionStrings:DurableTaskHub"] = RequireEnv("ConnectionStrings__DurableTaskHub"),
            ["Llm:Maf:Endpoint"] = RequireEnv("MAF_ENDPOINT"),
            ["Llm:Maf:Model"] = RequireEnv("MAF_MODEL"),
            ["Llm:Maf:ApiKey"] = RequireEnv("MAF_API_KEY"),
            ["Agents:PromptDirectory"] = "./prompts",
        })
        .Build();

    // Every other manual test in this repo used tenant 23 - it turns out NOT to be mapped-and-
    // enabled for the paid product (19) in UAT. Queried ProductMapping directly (2026-08-21,
    // UatTestDataManualTests.ListPaidEntitledTenants): only two UAT tenants are paid-entitled,
    // 29 and 1285. Tenant 1285 is product-entitled but has zero enabled recipients in UAT (a
    // real, separate gate - EntitlementDecision.ExitNoRecipients), so it can't reach Composing
    // either. With 1285 excluded, 29 is the only tenant this repo can currently drive end-to-end
    // without inserting UAT test data (matching the free tier's tenant-1490 precedent, not done
    // here - out of scope for this test, flagged as a known limitation instead of silently
    // working around it).
    [Theory]
    [InlineData(29, 38)] // 89% of estate under a soft-deleted parent - CLAUDE.md's tenant profile table
    public async Task RunAsync_RealTenant_ReachesCompleteStatus(int tenantId, int userId)
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();

        services.AddInsightsData(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddInsightsOrchestration(configuration);
        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        try
        {
            var client = provider.GetRequiredService<TaskHubClient>();
            var input = new InsightsReportOrchestrationInput(tenantId, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26", userId);

            var instance = await client.CreateOrchestrationInstanceAsync(InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, null, input);
            var state = await PollUntilTerminalAsync(client, instance.InstanceId, TimeSpan.FromMinutes(15));

            output.WriteLine($"Tenant {tenantId}: {state.OrchestrationStatus}, final status {state.Status}");
            if (state.OrchestrationStatus != OrchestrationStatus.Completed)
                output.WriteLine($"Output/failure detail: {state.Output}");

            Assert.Equal(OrchestrationStatus.Completed, state.OrchestrationStatus);
        }
        finally
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
                await hosted.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Up to 9 sequential LLM calls in the worst case (2x compose, 2x composition-reflect, 2x
    /// narrate, 2x narrative-reflect, 1x render) if both bounded reflection loops actually revise
    /// once - polls every 15s rather than one long WaitForOrchestrationAsync call, and swallows
    /// (logs, retries) a transient SqlException on any single poll rather than failing the whole
    /// run on it - the orchestration itself is durable against exactly this kind of blip; the test
    /// client polling it should be too, not the weakest link in verifying that it is.
    /// </summary>
    private async Task<OrchestrationState> PollUntilTerminalAsync(TaskHubClient client, string instanceId, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow.Add(budget);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var state = await client.GetOrchestrationStateAsync(instanceId);
                if (state is not null)
                {
                    output.WriteLine($"{DateTime.UtcNow:HH:mm:ss} status={state.OrchestrationStatus} custom={state.Status}");
                    if (state.OrchestrationStatus is OrchestrationStatus.Completed or OrchestrationStatus.Failed)
                        return state;
                }
            }
            catch (Exception ex)
            {
                output.WriteLine($"{DateTime.UtcNow:HH:mm:ss} poll error (retrying): {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(15));
        }
        throw new TimeoutException($"Instance {instanceId} did not reach a terminal state within {budget}.");
    }
}
