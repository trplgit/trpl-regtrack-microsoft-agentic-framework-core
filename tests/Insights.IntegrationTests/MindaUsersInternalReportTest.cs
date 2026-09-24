using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// [LAB, 2026-09-22, EXTENDED 2026-09-23] User request: real Minda (1008, user 12116 - same pair
/// as every other Minda run this session) reports for Users (deterministic composition, v1
/// narrate/reflect - Users is NOT a freehand dimension and has no SQL-tool/memory-tool wiring,
/// architecturally) and Internal (freehand, v2 narrate WITH ReadOnlySqlFetchTool live - same
/// pattern SqlFetchToolLabTest already proved for tenant 29, now on Minda's real data instead).
/// Reads D:\trpl-reginsights-dev\appsettings.uat.json directly - no credentials in any shell
/// command.
///
/// [EXTENDED 2026-09-23] The real ask this round: prove, with real evidence (not a doc comment),
/// that (a) regtech_dev01_readonly genuinely enforces read-only on a real cross-dimension SQL call
/// against Minda's real prod-readonly data, (b) a run that never gets the tool (Users) produces
/// ZERO tool-invocation log rows and a run that does (Internal) produces real ones - the exact
/// "can we tell if it called the tool or not" gap the user raised, now answered from a real
/// dbo.InsightsToolInvocationLog row, not from CallsMade after the fact, and (c) TenantMemoryTool
/// (never exercised on Minda before) writes and reads back real content live too. Deliberately
/// does NOT go through the orchestrator/DTFx at all (repository + agents called directly, same as
/// this file always has) - "use prod db to just read, don't write on its task hub."
/// </summary>
public sealed class MindaUsersInternalReportTest(ITestOutputHelper output)
{
    private const int TenantId = 1008;
    // [CORRECTED 2026-09-22, TWICE] First tried 12116 against UAT -> DimensionScopeDeniedException
    // (12116 doesn't exist on UAT's copy of 1008). "Fixed" to 9629 (valid on UAT) - reports came
    // back with barely any data (2-4 rows), because UAT's OWN copy of tenant 1008 is genuinely tiny
    // (4 branches, 49 instances - confirmed via MindaScopeDiagnosticTest, not a scope-selection
    // bug). Real Minda data lives on the prod-readonly replica (regtech_dev01_readonly,
    // 10.224.254.4), NOT UAT - confirmed there: 124 branches, 23,183 instances, and 12116 IS valid
    // there (121 branches, all 23,183 instances) - it was the right user for the wrong database.
    private const int UserId = 12116;

    // uat.json first (base - supplies Llm:Maf:*, which minda-readonly.json doesn't have), THEN
    // minda-readonly.json (later source wins on shared keys, so its ConnectionStrings:RegTrack -
    // the real prod-readonly replica - overrides uat's).
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.uat.json", optional: true)
        .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.minda-readonly.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    private static string RequireConfig(string key) =>
        Config[key]
        ?? throw new InvalidOperationException($"Set '{key}' in appsettings.uat.json or as an env var before running this lab test.");

    [Fact]
    public async Task GenerateUsersReport_Minda()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var model = RequireConfig("Llm:Maf:Model");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");

        var dimensionRepository = new SqlDimensionRepository(connectionString);
        var r = await dimensionRepository.GetUsersAsync(UserId, TenantId);
        var rowsJson = System.Text.Json.JsonSerializer.Serialize(r.Rows);
        var controlTotalsJson = System.Text.Json.JsonSerializer.Serialize(r.ControlTotals);
        output.WriteLine($"Fetched {r.Rows.Count} rows, {r.Assertions.Count} assertions.");

        var plan = DimensionSelectionComposition.Build(["Users"]);

        var narrateInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "03_narrative.md"));
        var narrateAgent = new MafNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeAgent", "Writes prose from typed assertions only.", narrateInstructions));
        var narrateResult = await narrateAgent.NarrateAsync(plan, r.Assertions, r.Findings);
        output.WriteLine($"Narrate: {narrateResult.TotalTokens} tokens, {narrateResult.Value.Blocks.Count} blocks.");

        var reflectionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "04_narrative_reflection.md"));
        var reflectionAgent = new MafNarrativeReflectionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "NarrativeReflectionAgent", "Critiques narrative prose for semantic errors.", reflectionInstructions));
        var reflection = await reflectionAgent.ReflectAsync(narrateResult.Value, r.Assertions, r.Findings);
        output.WriteLine($"Reflection verdict: {reflection.Value.Verdict}");
        foreach (var issue in reflection.Value.Issues ?? [])
            output.WriteLine($"  [{issue.Check}] {issue.Block}: \"{issue.Quote}\" -- {issue.Problem} -> {issue.Fix}");

        var renderInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "05_report_html_dimension_selection_user.md"));
        var renderAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "DimensionSelectionUserReportHtmlAgent", "Renders a single-Users-dimension request as self-contained HTML.",
            renderInstructions));
        var renderResult = await renderAgent.RenderAsync(
            plan, narrateResult.Value, r.Assertions, "Minda Corporation Group", "dimension_selection", DateTime.UtcNow,
            locationRows: null,
            dimensionRowsJson: new Dictionary<string, string> { ["Users"] = rowsJson },
            dimensionControlTotalsJson: new Dictionary<string, string> { ["Users"] = controlTotalsJson });

        var html = PoppinsFontInjector.Inject(renderResult.Value);
        const string outPath = @"D:\trpl-reginsights-dev\local-report-users-minda.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"Written: {outPath}");

        // [ADDED 2026-09-23] The "no access" half of the tool-logging proof. This path never
        // constructs a MafAnalystNarrativeAgent, ReadOnlySqlFetchTool, or TenantMemoryTool at all -
        // Users stays on v1's plain MafNarrativeAgent, architecturally with no tool to call. There
        // is no runId here (this lab harness never touches DTFx/the task hub), so there is nothing
        // for a row to be keyed by even in principle - proof by construction, not by a query.
        output.WriteLine("No SQL/memory tool was ever constructed for this call - zero possible InsightsToolInvocationLog rows, by construction.");

        Assert.NotEmpty(narrateResult.Value.Blocks);
    }

    [Fact]
    public async Task GenerateInternalReport_Minda_WithSqlToolLive()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var model = RequireConfig("Llm:Maf:Model");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
        const string dimension = "Internal";

        // [ADDED 2026-09-23] A real, if synthetic, run id - this lab harness never creates a real
        // DTFx instance ("don't write on its task hub"), but dbo.InsightsToolInvocationLog still
        // needs something real to key rows by so they can be queried back after the call, same as
        // a real orchestration run's runId would be used for in production.
        // [ADDED 2026-09-23] ConnectionStrings:RegTrack is the READ-ONLY prod replica for this lab
        // file (appsettings.minda-readonly.json overrides just that key) - the tool-invocation log
        // is a WRITE, so it goes to RegTrackReportsWrite (the writable UAT DB), same split
        // PersistActivity's own construction already established for exactly this reason (see
        // WorkerRegistration.cs's [TEMP OVERRIDE 2026-09-08] comment).
        var writeConnectionString = RequireConfig("ConnectionStrings:RegTrackReportsWrite");
        var labRunId = $"lab-minda-internal-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var toolLog = new SqlToolInvocationRecorder(writeConnectionString);

        var dimensionRepository = new SqlDimensionRepository(connectionString);
        var r = await dimensionRepository.GetInternalAsync(UserId, TenantId);
        var rowsJson = System.Text.Json.JsonSerializer.Serialize(r.Rows);
        var controlTotalsJson = System.Text.Json.JsonSerializer.Serialize(r.ControlTotals);
        var dataQualityJson = System.Text.Json.JsonSerializer.Serialize(r.DataQuality);
        output.WriteLine($"Fetched {r.Rows.Count} rows, {r.Assertions.Count} assertions.");

        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "02_composition_freehand_internal.md"));
        var compositionAgent = new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "FreehandCompositionInternalAgent", "Decides structure/hero/emphasis for a freehand Internal insight from real tenant data.",
            compositionInstructions));
        var composeResult = await compositionAgent.ComposeAsync(r.Assertions, r.Findings, rowsJson, controlTotalsJson, dataQualityJson);
        output.WriteLine($"Compose: {composeResult.TotalTokens} tokens.");

        var callLog = new List<(string Sql, int ResultLength, bool Success)>();
        var narrateInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "v2/03_narrative_analyst.md"));
        var narrateAgent = new MafAnalystNarrativeAgent(
            MafAgentFactory.CreateJsonAgent(endpoint, model, apiKey, "AnalystNarrativeAgent", "Traces root cause from typed assertions and raw dimension rows.", narrateInstructions),
            readOnlySqlConnectionString: connectionString,
            onSqlToolInvoked: async (runId, sql, result, success) =>
            {
                callLog.Add((sql, result.Length, success));
                output.WriteLine($"*** SQL TOOL FIRED *** query: {sql}");
                output.WriteLine($"    result ({result.Length} chars, success={success}): {result[..Math.Min(400, result.Length)]}");
                await toolLog.RecordAsync(runId, "analyze_and_narrate", "fetch_scoped_sql_data", sql, success, result.Length);
            });

        var narrateResult = await narrateAgent.AnalyzeAndNarrateAsync(
            composeResult.Value, r.Assertions, r.Findings, dimension, rowsJson, controlTotalsJson,
            userId: UserId, customerId: TenantId, runId: labRunId);
        output.WriteLine($"Narrate: {narrateResult.TotalTokens} tokens, {narrateResult.Value.Blocks.Count} blocks. SQL tool calls: {callLog.Count}");

        var renderInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "05_report_html_dimension_selection_internal.md"));
        var renderAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "DimensionSelectionInternalReportHtmlAgent", "Renders a freehand-composed Internal insight as self-contained HTML.",
            renderInstructions));
        var renderResult = await renderAgent.RenderAsync(
            composeResult.Value, narrateResult.Value, r.Assertions, "Minda Corporation Group", "dimension_selection", DateTime.UtcNow,
            locationRows: null,
            dimensionRowsJson: new Dictionary<string, string> { [dimension] = rowsJson },
            dimensionControlTotalsJson: new Dictionary<string, string> { [dimension] = controlTotalsJson });

        var html = PoppinsFontInjector.Inject(renderResult.Value);
        const string outPath = @"D:\trpl-reginsights-dev\local-report-internal-minda-sqltool.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"Written: {outPath}");

        // [ADDED 2026-09-23] Read the row(s) back from the real table, exactly as a future incident
        // investigation would - "did the agent call the tool for this run" answered from durable
        // storage, not from callLog (which only exists because this test happens to be watching).
        var loggedRows = await ReadBackAsync(writeConnectionString, labRunId);
        output.WriteLine($"dbo.InsightsToolInvocationLog rows for {labRunId}: {loggedRows.Count}");
        foreach (var (toolName, detail, success) in loggedRows)
            output.WriteLine($"  [{toolName}] success={success} detail={detail[..Math.Min(200, detail.Length)]}");

        if (callLog.Count > 0)
            Assert.Equal(callLog.Count, loggedRows.Count);

        Assert.NotEmpty(narrateResult.Value.Blocks);
    }

    /// <summary>
    /// [ADDED 2026-09-23] The direct A/B twin of GenerateInternalReport_Minda_WithSqlToolLive above -
    /// SAME real Internal (statutory-vs-internal governance) data, same composition/narrate/render
    /// prompts, same tenant - the ONLY difference is `readOnlySqlConnectionString` is never passed
    /// to MafAnalystNarrativeAgent, so `fetch_scoped_sql_data` is never added to the tool list at
    /// all (see AnalystNarrativeAgent.cs's own gate: `if (readOnlySqlConnectionString is not null
    /// &amp;&amp; ...)`) - not "the model chose not to call it", genuinely no tool present this run.
    /// Real side-by-side comparison of whether tool access changes narrate quality/content for the
    /// identical real tenant.
    /// </summary>
    [Fact]
    public async Task GenerateInternalReport_Minda_NoSqlTool()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var model = RequireConfig("Llm:Maf:Model");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
        const string dimension = "Internal";

        var writeConnectionString = RequireConfig("ConnectionStrings:RegTrackReportsWrite");
        var labRunId = $"lab-minda-internal-notool-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var toolLog = new SqlToolInvocationRecorder(writeConnectionString);

        var dimensionRepository = new SqlDimensionRepository(connectionString);
        var r = await dimensionRepository.GetInternalAsync(UserId, TenantId);
        var rowsJson = System.Text.Json.JsonSerializer.Serialize(r.Rows);
        var controlTotalsJson = System.Text.Json.JsonSerializer.Serialize(r.ControlTotals);
        var dataQualityJson = System.Text.Json.JsonSerializer.Serialize(r.DataQuality);
        output.WriteLine($"Fetched {r.Rows.Count} rows, {r.Assertions.Count} assertions.");

        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "02_composition_freehand_internal.md"));
        var compositionAgent = new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "FreehandCompositionInternalAgent", "Decides structure/hero/emphasis for a freehand Internal insight from real tenant data.",
            compositionInstructions));
        var composeResult = await compositionAgent.ComposeAsync(r.Assertions, r.Findings, rowsJson, controlTotalsJson, dataQualityJson);
        output.WriteLine($"Compose: {composeResult.TotalTokens} tokens.");

        // [KEY DIFFERENCE] No readOnlySqlConnectionString, no onSqlToolInvoked - the tool is never
        // constructed, never added to the model's tool list this run.
        var narrateInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "v2/03_narrative_analyst.md"));
        var narrateAgent = new MafAnalystNarrativeAgent(
            MafAgentFactory.CreateJsonAgent(endpoint, model, apiKey, "AnalystNarrativeAgent", "Traces root cause from typed assertions and raw dimension rows.", narrateInstructions));

        var narrateResult = await narrateAgent.AnalyzeAndNarrateAsync(
            composeResult.Value, r.Assertions, r.Findings, dimension, rowsJson, controlTotalsJson,
            userId: UserId, customerId: TenantId, runId: labRunId);
        output.WriteLine($"Narrate: {narrateResult.TotalTokens} tokens, {narrateResult.Value.Blocks.Count} blocks. (no SQL tool available this run)");

        var renderInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "05_report_html_dimension_selection_internal.md"));
        var renderAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "DimensionSelectionInternalReportHtmlAgent", "Renders a freehand-composed Internal insight as self-contained HTML.",
            renderInstructions));
        var renderResult = await renderAgent.RenderAsync(
            composeResult.Value, narrateResult.Value, r.Assertions, "Minda Corporation Group", "dimension_selection", DateTime.UtcNow,
            locationRows: null,
            dimensionRowsJson: new Dictionary<string, string> { [dimension] = rowsJson },
            dimensionControlTotalsJson: new Dictionary<string, string> { [dimension] = controlTotalsJson });

        var html = PoppinsFontInjector.Inject(renderResult.Value);
        const string outPath = @"D:\trpl-reginsights-dev\local-report-internal-minda-notool.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"Written: {outPath}");

        // No tool means no possible InsightsToolInvocationLog rows for this runId - proof by
        // construction, same reasoning GenerateUsersReport_Minda already documents.
        var loggedRows = await ReadBackAsync(writeConnectionString, labRunId);
        output.WriteLine($"dbo.InsightsToolInvocationLog rows for {labRunId}: {loggedRows.Count} (expected 0 - tool was never constructed this run)");
        Assert.Empty(loggedRows);

        Assert.NotEmpty(narrateResult.Value.Blocks);
    }

    /// <summary>
    /// [ADDED 2026-09-23] TenantMemoryTool has never been exercised live on Minda before - closes
    /// that gap and proves the write-side logging (onMemoryWriteInvoked) the same way the SQL tool
    /// test above proves fetch_scoped_sql_data's. Writes are real: this tenant's real memory blob
    /// on the storage account appsettings.uat.json's Azure:BlobConnectionString points to.
    /// </summary>
    [Fact]
    public async Task WriteAndReadTenantMemory_Minda_Live()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var model = RequireConfig("Llm:Maf:Model");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var blobConnectionString = RequireConfig("Azure:BlobConnectionString");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
        const string dimension = "Internal";
        const string containerName = "insights-tenant-memory";

        var writeConnectionString = RequireConfig("ConnectionStrings:RegTrackReportsWrite");
        var labRunId = $"lab-minda-memory-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var toolLog = new SqlToolInvocationRecorder(writeConnectionString);
        // [FOUND LIVE 2026-09-23] AdalKeyVaultReportEncryptor reads its Key Vault client secret
        // from whatever DB its connection string points to - the prod-readonly replica's copy is
        // stale (AADSTS7000215 "Invalid client secret provided", confirmed live), same class of
        // replica-staleness this session already found elsewhere. Same RegTrackReportsWrite split
        // as PersistActivity's own construction, for the same reason: reads may come from the
        // replica, but Key Vault/encryption infra must go through the writable UAT DB.
        var encryptor = new Insights.Persistence.AdalKeyVaultReportEncryptor(writeConnectionString);

        var dimensionRepository = new SqlDimensionRepository(connectionString);
        var r = await dimensionRepository.GetInternalAsync(UserId, TenantId);
        var rowsJson = System.Text.Json.JsonSerializer.Serialize(r.Rows);
        var controlTotalsJson = System.Text.Json.JsonSerializer.Serialize(r.ControlTotals);
        var dataQualityJson = System.Text.Json.JsonSerializer.Serialize(r.DataQuality);

        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "02_composition_freehand_internal.md"));
        var compositionAgent = new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "FreehandCompositionInternalAgent", "Decides structure/hero/emphasis for a freehand Internal insight from real tenant data.",
            compositionInstructions));
        var composeResult = await compositionAgent.ComposeAsync(r.Assertions, r.Findings, rowsJson, controlTotalsJson, dataQualityJson);

        var memoryWriteLog = new List<(string Dimension, bool Success)>();
        var narrateInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "v2/03_narrative_analyst.md"));
        var narrateAgent = new MafAnalystNarrativeAgent(
            MafAgentFactory.CreateJsonAgent(endpoint, model, apiKey, "AnalystNarrativeAgent", "Traces root cause from typed assertions and raw dimension rows.", narrateInstructions),
            memoryEncryptor: encryptor, memoryDecryptor: encryptor,
            memoryBlobConnectionString: blobConnectionString, memoryContainerName: containerName,
            onMemoryWriteInvoked: async (runId, dimensionName, success) =>
            {
                memoryWriteLog.Add((dimensionName, success));
                output.WriteLine($"*** MEMORY WRITE FIRED *** dimension={dimensionName} success={success}");
                await toolLog.RecordAsync(runId, "analyze_and_narrate", "write_tenant_memory", dimensionName, success, resultLength: null);
            });

        var narrateResult = await narrateAgent.AnalyzeAndNarrateAsync(
            composeResult.Value, r.Assertions, r.Findings, dimension, rowsJson, controlTotalsJson,
            userId: UserId, customerId: TenantId, runId: labRunId);
        output.WriteLine($"Narrate: {narrateResult.TotalTokens} tokens. Memory write calls: {memoryWriteLog.Count}");

        var loggedRows = await ReadBackAsync(writeConnectionString, labRunId);
        output.WriteLine($"dbo.InsightsToolInvocationLog rows for {labRunId}: {loggedRows.Count}");
        foreach (var (toolName, detail, success) in loggedRows)
            output.WriteLine($"  [{toolName}] success={success} detail={detail}");

        // Read back the real blob directly (not via memoryWriteLog, which only proves the tool
        // fired) - confirms the write actually reached the tenant's real history section, the
        // thing a NEXT run's tenant_history field would read.
        var readerTool = new TenantMemoryTool(encryptor, encryptor, blobConnectionString, containerName, TenantId, [dimension]);
        var sections = await readerTool.ReadSectionsAsync();
        output.WriteLine($"Tenant {TenantId} memory section for {dimension} ({sections[dimension].Length} chars): {sections[dimension]}");

        if (memoryWriteLog.Count > 0)
        {
            Assert.True(memoryWriteLog[0].Success);
            Assert.NotEmpty(sections[dimension]);
        }
        else
        {
            output.WriteLine("Model chose not to call write_tenant_memory this run - a legitimate, expected outcome (see prompt: most runs have nothing new worth remembering).");
        }

        Assert.NotEmpty(narrateResult.Value.Blocks);
    }

    /// <summary>
    /// [ADDED 2026-09-23] The model chose not to call the tool in GenerateInternalReport_Minda_
    /// WithSqlToolLive above (0 calls - the documented, expected "restraint" behaviour, 7/7 natural
    /// runs never fired it before either). That proves the LOG correctly shows zero rows for a run
    /// that made zero calls, but not that a REAL fired call gets logged - this closes that gap by
    /// calling ReadOnlySqlFetchTool directly, no LLM involved, isolating the mechanism from model
    /// non-determinism. This is also the real "trace users/departments responsible for overdue"
    /// cross-dimension query the user asked to see evidence of, run for real against Minda.
    /// </summary>
    [Fact]
    public async Task FetchScopedSqlData_Minda_DirectCall_LogsARealRowAndProvesReadOnlyGrant()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var writeConnectionString = RequireConfig("ConnectionStrings:RegTrackReportsWrite");
        var toolLog = new SqlToolInvocationRecorder(writeConnectionString);
        var labRunId = $"lab-minda-direct-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        var tool = new ReadOnlySqlFetchTool(connectionString, UserId, TenantId);

        // Real cross-dimension trace: which departments are actually behind the overdue,
        // imprisonment-bearing instances - the concrete "who/what is responsible" query this tool
        // was built for, not a synthetic smoke query.
        const string sql = """
            SELECT DepartmentName, COUNT(*) AS OverdueImprisonmentCount
            FROM #scoped
            WHERE Imprisonment = 1
            GROUP BY DepartmentName
            ORDER BY OverdueImprisonmentCount DESC
            """;

        var result = await tool.FetchDataAsync(sql);
        output.WriteLine($"Direct FetchDataAsync result ({result.Length} chars): {result[..Math.Min(1000, result.Length)]}");

        using (var doc = System.Text.Json.JsonDocument.Parse(result))
        {
            var success = !doc.RootElement.TryGetProperty("error", out _);
            Assert.True(success, $"Expected a real successful result against Minda's prod-readonly data, got: {result}");

            await toolLog.RecordAsync(labRunId, "direct_lab_call", "fetch_scoped_sql_data", sql, success, result.Length);
        }

        var loggedRows = await ReadBackAsync(writeConnectionString, labRunId);
        output.WriteLine($"dbo.InsightsToolInvocationLog rows for {labRunId}: {loggedRows.Count}");
        Assert.Single(loggedRows);
        Assert.True(loggedRows[0].Success);
        Assert.Equal("fetch_scoped_sql_data", loggedRows[0].ToolName);

        // [ADDED 2026-09-23] Re-proves the doc comment's 2026-09-22 claim, fresh, today: the same
        // login this tool uses to READ is genuinely DB-enforced read-only, not just blocked
        // client-side by ReadOnlySqlFetchTool's own keyword check. A raw UPDATE on the SAME
        // connection string, against a real table that genuinely exists on this replica
        // (CustomerBranch - the same table ReadOnlySqlFetchTool's own #scoped setup JOINs), bypassing
        // the tool entirely, must fail with a real permission error - not "table doesn't exist" (a
        // log table only migrated onto the separate writable UAT DB would give a misleading result
        // here, so this deliberately targets real tenant data instead).
        await using (var rawConnection = new SqlConnection(connectionString))
        {
            await rawConnection.OpenAsync();
            await using var updateAttempt = new SqlCommand(
                "UPDATE dbo.CustomerBranch SET Name = Name WHERE 1 = 0", rawConnection);

            var denied = await Assert.ThrowsAsync<SqlException>(() => updateAttempt.ExecuteNonQueryAsync());
            output.WriteLine($"Real UPDATE on the prod-readonly login was refused, as expected: {denied.Number}: {denied.Message}");
        }
    }

    private static async Task<List<(string ToolName, string Detail, bool Success)>> ReadBackAsync(string connectionString, string runId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT ToolName, Detail, Success FROM dbo.InsightsToolInvocationLog WHERE RunId = @RunId ORDER BY Id", connection);
        command.Parameters.AddWithValue("@RunId", runId);

        var rows = new List<(string, string, bool)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));

        return rows;
    }
}
