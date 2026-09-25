using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// [LAB, 2026-09-25] [RETARGETED FROM MINDA, SAME DAY] Originally built against real Minda (1008,
/// user 12116) via the prod-readonly replica (regtech_dev01_readonly, 10.224.254.4) - matching
/// MindaUsersInternalReportTest.cs's own established pairing, and the same setup that produced
/// local-report-users-minda-fullfreehand.html. Run live: all 8 real proc calls failed with
/// "Procedure or function usp_Insights_Dimension_X has too many arguments specified" - the
/// replica's copy of every one of these 8 procs is still the PRE-window version; the SQL deploys
/// this session only ever went to UAT (10.13.0.6/vitComplianceSystem). The replica is read-only by
/// design (see the test-tenant-and-readonly-db project memory) and mirrors a separate source this
/// session has no path to alter or trigger a resync on - real Minda data cannot exercise the new
/// window gate until whoever owns that replica's sync refreshes it.
///
/// Retargeted to tenant 1285 (Adi Demo Customer), user 11416 on UAT instead - the same pairing
/// already proven live earlier this session for Act/Event's own window-gate tests, and the ONLY
/// real dataset currently compatible with all 8 of the newly-windowed procs. Same real full freehand
/// pipeline shape (fetch -> compose -> narrate(v2) -> render) for all 8 freehand dimensions that got
/// the hard @WindowStart/@WindowEnd population gate this session (Act, Event, Location, Risk,
/// Nature, Departments, Users, Internal), using the _v2 prompts (the "window" data_quality phrasing
/// fix).
///
/// Entity is deliberately excluded - it renders through the deterministic fixed_holistic path, a
/// different mechanism with no freehand composition/render prompt to test here.
///
/// NOT part of the automated suite - hits real UAT SQL and spends real LLM tokens, 8 real pipeline
/// runs. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~MindaAllWindowedDimensionsFreehandLabTest
/// Reads D:\trpl-reginsights-dev\appsettings.uat.json directly - no credentials in any shell
/// command.
/// </summary>
public sealed class MindaAllWindowedDimensionsFreehandLabTest(ITestOutputHelper output)
{
    private const int TenantId = 1285;
    private const int UserId = 11416;
    private const string TenantLabel = "Adi Demo Customer";

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.uat.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    private static string RequireConfig(string key) =>
        Config[key]
        ?? throw new InvalidOperationException($"Set '{key}' in appsettings.uat.json/appsettings.minda-readonly.json or as an env var before running this lab test.");

    private static (DateTime WindowStart, DateTime WindowEnd) Window30Days() =>
        (DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);

    private async Task<(string RowsJson, string ControlTotalsJson, string DataQualityJson)> RunFreehandPipelineAsync<TControlTotals, TRow>(
        string dimension, string outFileSuffix,
        Func<SqlDimensionRepository, DateTime, DateTime, Task<DimensionResult<TControlTotals, TRow>>> fetch)
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var model = RequireConfig("Llm:Maf:Model");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
        var (windowStart, windowEnd) = Window30Days();

        var repo = new SqlDimensionRepository(connectionString);
        var r = await fetch(repo, windowStart, windowEnd);
        output.WriteLine($"[{dimension}] Fetched {r.Rows.Count} rows, {r.Assertions.Count} assertions, {r.Findings.Count} findings, {r.DataQuality.Count} data_quality notes.");
        foreach (var dq in r.DataQuality)
            output.WriteLine($"  data_quality: {dq.Issue} -- {dq.Detail}");

        var rowsJson = System.Text.Json.JsonSerializer.Serialize(r.Rows);
        var controlTotalsJson = System.Text.Json.JsonSerializer.Serialize(r.ControlTotals);
        var dataQualityJson = System.Text.Json.JsonSerializer.Serialize(r.DataQuality);
        output.WriteLine($"[{dimension}] control_totals: {controlTotalsJson}");

        var compositionFile = $"02_composition_freehand_{dimension.ToLowerInvariant()}_v2.md";
        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, compositionFile));
        var compositionAgent = new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, $"FreehandComposition{dimension}Agent", $"Decides structure/hero/emphasis for a freehand {dimension} insight from real tenant data.",
            compositionInstructions));
        var composeResult = await compositionAgent.ComposeAsync(r.Assertions, r.Findings, rowsJson, controlTotalsJson, dataQualityJson);
        var plan = composeResult.Value;
        output.WriteLine($"[{dimension}] Compose: {composeResult.TotalTokens} tokens. Hero: {plan.Hero.Block} - {plan.Hero.Reason}");
        foreach (var block in plan.Blocks)
            output.WriteLine($"  Block: {block.Block} - {block.Emphasis}");

        var narrateInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "v2/03_narrative_analyst.md"));
        var narrateAgent = new MafAnalystNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "AnalystNarrativeAgent", "Traces root cause from typed assertions and raw dimension rows.", narrateInstructions));
        var narrateResult = await narrateAgent.AnalyzeAndNarrateAsync(plan, r.Assertions, r.Findings, dimension, rowsJson, controlTotalsJson);
        output.WriteLine($"[{dimension}] Narrate: {narrateResult.TotalTokens} tokens, {narrateResult.Value.Blocks.Count} blocks.");
        foreach (var block in narrateResult.Value.Blocks)
        {
            if (block.Refused is not null)
                output.WriteLine($"  [{block.Block}] REFUSED - assertion_id={block.Refused.AssertionId} reason={block.Refused.Reason}");
            else
                output.WriteLine($"  [{block.Block}] {block.Prose}");
        }

        // [NOTE] Render filenames don't all mirror the dimension name exactly - Departments'
        // render file is singular ("department") and Users' is singular ("user"), both real,
        // pre-existing filename choices from when each prompt was first created.
        var renderFileStem = dimension switch
        {
            "Departments" => "department",
            "Users" => "user",
            _ => dimension.ToLowerInvariant(),
        };
        var renderFile = $"05_report_html_dimension_selection_{renderFileStem}_v2.md";
        var renderInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, renderFile));
        var renderAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, $"DimensionSelection{dimension}ReportHtmlAgent", $"Renders a freehand-composed {dimension} insight as self-contained HTML.",
            renderInstructions));
        var renderResult = await renderAgent.RenderAsync(
            plan, narrateResult.Value, r.Assertions, TenantLabel, "dimension_selection", DateTime.UtcNow,
            locationRows: null,
            dimensionRowsJson: new Dictionary<string, string> { [dimension] = rowsJson },
            dimensionControlTotalsJson: new Dictionary<string, string> { [dimension] = controlTotalsJson });
        output.WriteLine($"[{dimension}] Render: {renderResult.TotalTokens} tokens.");

        var html = PoppinsFontInjector.Inject(renderResult.Value);
        var outPath = $@"D:\trpl-reginsights-dev\local-report-{outFileSuffix}-adi1285-30day-v2.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"[{dimension}] Written: {outPath}");

        Assert.NotEmpty(narrateResult.Value.Blocks);
        Assert.Contains("<html", renderResult.Value, StringComparison.OrdinalIgnoreCase);

        return (rowsJson, controlTotalsJson, dataQualityJson);
    }

    [Fact]
    public Task GenerateActReport_Minda() => RunFreehandPipelineAsync(
        "Act", "act", (repo, ws, we) => repo.GetActAsync(UserId, TenantId, ws, we));

    [Fact]
    public Task GenerateEventReport_Minda() => RunFreehandPipelineAsync(
        "Event", "event", (repo, ws, we) => repo.GetEventAsync(UserId, TenantId, ws, we));

    [Fact]
    public Task GenerateLocationReport_Minda() => RunFreehandPipelineAsync(
        "Location", "location", (repo, ws, we) => repo.GetLocationAsync(UserId, TenantId, ws, we));

    [Fact]
    public Task GenerateRiskReport_Minda() => RunFreehandPipelineAsync(
        "Risk", "risk", (repo, ws, we) => repo.GetRiskAsync(UserId, TenantId, ws, we));

    [Fact]
    public Task GenerateNatureReport_Minda() => RunFreehandPipelineAsync(
        "Nature", "nature", (repo, ws, we) => repo.GetNatureAsync(UserId, TenantId, ws, we));

    [Fact]
    public Task GenerateDepartmentsReport_Minda() => RunFreehandPipelineAsync(
        "Departments", "departments", (repo, ws, we) => repo.GetDepartmentsAsync(UserId, TenantId, ws, we));

    [Fact]
    public Task GenerateUsersReport_Minda() => RunFreehandPipelineAsync(
        "Users", "users", (repo, ws, we) => repo.GetUsersAsync(UserId, TenantId, ws, we));

    [Fact]
    public Task GenerateInternalReport_Minda() => RunFreehandPipelineAsync(
        "Internal", "internal", (repo, ws, we) => repo.GetInternalAsync(UserId, TenantId, ws, we));
}
