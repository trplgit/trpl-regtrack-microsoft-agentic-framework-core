using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// [LAB, 2026-09-23] User request: an A/B pair (tool vs no tool) more likely to show a real
/// difference than Internal did (Internal's own rows already carry BranchName/DepartmentName, so
/// there's rarely a real gap left for the tool). ActRow (Insights.Domain/DimensionShapes.cs) has
/// NO BranchName/DepartmentName at all - only a BranchesCovered COUNT - so any "which real
/// branches/departments drive this Act's overdue rate" question is structurally unanswerable from
/// Act's own data alone. Confirmed live already: one of the two real tool calls in the Entity/
/// holistic run this session was exactly this shape (`SELECT DepartmentName, BranchName, ...
/// WHERE ActID = 7310`). Same real Minda tenant/user, same read-only prod-replica DB, same
/// RegTrackReportsWrite split for tool-invocation logging as every other lab test this session.
/// </summary>
public sealed class MindaActReportTest(ITestOutputHelper output)
{
    private const int TenantId = 1008;
    private const int UserId = 12116;

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.uat.json", optional: true)
        .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.minda-readonly.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    private static string RequireConfig(string key) =>
        Config[key]
        ?? throw new InvalidOperationException($"Set '{key}' in appsettings.uat.json or as an env var before running this lab test.");

    [Fact]
    public async Task GenerateActReport_Minda_WithSqlToolLive()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var writeConnectionString = RequireConfig("ConnectionStrings:RegTrackReportsWrite");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var model = RequireConfig("Llm:Maf:Model");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
        const string dimension = "Act";

        var toolLog = new SqlToolInvocationRecorder(writeConnectionString);
        var labRunId = $"lab-minda-act-tool-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        var dimensionRepository = new SqlDimensionRepository(connectionString);
        var r = await dimensionRepository.GetActAsync(UserId, TenantId);
        var rowsJson = System.Text.Json.JsonSerializer.Serialize(r.Rows);
        var controlTotalsJson = System.Text.Json.JsonSerializer.Serialize(r.ControlTotals);
        var dataQualityJson = System.Text.Json.JsonSerializer.Serialize(r.DataQuality);
        output.WriteLine($"Fetched {r.Rows.Count} rows, {r.Assertions.Count} assertions.");

        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "02_composition_freehand_act.md"));
        var compositionAgent = new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "FreehandCompositionActAgent", "Decides structure/hero/emphasis for a freehand Act insight from real tenant data.",
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

        var renderInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "05_report_html_dimension_selection_act.md"));
        var renderAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "DimensionSelectionActReportHtmlAgent", "Renders a freehand-composed Act insight as self-contained HTML.",
            renderInstructions));
        var renderResult = await renderAgent.RenderAsync(
            composeResult.Value, narrateResult.Value, r.Assertions, "Minda Corporation Group", "dimension_selection", DateTime.UtcNow,
            locationRows: null,
            dimensionRowsJson: new Dictionary<string, string> { [dimension] = rowsJson },
            dimensionControlTotalsJson: new Dictionary<string, string> { [dimension] = controlTotalsJson });

        var html = PoppinsFontInjector.Inject(renderResult.Value);
        const string outPath = @"D:\trpl-reginsights-dev\local-report-act-minda-sqltool.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"Written: {outPath}");

        var loggedRows = await ReadBackAsync(writeConnectionString, labRunId);
        output.WriteLine($"dbo.InsightsToolInvocationLog rows for {labRunId}: {loggedRows}");

        Assert.NotEmpty(narrateResult.Value.Blocks);
    }

    [Fact]
    public async Task GenerateActReport_Minda_NoSqlTool()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var writeConnectionString = RequireConfig("ConnectionStrings:RegTrackReportsWrite");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var model = RequireConfig("Llm:Maf:Model");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
        const string dimension = "Act";

        var labRunId = $"lab-minda-act-notool-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        var dimensionRepository = new SqlDimensionRepository(connectionString);
        var r = await dimensionRepository.GetActAsync(UserId, TenantId);
        var rowsJson = System.Text.Json.JsonSerializer.Serialize(r.Rows);
        var controlTotalsJson = System.Text.Json.JsonSerializer.Serialize(r.ControlTotals);
        var dataQualityJson = System.Text.Json.JsonSerializer.Serialize(r.DataQuality);
        output.WriteLine($"Fetched {r.Rows.Count} rows, {r.Assertions.Count} assertions.");

        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "02_composition_freehand_act.md"));
        var compositionAgent = new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "FreehandCompositionActAgent", "Decides structure/hero/emphasis for a freehand Act insight from real tenant data.",
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

        var renderInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "05_report_html_dimension_selection_act.md"));
        var renderAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "DimensionSelectionActReportHtmlAgent", "Renders a freehand-composed Act insight as self-contained HTML.",
            renderInstructions));
        var renderResult = await renderAgent.RenderAsync(
            composeResult.Value, narrateResult.Value, r.Assertions, "Minda Corporation Group", "dimension_selection", DateTime.UtcNow,
            locationRows: null,
            dimensionRowsJson: new Dictionary<string, string> { [dimension] = rowsJson },
            dimensionControlTotalsJson: new Dictionary<string, string> { [dimension] = controlTotalsJson });

        var html = PoppinsFontInjector.Inject(renderResult.Value);
        const string outPath = @"D:\trpl-reginsights-dev\local-report-act-minda-notool.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"Written: {outPath}");

        var loggedRows = await ReadBackAsync(writeConnectionString, labRunId);
        output.WriteLine($"dbo.InsightsToolInvocationLog rows for {labRunId}: {loggedRows} (expected 0 - tool was never constructed this run)");
        Assert.Equal(0, loggedRows);

        Assert.NotEmpty(narrateResult.Value.Blocks);
    }

    private static async Task<int> ReadBackAsync(string connectionString, string runId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.InsightsToolInvocationLog WHERE RunId = @RunId", connection);
        command.Parameters.AddWithValue("@RunId", runId);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
