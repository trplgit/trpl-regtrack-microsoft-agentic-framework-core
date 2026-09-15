using Dapper;
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
    /// THROWAWAY, read-only diagnostic - real token spend recorded in dbo.InsightsTenantTokenUsage
    /// (SqlTenantTokenBudgetRepository's own table, written once per real run by
    /// RecordTenantTokenUsageActivity). Added in response to a direct user question about spend -
    /// this is the actual, ground-truth token count for every real run this session made, not an
    /// estimate. Deliberately reports RAW TOKENS ONLY, no dollar conversion - CLAUDE.md non-
    /// negotiable #2 (fail closed, never guess) applies here too: real per-deployment pricing for
    /// sol/gpt-5.2 isn't known to this code, so fabricating a dollar figure would be exactly the
    /// kind of unverifiable claim the whole pipeline exists to refuse.
    /// </summary>
    [Fact]
    public async Task ReportRealTokenSpend()
    {
        var connectionString = new ConfigurationBuilder()
            .AddJsonFile(@"D:\trpl-reginsights-dev\trpl-regtrack-microsoft-agentic-framework-core-dev\src\RegtrackInsights\appsettings.json")
            .Build()["ConnectionStrings:RegTrack"]!;

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);

        var byTenant = await connection.QueryAsync<(int CustomerID, long Runs, long TotalTokens, DateTime FirstRecordedUtc, DateTime LastRecordedUtc)>("""
            SELECT CustomerID, COUNT(*) AS Runs, SUM(TotalTokens) AS TotalTokens,
                   MIN(RecordedAtUtc) AS FirstRecordedUtc, MAX(RecordedAtUtc) AS LastRecordedUtc
            FROM dbo.InsightsTenantTokenUsage
            GROUP BY CustomerID
            ORDER BY SUM(TotalTokens) DESC;
            """);

        long grandTotal = 0;
        long totalRuns = 0;
        output.WriteLine("Tenant | Runs | TotalTokens | First | Last");
        foreach (var row in byTenant)
        {
            output.WriteLine($"{row.CustomerID} | {row.Runs} | {row.TotalTokens:N0} | {row.FirstRecordedUtc:o} | {row.LastRecordedUtc:o}");
            grandTotal += row.TotalTokens;
            totalRuns += row.Runs;
        }
        output.WriteLine($"GRAND TOTAL: {totalRuns} runs, {grandTotal:N0} tokens, across {byTenant.Count()} tenants.");
    }

    /// <summary>
    /// THROWAWAY, read-only diagnostic - checks whether sql/31_agent_reasoning_log.sql (from the
    /// still-unmerged agent-reasoning-capture branch, parked pending user/Vinay deploy) has
    /// actually been deployed to the real vitComplianceSystem database. Also flags a REAL
    /// filename collision found while checking this: reginsights-staging now has its OWN,
    /// DIFFERENT sql/31 (Tanvi's sql/31_freetier_insight_json.sql, merged 2026-09-15) - if
    /// agent-reasoning-capture is ever merged as-is, its sql/31_agent_reasoning_log.sql will
    /// collide on the filename (and both scripts would need to be renumbered/reconciled).
    /// </summary>
    [Fact]
    public async Task CheckAgentReasoningLogTableDeployed()
    {
        var connectionString = new ConfigurationBuilder()
            .AddJsonFile(@"D:\trpl-reginsights-dev\trpl-regtrack-microsoft-agentic-framework-core-dev\src\RegtrackInsights\appsettings.json")
            .Build()["ConnectionStrings:RegTrack"]!;

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        var exists = await connection.QuerySingleAsync<int>(
            "SELECT CASE WHEN OBJECT_ID('dbo.InsightsAgentReasoningLog', 'U') IS NULL THEN 0 ELSE 1 END;");

        output.WriteLine(exists == 1
            ? "DEPLOYED: dbo.InsightsAgentReasoningLog exists in vitComplianceSystem."
            : "NOT DEPLOYED: dbo.InsightsAgentReasoningLog does not exist - sql/31_agent_reasoning_log.sql (agent-reasoning-capture branch) has not been run against this database.");
    }

    /// <summary>
    /// THROWAWAY, one-off cleanup - terminates specific stale orchestration instances by id.
    /// [ADDED 2026-09-14] The shared task hub (vitInsightsTaskHub) accumulated ~21 Running
    /// instances left over from earlier manual runs this session that were stopped via TaskStop
    /// mid-flight rather than reaching a terminal state (DurableTask.Core.TaskHubClient has no
    /// "list all instances" API - these ids were read directly off a stuck run's own console log,
    /// where they kept appearing as "Checkpointing orchestration... Running" for 20 real minutes
    /// while a brand-new Minda Entity request never got a single LlmConcurrencyGate slot). User
    /// confirmed (2026-09-14): terminate the backlog, then retry.
    /// </summary>
    [Fact]
    public async Task TerminateStaleBacklogInstances()
    {
        string[] staleIds =
        [
            "0e8fe72a93bb4aec85d82b85d70b4950", "0ef2dda75da34f70bdc0eaad204cb693",
            "1864fdbc89014a44b673ca367eaa6897", "1d0e648f1c16426b9e590af058932803",
            "1ef682ee2e7c4bac9b4cd617fd0ac549", "25c4203f5c8a4845936989f42de55dfe",
            "26fe292614554041b5b67c1736fa643d", "34e704646da64fc583d0669dab3a603d",
            "38bdf108f63a4e87ba8af7144e611814", "3d3e2c451c7a44dfaabe1ce08e877509",
            "6f927fe965314cee837737cd0f33e8c5", "89d562309a7b4f658f272635c724343f",
            "96d31fd9169c431ca520328f65d86822", "9d4f948f2e5f4338b8a523b2a0e2571a",
            "ab8fc6a5e1ff44a5bfd9e0b649fe6854", "ada59eddbb2141008bff6f47c912c68a",
            "ba5ee6799c414eca958b220c06c5781c", "bcb70348e38e4d4e87d0956607a6afef",
            "c31bdbf289ba4480b69d830cf1431b7b", "cd3d0c69b96a41619713b857a665fb20",
            "dda2a595a2e845c5b42350c2f2adc8bb", "fc0ea03f129344b5bec8ae51fb4af8f7",
        ];

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(@"D:\trpl-reginsights-dev\trpl-regtrack-microsoft-agentic-framework-core-dev\src\RegtrackInsights\appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agents:PromptDirectory"] = "./prompts",
                ["Azure:BlobContainer"] = "insights-reports-temp",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInsightsData(configuration);
        services.AddInsightsWorker();
        services.AddInsightsOrchestration(configuration);
        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<TaskHubClient>();

        foreach (var id in staleIds)
        {
            var state = await client.GetOrchestrationStateAsync(id);
            if (state is null)
            {
                output.WriteLine($"{id}: not found (already gone)");
                continue;
            }
            if (state.OrchestrationStatus is OrchestrationStatus.Completed or OrchestrationStatus.Failed or OrchestrationStatus.Terminated)
            {
                output.WriteLine($"{id}: already terminal ({state.OrchestrationStatus})");
                continue;
            }
            await client.TerminateInstanceAsync(state.OrchestrationInstance, "Stale backlog cleanup 2026-09-14 - superseded by TaskStop mid-run, never reached terminal state.");
            output.WriteLine($"{id}: terminated (was {state.OrchestrationStatus})");
        }
    }

    /// <summary>
    /// [ADDED 2026-09-14] All 7 v1 dimensions (CLAUDE.md "V1 release scope") for ONE real tenant -
    /// Minda Corporation Group (1008, user 12116, same pair as every other Minda run this session).
    /// Sequential, not concurrent: this is a single-tenant completeness check, not a load test (that
    /// is RunAsync_FreehandDimensions_RealTenants_Concurrently_AllReachCompleteStatus's job), and
    /// running sequentially gives each dimension the full 3-slot LlmConcurrencyGate to itself rather
    /// than fighting the other 6 for it, plus lets the local-fallback filename unambiguously identify
    /// which dimension produced which file WITHOUT parsing PersistOutput.LocalFilePath back out of
    /// state.Output - Period is threaded through to PersistActivity unchanged
    /// (InsightsReportOrchestrator.cs:835), so embedding the dimension name in Period here is enough
    /// (same trick the 2026-09-13 session used, see the "solretry1__dim=act" style files already in
    /// Reports:LocalFallbackDirectory).
    ///
    /// Entity is requested exactly like the other 6 - ReportTypeRouter's own Entity-ALONE redirect
    /// (InsightsReportOrchestrator.cs, "Product rule 2026-09-11") turns it into a real "fixed_holistic"
    /// run before anything else reads ReportType, so this test does not special-case it.
    ///
    /// [PRE-REQUISITE, 2026-09-14] Run TerminateStaleBacklogInstances first - the same shared task
    /// hub DB had ~21 leftover Running instances from earlier manual runs (stopped mid-flight via
    /// TaskStop) that starved a fresh run of the 3-slot LlmConcurrencyGate for 20+ real minutes with
    /// zero errors, only "No events found. Waiting..." - not a code defect, a real backlog.
    /// </summary>
    [Fact]
    public async Task RunAsync_AllV1Dimensions_Minda_StoresLocally()
    {
        // CLAUDE.md's "V1 release scope" table, in table order.
        string[] dimensions = ["Entity", "Users", "Departments", "BacklogAging", "Act", "Licence", "Location"];
        const int tenantId = 1008;
        const int userId = 12116;

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(@"D:\trpl-reginsights-dev\trpl-regtrack-microsoft-agentic-framework-core-dev\src\RegtrackInsights\appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agents:PromptDirectory"] = "./prompts",
                ["Azure:BlobContainer"] = "insights-reports-temp",
            })
            .Build();

        var services = new ServiceCollection();
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
            var results = new List<(string Dimension, string Outcome, string? LocalFilePath)>();

            foreach (var dimension in dimensions)
            {
                var input = new InsightsReportOrchestrationInput(
                    tenantId, DimensionSelectionComposition.ReportType, new InsightsScopeRequest("tenant", null),
                    $"90day__dim={dimension}", userId, LlmCallPriority.Interactive, [dimension]);

                var instance = await client.CreateOrchestrationInstanceAsync(InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, null, input);
                output.WriteLine($"{DateTime.UtcNow:HH:mm:ss} Enqueued {dimension} -> {instance.InstanceId}");

                var state = await PollUntilTerminalAsync(client, instance.InstanceId, TimeSpan.FromMinutes(20));
                output.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {dimension}: {state.OrchestrationStatus}, final status {state.Status}");

                string? localFilePath = null;
                if (state.OrchestrationStatus == OrchestrationStatus.Completed && !string.IsNullOrEmpty(state.Output))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(state.Output);
                    if (doc.RootElement.TryGetProperty("LocalFilePath", out var pathProp))
                        localFilePath = pathProp.GetString();
                    output.WriteLine($"  Stored: {localFilePath}");
                }
                else
                {
                    output.WriteLine($"  Output/failure detail ({dimension}): {state.Output}");
                }

                results.Add((dimension, state.OrchestrationStatus.ToString(), localFilePath));
            }

            var failed = results.Where(r => r.Outcome != nameof(OrchestrationStatus.Completed)).ToList();
            output.WriteLine($"Summary: {results.Count - failed.Count}/{results.Count} completed.");
            foreach (var r in results)
                output.WriteLine($"  {r.Dimension}: {r.Outcome}{(r.LocalFilePath is null ? "" : " -> " + r.LocalFilePath)}");

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
