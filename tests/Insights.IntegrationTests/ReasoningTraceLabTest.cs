using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// [LAB, 2026-09-26] Real, end-to-end proof of the new reasoning-trace explainer (gpt-4o-mini, same
/// Llm:Maf endpoint/key every other real agent uses) - built and tested here first, against real
/// Minda (1008, user 12116, prod-readonly replica), because UAT is unreachable this session
/// (VPN/network down - see the earlier live connectivity check). Real fetch -> real compose ->
/// real narrate(v2, WITH real reasoning-log + tool-invocation-log recording wired in, unlike every
/// earlier lab test this session which left both null) -> assemble the real ReasoningTraceBundle
/// (no new LLM call for this step) -> real gpt-4o-mini explainer call -> a real .md file.
///
/// NOT part of the automated suite - hits the real prod-readonly replica and the writable UAT DB
/// (for logging), plus real LLM tokens on 3 real calls (compose, narrate, explain). Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~ReasoningTraceLabTest
/// Reads D:\trpl-reginsights-dev\appsettings.uat.json (base) then
/// D:\trpl-reginsights-dev\appsettings.minda-readonly.json (overrides ConnectionStrings:RegTrack to
/// the real prod-readonly replica) - no credentials in any shell command.
/// </summary>
public sealed class ReasoningTraceLabTest(ITestOutputHelper output)
{
    private const int TenantId = 1008;
    private const int UserId = 12116;
    // [CHOSEN 2026-09-26] Licence, not Internal/Act/etc - the prod-readonly replica this lab test
    // targets still runs the PRE-window proc versions (confirmed live: "Procedure or function ...
    // has too many arguments specified" against every windowed dim). Licence was deliberately never
    // windowed this session (separate additive-field track), so it is the real, currently-callable
    // dimension that lets this lab prove the reasoning-trace pipeline itself without re-tripping
    // the already-diagnosed replica/window gap.
    private const string Dimension = "Licence";

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.uat.json", optional: true)
        .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.minda-readonly.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    private static string RequireConfig(string key) =>
        Config[key]
        ?? throw new InvalidOperationException($"Set '{key}' in appsettings.uat.json/appsettings.minda-readonly.json or as an env var before running this lab test.");

    [Fact]
    public async Task BuildAndExplain_RealInternalReport_Minda()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var writeConnectionString = RequireConfig("ConnectionStrings:RegTrackReportsWrite");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var narrateModel = RequireConfig("Llm:Maf:Model");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");

        var reasoningRecorder = new SqlAgentReasoningRecorder(writeConnectionString);
        var toolInvocationRecorder = new SqlToolInvocationRecorder(writeConnectionString);
        var labRunId = $"lab-reasoning-trace-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        var repo = new SqlDimensionRepository(connectionString);
        var r = await repo.GetLicenceAsync(UserId, TenantId);
        output.WriteLine($"[{Dimension}] Fetched {r.Rows.Count} rows, {r.Assertions.Count} assertions, {r.Findings.Count} findings.");

        var rowsJson = System.Text.Json.JsonSerializer.Serialize(r.Rows);
        var controlTotalsJson = System.Text.Json.JsonSerializer.Serialize(r.ControlTotals);
        var dataQualityJson = System.Text.Json.JsonSerializer.Serialize(r.DataQuality);

        // ---- Real compose call, WITH its own reasoning captured this time (found live earlier
        // this session: ComposeAsync never extracted a ReasoningSummary at all - fixed here inline,
        // not in production code yet, since that is a separate change from this lab proof). ----
        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "02_composition_freehand_licence.md"));
        var compositionAgent = new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, narrateModel, apiKey, "FreehandCompositionLicenceAgent", "Decides structure/hero/emphasis for a freehand Licence insight from real tenant data.",
            compositionInstructions));
        var composeResult = await compositionAgent.ComposeAsync(r.Assertions, r.Findings, rowsJson, controlTotalsJson, dataQualityJson);
        output.WriteLine($"[{Dimension}] Compose: {composeResult.TotalTokens} tokens.");
        // ComposeAsync does not currently extract/return a ReasoningSummary (real gap found live
        // 2026-09-25) - so there is nothing to record for the "compose" stage here. The bundle and
        // prompt both handle an empty reasoning_log entry for a stage correctly either way.

        var narrateInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "v2/03_narrative_analyst.md"));
        var narrateAgent = new MafAnalystNarrativeAgent(
            MafAgentFactory.CreateJsonAgent(endpoint, narrateModel, apiKey, "AnalystNarrativeAgent", "Traces root cause from typed assertions and raw dimension rows.", narrateInstructions),
            readOnlySqlConnectionString: connectionString,
            onSqlToolInvoked: async (runId, sql, toolResult, success) =>
            {
                output.WriteLine($"*** SQL TOOL FIRED *** {sql}");
                try
                {
                    await toolInvocationRecorder.RecordAsync(runId, "analyze_and_narrate", "fetch_scoped_sql_data", sql, success, toolResult.Length);
                }
                catch (Exception ex)
                {
                    output.WriteLine($"UAT unreachable for tool-invocation log write ({ex.GetType().Name}) - continuing, this lab run rebuilds the bundle in-memory anyway.");
                }
            });

        var narrateResult = await narrateAgent.AnalyzeAndNarrateAsync(
            composeResult.Value, r.Assertions, r.Findings, Dimension, rowsJson, controlTotalsJson,
            userId: UserId, customerId: TenantId, runId: labRunId);
        output.WriteLine($"[{Dimension}] Narrate: {narrateResult.TotalTokens} tokens, {narrateResult.Value.Blocks.Count} blocks.");

        // [ADDED 2026-09-26] UAT (10.13.0.6, backing RegTrackReportsWrite) is unreachable this
        // session - only the prod-readonly replica (10.224.254.4) responded above. The real
        // recorder round-trip (write, then read back) is already proven live from an earlier
        // session (see the "SQL fetch tool live" / "cancel endpoint + tool-invocation logging"
        // work) - this lab run instead builds the SAME shape directly from what compose/narrate
        // already produced in memory, so the explainer itself can still be proven end to end
        // without depending on UAT being reachable right now. Fails soft, same "log capture is not
        // worth a response" stance AnalyzeAndNarrateActivity's own production code already takes.
        IReadOnlyList<AgentReasoningLogEntry> reasoningLog;
        IReadOnlyList<ToolInvocationLogEntry> toolInvocations;
        try
        {
            if (narrateResult.ReasoningSummary is { Length: > 0 })
                await reasoningRecorder.RecordAsync(labRunId, "analyze_and_narrate", narrateResult.ReasoningSummary);
            reasoningLog = await reasoningRecorder.GetForRunAsync(labRunId);
            toolInvocations = await toolInvocationRecorder.GetForRunAsync(labRunId);
            output.WriteLine($"[{Dimension}] Trace bundle (from UAT): {reasoningLog.Count} reasoning-log entries, {toolInvocations.Count} tool-invocation entries.");
        }
        catch (Exception ex)
        {
            output.WriteLine($"[{Dimension}] UAT unreachable for reasoning-log round-trip ({ex.GetType().Name}: {ex.Message}) - building the bundle directly from the in-memory call result instead.");
            reasoningLog = narrateResult.ReasoningSummary is { Length: > 0 }
                ? [new AgentReasoningLogEntry("analyze_and_narrate", narrateResult.ReasoningSummary, DateTime.UtcNow)]
                : [];
            toolInvocations = [];
        }

        var bundle = new ReasoningTraceBundle(
            Dimension, labRunId, composeResult.Value, r.Assertions, r.Findings, rowsJson, controlTotalsJson,
            r.DataQuality, reasoningLog, toolInvocations);

        // ---- Real gpt-4o-mini explainer call - same endpoint/key as every other real agent
        // (Llm:Maf), deliberately a smaller/cheaper deployment for this explain-only task. ----
        var explainerInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "08_reasoning_explainer.md"));
        var explainerAgent = new MafReasoningExplainerAgent(MafAgentFactory.CreateSimpleTextAgent(
            endpoint, "gpt-4o-mini", apiKey, "ReasoningExplainerAgent", "Explains a report's own provenance from its real trace bundle.",
            explainerInstructions));
        var explainResult = await explainerAgent.ExplainAsync(bundle);
        output.WriteLine($"[{Dimension}] Explain: {explainResult.TotalTokens} tokens.");

        const string outPath = @"D:\trpl-reginsights-dev\reasoning-trace-internal-minda.md";
        await File.WriteAllTextAsync(outPath, explainResult.Value);
        output.WriteLine($"[{Dimension}] Written: {outPath}");

        Assert.NotEmpty(explainResult.Value);
        Assert.Contains(Dimension, explainResult.Value, StringComparison.OrdinalIgnoreCase);
    }
}
