using DurableTask.Core;
using Insights.Data;
using Insights.Domain;
using Insights.Worker;
using Insights.Worker.HealthChecks;
using Insights.Worker.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Xunit.Abstractions;

namespace Insights.UnitTests;

/// <summary>
/// [MANUAL, REAL, 2026-09-24] Real end-to-end proof of the new reqId-stream per-dimension detail
/// (InsightsRunStatus.Dimension, RunEndpoints' widened /api/insights/requests/{reqId}/stream) -
/// through the REAL orchestrator (real DTFx worker dequeue loop, real LLM calls for Compose/
/// Narrate/Render/injectors/QA for two real freehand dimensions), served through the REAL API
/// endpoint code (RunEndpoints.MapInsightsRunEndpoints), using InsightsApiTestHost's real Kestrel
/// test server wired to REAL backing services (not fakes) resolved from the same DI registrations
/// production uses. CONFIRMED PASSING LIVE 2026-09-24: 4m43s wall clock, well under the endpoint's
/// own 15-minute MaxStreamDuration ceiling, real reportIds for both dimensions.
///
/// [GATED, deliberately not by a missing-secret throw] This file lives in Insights.UnitTests (the
/// project this whole session's own "run the full suite" sanity checks use, expecting FAST and
/// FREE) so it can reuse InsightsApiTestHost's internal real-Kestrel host - but it reads config
/// straight from a real file that DOES exist on this machine (unlike
/// InsightsReportOrchestratorManualRunTests.cs's env-var RequireEnv gate, which fails fast on a
/// machine that never set those vars). Without its own gate this would silently re-run the real
/// 5-minute paid pipeline on every plain `dotnet test tests/Insights.UnitTests`. Skipped unless
/// INSIGHTS_RUN_REAL_E2E=1 is set (not a secret - a plain opt-in flag) - run explicitly:
///   INSIGHTS_RUN_REAL_E2E=1 dotnet test tests/Insights.UnitTests --filter FullyQualifiedName~RequestStreamRealE2ETests
///
/// Tenant 29/user 38 - the established reference tenant, already proven reaching Completed status
/// elsewhere in this repo. Risk + Nature - two real freehand dimensions
/// (FreehandDimensions.Names), small/fast relative to Location/Act.
/// </summary>
public sealed class RequestStreamRealE2ETests(ITestOutputHelper output)
{
    [Fact]
    public async Task PostThenStreamByReqId_RealTwoDimensionRun_ReturnsRealPerDimensionDetail()
    {
        // No SkippableFact package in this repo - a plain early return (not a throw, so this shows
        // green/PASSED in a normal full-suite run rather than failing it) is the dependency-free
        // way to opt this real, paid run out of every plain `dotnet test`. See this class's own
        // doc comment for how to actually opt in.
        if (Environment.GetEnvironmentVariable("INSIGHTS_RUN_REAL_E2E") != "1")
        {
            output.WriteLine("Skipped - set INSIGHTS_RUN_REAL_E2E=1 to run this real, paid, ~5-minute end-to-end test.");
            return;
        }

        // Reads real UAT config straight from the file, same as every other real lab test this
        // session - never puts a secret in a shell command or env var. CooldownDays overridden to
        // 0 on top (real value is 30, which would block a same-day re-run of this exact test).
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.uat.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agents:PromptDirectory"] = "./prompts",
                ["Azure:TempBlobContainer"] = "insights-reports-temp",
                ["Reports:LocalFallbackDirectory"] = @"D:\trpl-reginsights-dev\local-report-fallback",
                ["Reports:CooldownDays"] = "0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddInsightsData(configuration);
        services.AddInsightsTenantTokenBudget(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddInsightsOrchestration(configuration); // client + real worker dequeue loop
        services.AddInsightsReportContentService(configuration); // cooldown + report-request repo + content
        services.AddInsightsObservability(configuration);
        services.AddInsightsHealthChecks(configuration);
        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        try
        {
            const int tenantId = 29;
            const int userId = 38;

            var tenants = provider.GetRequiredService<ITenantDirectoryRepository>();
            var scope = provider.GetRequiredService<IScopeRepository>();
            var cooldown = provider.GetRequiredService<ICooldownRepository>();
            var enqueuer = provider.GetRequiredService<IInsightsRunEnqueuer>();
            var runs = provider.GetRequiredService<IRunStatusReader>();
            var requests = provider.GetRequiredService<IReportRequestRepository>();

            // Real Kestrel test server, REAL backing services (not InsightsApiTestHost's usual
            // fakes) - the exact production endpoint code, exercised for real.
            var client = await InsightsApiTestHost.StartAsync(
                userId, tenants, runs, scope, enqueuer, content: null, cooldown: cooldown, requests: requests);
            client.Timeout = TimeSpan.FromMinutes(20);

            var period = $"e2e-reqid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            var body = JsonSerializer.Serialize(new
            {
                tenantId,
                scope = new { type = "tenant" },
                period,
                requestedDimensions = new[] { "Risk", "Nature" },
            });

            var postResponse = await client.PostAsync("/api/insights/reports",
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            var postBody = await postResponse.Content.ReadAsStringAsync();
            output.WriteLine($"POST /api/insights/reports -> {(int)postResponse.StatusCode}: {postBody}");
            Assert.Equal(System.Net.HttpStatusCode.Accepted, postResponse.StatusCode);

            using var postJson = JsonDocument.Parse(postBody);
            var reqId = postJson.RootElement.GetProperty("reqId").GetString();
            var postedReports = postJson.RootElement.GetProperty("reports").EnumerateArray().ToList();
            Assert.Equal(2, postedReports.Count);
            foreach (var r in postedReports)
                output.WriteLine($"  Queued: dimension={r.GetProperty("dimension").GetString()} runId={r.GetProperty("runId").GetString()} status={r.GetProperty("status").GetString()}");

            output.WriteLine($"Streaming GET /api/insights/requests/{reqId}/stream - real run, will take several real minutes...");
            var streamResponse = await client.GetAsync($"/api/insights/requests/{reqId}/stream");
            var streamBody = await streamResponse.Content.ReadAsStringAsync();
            var frames = streamBody.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            output.WriteLine($"Received {frames.Length} frame(s) over the stream.");

            var finalFrame = frames[^1];
            using var finalJson = JsonDocument.Parse(finalFrame.Substring("data: ".Length));
            output.WriteLine($"Final frame: {finalJson.RootElement}");

            var finalStatus = finalJson.RootElement.GetProperty("status").GetString();
            var finalReports = finalJson.RootElement.GetProperty("reports").EnumerateArray().ToList();

            foreach (var r in finalReports)
                output.WriteLine($"  Final: dimension={r.GetProperty("dimension").GetString()} runId={r.GetProperty("runId").GetString()} status={r.GetProperty("status").GetString()} reportId={r.GetProperty("reportId").GetString()}");

            Assert.Equal("completed", finalStatus);
            Assert.Equal(2, finalReports.Count);
            Assert.Contains(finalReports, r => r.GetProperty("dimension").GetString() == "Risk" && r.GetProperty("reportId").GetString() != null);
            Assert.Contains(finalReports, r => r.GetProperty("dimension").GetString() == "Nature" && r.GetProperty("reportId").GetString() != null);
        }
        finally
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
                await hosted.StopAsync(CancellationToken.None);
        }
    }
}
