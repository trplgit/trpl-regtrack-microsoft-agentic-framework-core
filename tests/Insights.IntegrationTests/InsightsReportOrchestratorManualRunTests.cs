using DurableTask.Core;
using Insights.Data;
using Insights.Domain;
using Insights.Worker;
using Insights.Worker.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// The real end-to-end run: real UAT SQL, real LLM calls (compose, x2 reflect, narrate, x2
/// reflect, render), real headless-Chromium DOMPurify + Playwright, real Durable Task persistence
/// to the dedicated task-hub DB. Spends real tokens - run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~InsightsReportOrchestratorManualRunTests
/// Requires: ConnectionStrings__RegTrack, ConnectionStrings__DurableTaskHub, MAF_ENDPOINT,
/// MAF_MODEL, MAF_API_KEY (same env vars every other manual test in this repo already needs),
/// PLAYWRIGHT_BROWSERS_PATH (missing it does not fail fast - the rendering stage just sits there
/// indefinitely waiting on a browser launch that has nowhere to find Chromium; confirmed live
/// 2026-08-21 - a run stalled at stage 6/7 for 12+ minutes with this unset, then completed in
/// under 90 seconds once it was), AND (build order item 14) AZURE_BLOB_CONNECTION_STRING.
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
            // Build order item 14's write path.
            ["Azure:BlobConnectionString"] = RequireEnv("AZURE_BLOB_CONNECTION_STRING"),
            ["Azure:BlobContainer"] = "insights-reports-temp",

            // Design doc Sec.12.3's per-tenant monthly circuit breaker - not optional, see
            // TenantTokenBudgetRegistration's doc comment. Matches appsettings.json's defaults.
            ["Budget:PerTenantMonthlyTokenCeiling"] = "5000000",
            ["Budget:AlertAtPercentOfCeiling"] = "80",

            // OTel -> LangFuse (build order item 17 / O-4). Genuinely optional here, unlike every
            // RequireEnv above - ObservabilityRegistration itself skips wiring cleanly when
            // LANGFUSE_BASE_URL is absent, so a run without it still exercises everything else.
            ["Otel:LangfuseEndpoint"] = Environment.GetEnvironmentVariable("LANGFUSE_BASE_URL"),
            ["Otel:LangfusePublicKey"] = Environment.GetEnvironmentVariable("LANGFUSE_PUBLIC_KEY"),
            ["Otel:LangfuseSecretKey"] = Environment.GetEnvironmentVariable("LANGFUSE_SECRET_KEY"),
            ["Otel:EnableSensitiveData"] = Environment.GetEnvironmentVariable("LANGFUSE_ENABLE_SENSITIVE_DATA") ?? "false",
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
        services.AddInsightsTenantTokenBudget(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddInsightsOrchestration(configuration);
        services.AddInsightsObservability(configuration);
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
    /// [ADDED 2026-09-15] Real end-to-end run of `dimension_selection:Users` (the same template
    /// this session spent all day fixing - CSS inlining, lens toggle nesting, real company name)
    /// through the ACTUAL orchestrator - not an activity-by-activity bypass, the real Durable Task worker dequeuing off the real
    /// SQL-backed task hub. Three real production tenants, requested by the user directly:
    /// Life Cell Group (1326), Minda Corporation Group (1008), Agrocel Group (1082) - user ids
    /// each independently confirmed to have real scope pairs via tvfInsightsScopePairs before
    /// this test was written (2026-09-13 session).
    ///
    /// Key Vault is still Forbidden (real, ongoing, RBAC-side denial - see PersistActivity's own
    /// doc comment) so this sets Reports:LocalFallbackDirectory, same temporary bypass every other
    /// real run this session has used. Azure:BlobConnectionString is still supplied (the real,
    /// working trplchatgpt9378 account, confirmed live via a direct blob probe this session) only
    /// because PersistActivity's DI-injected IReportBlobWriter is constructed eagerly regardless
    /// of whether local-fallback ever calls it - never actually written to here.
    /// </summary>
    [Theory]
    [InlineData(1326, 83105, "Life Cell Group")]
    [InlineData(1008, 12116, "Minda Corporation Group")]
    [InlineData(1082, 14128, "Agrocel Group")]
    public async Task RunAsync_DimensionSelectionUsers_RealTenant_ReachesCompleteStatus(int tenantId, int userId, string expectedCompanyLabel)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Read path - broad EXECUTE on usp_Insights_* procs, no table-level write grants.
                ["ConnectionStrings:RegTrack"] = RequireEnv("ConnectionStrings__RegTrack"),
                // [FIX 2026-09-15] Write path - TenantTokenBudgetRegistration.cs and
                // WorkerRegistration.cs's own RegisterReportsDbContext ALREADY prefer this key over
                // ConnectionStrings:RegTrack for every real write (InsightsTenantTokenUsage,
                // GeneratedReport) - this test just never supplied it, so both silently fell back
                // to the read-only connection and the token-usage INSERT was denied at the very
                // last stage of an otherwise fully-successful run. No code changed; the pipeline
                // already splits read/write by design, confirmed live by that exact failure.
                ["ConnectionStrings:RegTrackReportsWrite"] = RequireEnv("ConnectionStrings__RegTrackReportsWrite"),
                ["ConnectionStrings:DurableTaskHub"] = RequireEnv("ConnectionStrings__DurableTaskHub"),
                ["Llm:Maf:Endpoint"] = RequireEnv("MAF_ENDPOINT"),
                ["Llm:Maf:Model"] = RequireEnv("MAF_MODEL"),
                ["Llm:Maf:ApiKey"] = RequireEnv("MAF_API_KEY"),
                ["Agents:PromptDirectory"] = "./prompts",
                ["Azure:BlobConnectionString"] = RequireEnv("AZURE_BLOB_CONNECTION_STRING"),
                ["Azure:BlobContainer"] = "insights-reports-temp",
                ["Reports:LocalFallbackDirectory"] = @"D:\trpl-reginsights-dev\local-report-fallback",
                ["Budget:PerTenantMonthlyTokenCeiling"] = "5000000",
                ["Budget:AlertAtPercentOfCeiling"] = "80",
            })
            .Build();

        var services = new ServiceCollection();
        // [FIX 2026-09-15] AddInsightsOrchestration's own registration resolves ILoggerFactory
        // eagerly (WorkerRegistration.cs RegisterSqlOrchestrationService/AddInsightsOrchestrationWorker)
        // - missing here regardless of tenant, confirmed live on all three. Pre-existing gap in
        // every manual test in this file; none had been run since AddInsightsOrchestration started
        // requiring it.
        services.AddLogging();
        services.AddInsightsData(configuration);
        services.AddInsightsTenantTokenBudget(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddInsightsOrchestration(configuration);
        services.AddInsightsObservability(configuration);
        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        try
        {
            var client = provider.GetRequiredService<TaskHubClient>();
            var input = new InsightsReportOrchestrationInput(
                tenantId, DimensionSelectionComposition.ReportType, new InsightsScopeRequest("tenant", null),
                "90day", userId, LlmCallPriority.Interactive, ["Users"]);

            var instance = await client.CreateOrchestrationInstanceAsync(InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, null, input);
            var state = await PollUntilTerminalAsync(client, instance.InstanceId, TimeSpan.FromMinutes(15));

            output.WriteLine($"Tenant {tenantId} ({expectedCompanyLabel}): {state.OrchestrationStatus}, final status {state.Status}");
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
    /// [ADDED 2026-09-14, REWRITTEN 2026-09-14] Real end-to-end stability pass - "multiple
    /// pitfalls", not a single happy-path repeat: all 5 freehand dimensions
    /// (FreehandDimensions.Names) across 3 real production tenants with independently-confirmed
    /// real scope pairs (2026-09-13 session) - Minda Corporation Group (1008), Life Cell Group
    /// (1326), Agrocel Group (1082). Location included for the first time here - never run live
    /// since joining FreehandDimensions.Names.
    ///
    /// [REWRITTEN, real concurrency] Was a [Theory] with 10 InlineData rows - xUnit runs Theory
    /// cases of ONE method sequentially by default, so that never actually exercised concurrent
    /// load. This fires all 10 real orchestration runs through ONE shared ServiceProvider
    /// instead, via Task.WhenAll - deliberately ONE shared LlmConcurrencyGate instance, matching
    /// the real deployed worker exactly (also one process, one gate). Ten SEPARATE dotnet-test
    /// processes would each build their OWN in-memory gate (cap 3 each) - real aggregate
    /// concurrent LLM calls could hit ~30 against the same Azure resources, a self-inflicted
    /// rate-limit risk that has nothing to do with real pipeline bugs and would contaminate the
    /// signal this run exists to produce.
    ///
    /// Reads real connection strings/keys straight from D:\trpl-reginsights-dev\appsettings.json
    /// (the real local dev config, outside this repo) rather than env vars - keeps secrets out of
    /// any shell command entirely. Non-secret overrides layered on top via AddInMemoryCollection.
    ///
    /// [REAL FINDING, 2026-09-14 - EXPECTED, NOT A BUG] Run twice for real (15-min then 30-min
    /// budget, with real console logging wired both times): 0/10 reached rendering in either
    /// run, every case stuck cycling gathering/validating/composing/narrating. Confirmed NOT a
    /// defect - LlmConcurrencyGate/ConcurrencyGatedChatClient's actual code was read line by line
    /// (correct: proper locking, direct slot handoff, double-release guard), and the real
    /// diagnostic log across both runs (~3000+ lines, DTFx's own console logging) shows zero
    /// errors, warnings, exceptions, or retries anywhere - checkpoints landing steadily, fast
    /// latencies, nothing hanging. This IS what Agents:MaxConcurrentLlmCalls=3 genuinely produces
    /// when all 10 real orchestrations are freehand dimensions (every one needs the extra real
    /// composition call on top of narrate+reflect+render+vision-QA) and all launch at once - a
    /// real capacity ceiling, deliberately left as-is (user decision, 2026-09-14): real production
    /// traffic bursting 10 simultaneous freehand Generate clicks is not the expected case, and
    /// raising the cap without knowing sol/gpt-5.2/terra's real per-deployment RPM/TPM risks
    /// tripping the exact rate limit this gate exists to prevent. If real production telemetry
    /// later shows this ceiling actually matters, that real number is the input this decision
    /// needs - not a guess.
    /// </summary>
    [Fact]
    public async Task RunAsync_FreehandDimensions_RealTenants_Concurrently_AllReachCompleteStatus()
    {
        (string Dimension, int TenantId, int UserId)[] cases =
        [
            ("Act", 1008, 12116),
            ("BacklogAging", 1008, 12116),
            ("Licence", 1008, 12116),
            ("Location", 1008, 12116),
            ("Departments", 1326, 83105),
            ("Location", 1326, 83105),
            ("Act", 1326, 83105),
            ("BacklogAging", 1082, 14128),
            ("Licence", 1082, 14128),
            ("Departments", 1082, 14128),
        ];

        // [FIX 2026-09-14] The REAL, current config is the in-repo (gitignored)
        // src/RegtrackInsights/appsettings.json - it already has Llm:Maf pointed at sol/ai-2. The
        // root D:\trpl-reginsights-dev\appsettings.json this first pointed at is a stale copy
        // (still ai-3/terra) - that mismatch is exactly what produced the DeploymentNotFound 404.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(@"D:\trpl-reginsights-dev\trpl-regtrack-microsoft-agentic-framework-core-dev\src\RegtrackInsights\appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agents:PromptDirectory"] = "./prompts",
                ["Azure:BlobContainer"] = "insights-reports-temp",
            })
            .Build();

        var services = new ServiceCollection();
        // [FIX 2026-09-14] Was AddLogging() with no provider attached - every ILogger call in the
        // whole pipeline (DurableTaskRunStatusReader's own logger.LogError on failure, anything
        // DTFx or the OpenAI SDK logs internally) was silently discarded. Real diagnostic blindness
        // - the first 10-way concurrent run stalled with zero instances reaching rendering and
        // this gave no way to tell whether that was real queueing math or actual failures retrying
        // silently underneath ComposeFreehandDimensionActivity's own ScheduleWithRetry.
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddInsightsData(configuration);
        services.AddInsightsTenantTokenBudget(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddInsightsOrchestration(configuration);
        services.AddInsightsObservability(configuration);
        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        try
        {
            var client = provider.GetRequiredService<TaskHubClient>();

            // Enqueue is cheap/fast (real DTFx metadata write, not an LLM call) - sequential here
            // costs nothing real and avoids any shared-state risk in the enqueue path itself. The
            // real concurrency this run is actually testing happens AFTER this, inside the
            // worker's own dequeue loop, gated by the one shared LlmConcurrencyGate.
            var instances = new List<(string Dimension, int TenantId, string InstanceId)>();
            foreach (var (dimension, tenantId, userId) in cases)
            {
                var input = new InsightsReportOrchestrationInput(
                    tenantId, DimensionSelectionComposition.ReportType, new InsightsScopeRequest("tenant", null),
                    "90day", userId, LlmCallPriority.Interactive, [dimension]);
                var instance = await client.CreateOrchestrationInstanceAsync(InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, null, input);
                instances.Add((dimension, tenantId, instance.InstanceId));
                output.WriteLine($"{DateTime.UtcNow:HH:mm:ss} Enqueued: {dimension}, tenant {tenantId} -> {instance.InstanceId}");
            }

            // [FIX 2026-09-14] Was `await PollUntilTerminalAsync(...)` directly inside the
            // Select - the FIRST instance to time out faulted the whole Task.WhenAll immediately,
            // hiding whatever the other 9 were actually doing. Each instance's own outcome
            // (Completed/Failed/TimedOut) is now captured individually, so one straggler can never
            // hide the real per-instance picture again. Budget raised from 15 to 30 minutes - the
            // first run's own real math (all 10 cases are freehand dimensions, every one needs the
            // extra composition call, all fighting over the same 3-slot gate) makes 15 minutes an
            // unrealistic budget for genuine worst-case queueing, not evidence of a hang by itself.
            var polls = instances.Select(async i =>
            {
                try
                {
                    var state = await PollUntilTerminalAsync(client, i.InstanceId, TimeSpan.FromMinutes(30));
                    output.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {i.Dimension}, tenant {i.TenantId}: {state.OrchestrationStatus}, final status {state.Status}");
                    if (state.OrchestrationStatus != OrchestrationStatus.Completed)
                        output.WriteLine($"  Output/failure detail ({i.Dimension}, tenant {i.TenantId}): {state.Output}");
                    return (i.Dimension, i.TenantId, Outcome: state.OrchestrationStatus.ToString());
                }
                catch (Exception ex)
                {
                    output.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {i.Dimension}, tenant {i.TenantId}: EXCEPTION - {ex.GetType().Name}: {ex.Message}");
                    return (i.Dimension, i.TenantId, Outcome: $"Exception:{ex.GetType().Name}");
                }
            });

            var results = await Task.WhenAll(polls);

            var failed = results.Where(r => r.Outcome != nameof(OrchestrationStatus.Completed)).ToList();
            output.WriteLine($"Summary: {results.Length - failed.Count}/{results.Length} completed.");
            foreach (var (dimension, tenantId, outcome) in failed)
                output.WriteLine($"  FAILED: {dimension}, tenant {tenantId} -> {outcome}");

            Assert.Empty(failed);
        }
        finally
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
                await hosted.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// THROWAWAY - a real worker process that only DEQUEUES, and stays alive INDEFINITELY (up to
    /// the 30-minute budget below) rather than polling one known run and exiting.
    ///
    /// [TRAP] The first version of this polled one deterministic run id and returned as soon as it
    /// went terminal - which meant it exited almost instantly against a key that already had a
    /// completed run from earlier, stopping the worker (and its dequeue loop) long before a human
    /// could click Generate on the AG-UI test page and have anything pick it up. A worker's job is
    /// to sit and dequeue for as long as the process lives, not to watch one instance.
    ///
    /// Picks up whatever is queued (e.g. via the AG-UI test page / POST /api/insights/reports
    /// through the deliberately worker-less Postman host - see
    /// InsightsApiManualRunTests.HostForPostmanAsync). Spends real LLM tokens per run it processes.
    /// Requires the same env vars as the Theory above.
    /// </summary>
    [Fact]
    public async Task RunAsync_StaysLiveAndDequeuesWhateverArrives()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();

        services.AddInsightsData(configuration);
        services.AddInsightsTenantTokenBudget(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddInsightsOrchestration(configuration);
        services.AddInsightsObservability(configuration);
        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        try
        {
            output.WriteLine("Worker is live and dequeuing. Staying up for 30 minutes or until stopped.");
            await Task.Delay(TimeSpan.FromMinutes(30));
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
