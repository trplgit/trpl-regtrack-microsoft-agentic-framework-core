using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// [LAB, 2026-09-23] User request: "Entity" (== the fixed Holistic Insights report - Entity-alone
/// requests already redirect to fixed_holistic, ReportTypeRouter.cs) should call all 14/15 real
/// dimensions like FetchDimensionsActivity already does, and get a freehand agent's help on top of
/// what the template already knows how to present. This reproduces the real fixed_holistic pipeline
/// (fetch every dimension, ComputeScoreActivity, FixedHolisticComposition.Build - ALL UNCHANGED,
/// same code production uses) but swaps the NARRATE step: v1 NarrateActivity+Reflect ->
/// MafAnalystNarrativeAgent.AnalyzeAndNarrateHolisticAsync (new method, same class the single-
/// dimension freehand path already uses), with a real ReadOnlySqlFetchTool for cross-dimension
/// tracing across ALL fetched dimensions at once - the concrete "which department/performer is
/// actually behind this" capability the user asked for. The render step
/// (05_report_html_fixed_holistic.md) is UNCHANGED, per explicit instruction ("leave the prompt/
/// template as it is").
///
/// [KNOWN LIMITATION, deliberate scope cut for this first pass] Does NOT run the post-render
/// deterministic injector activities fixed_holistic normally goes through in production
/// (InjectCoverageGridActivity/InjectCoverageCssActivity/InjectCoverageScriptActivity - the real
/// per-branch Coverage store grid; InjectBacklogAgeBarActivity/its CSS; InjectForwardLookActivity/
/// its CSS; NormalizeActivity/SanitizeActivity; ValidateFixedHolisticStructureActivity's structure
/// gate; PlaywrightQaActivity/VisionQaActivity). The narrative prose and the Actions tab - the
/// actual target of this experiment - render for real; the Coverage tile grid and a few chart
/// sections will look thinner than a real production report until those injectors are added to
/// this harness too, a natural follow-up once the narrate content itself is reviewed.
/// </summary>
public sealed class EntityHolisticFreehandNarrateLabTest(ITestOutputHelper output)
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
    public async Task GenerateEntityHolisticReport_Minda_FreehandNarrate()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var model = RequireConfig("Llm:Maf:Model");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");

        var dimensionRepository = new SqlDimensionRepository(connectionString);

        var dimensionResultsJson = new Dictionary<string, string>();
        var rowsJsonByDim = new Dictionary<string, string>();
        var controlTotalsJsonByDim = new Dictionary<string, string>();
        var dataQualityJsonByDim = new Dictionary<string, string>();
        var assertions = new List<Assertion>();
        var findings = new List<Finding>();
        LocationRow[]? locationRows = null;
        BacklogAgingRow[]? backlogAgingRows = null;
        BacklogAgingControlTotals? backlogAgingControlTotals = null;
        ForwardPipelineRow[]? forwardPipelineRows = null;
        ForwardPipelineControlTotals? forwardPipelineControlTotals = null;
        ForwardRiskControlTotals? forwardRiskControlTotals = null;

        async Task TryFetchAsync<TControlTotals, TRow>(string name, Func<Task<DimensionResult<TControlTotals, TRow>>> fetch)
        {
            try
            {
                var result = await fetch();
                dimensionResultsJson[name] = System.Text.Json.JsonSerializer.Serialize(result);
                rowsJsonByDim[name] = System.Text.Json.JsonSerializer.Serialize(result.Rows);
                controlTotalsJsonByDim[name] = System.Text.Json.JsonSerializer.Serialize(result.ControlTotals);
                dataQualityJsonByDim[name] = System.Text.Json.JsonSerializer.Serialize(result.DataQuality);
                assertions.AddRange(result.Assertions);
                findings.AddRange(result.Findings);
                output.WriteLine($"  {name}: {result.Rows.Count} rows, {result.Assertions.Count} assertions");
            }
            catch (Exception ex)
            {
                output.WriteLine($"  {name}: FAILED (degrading, same as production's partial-generation stance) - {ex.GetType().Name}: {ex.Message}");
            }
        }

        var now = DateTime.UtcNow;
        var windowStart = new DateTime(ReportPeriodResolver.CurrentFyStartYear(now), 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var windowEnd = now;

        output.WriteLine("Fetching all real dimensions for tenant 1008 (Minda), same set FetchDimensionsActivity uses:");
        await TryFetchAsync("Location", async () =>
        {
            var result = await dimensionRepository.GetLocationAsync(UserId, TenantId, windowStart, windowEnd);
            locationRows = result.Rows.ToArray();
            return result;
        });
        await TryFetchAsync("Entity", () => dimensionRepository.GetEntityAsync(UserId, TenantId, windowStart, windowEnd));
        await TryFetchAsync("Risk", () => dimensionRepository.GetRiskAsync(UserId, TenantId, windowStart, windowEnd));
        await TryFetchAsync("Nature", () => dimensionRepository.GetNatureAsync(UserId, TenantId, windowStart, windowEnd));
        await TryFetchAsync("Departments", () => dimensionRepository.GetDepartmentsAsync(UserId, TenantId, windowStart, windowEnd));
        await TryFetchAsync("Act", () => dimensionRepository.GetActAsync(UserId, TenantId, windowStart, windowEnd));
        // Same UsersHeadcountCalculator patch FetchDimensionsActivity applies - see its own doc
        // comment (and this session's "0:0" finding) for why: raw SQL control totals never compute
        // PerformerUserCount/ReviewerUserCount, only this C# post-step does.
        await TryFetchAsync("Users", async () =>
        {
            var result = await dimensionRepository.GetUsersAsync(UserId, TenantId, windowStart, windowEnd);
            var (performerUserCount, reviewerUserCount) = UsersHeadcountCalculator.Compute(result.Rows);
            return new DimensionResult<UsersControlTotals, UsersRow>(
                result.Dimension,
                result.ControlTotals with { PerformerUserCount = performerUserCount, ReviewerUserCount = reviewerUserCount },
                result.Rows, result.Detectors, result.Assertions, result.Findings, result.DataQuality);
        });
        await TryFetchAsync("Internal", () => dimensionRepository.GetInternalAsync(UserId, TenantId, windowStart, windowEnd));
        await TryFetchAsync("Event", () => dimensionRepository.GetEventAsync(UserId, TenantId, windowStart, windowEnd));
        await TryFetchAsync("Licence", () => dimensionRepository.GetLicenceAsync(UserId, TenantId));
        await TryFetchAsync("BacklogAging", async () =>
        {
            var result = await dimensionRepository.GetBacklogAgingAsync(UserId, TenantId);
            backlogAgingRows = result.Rows.ToArray();
            backlogAgingControlTotals = result.ControlTotals;
            return result;
        });
        await TryFetchAsync("TimelinessFY", () => dimensionRepository.GetTimelinessFYAsync(UserId, TenantId, windowStart, windowEnd));
        await TryFetchAsync("ForwardPipeline", async () =>
        {
            var result = await dimensionRepository.GetForwardPipelineAsync(UserId, TenantId);
            forwardPipelineRows = result.Rows.ToArray();
            forwardPipelineControlTotals = result.ControlTotals;
            return result;
        });
        await TryFetchAsync("EvidenceIntegrity", () => dimensionRepository.GetEvidenceIntegrityAsync(UserId, TenantId, windowStart, windowEnd));
        await TryFetchAsync("ForwardRisk", async () =>
        {
            var result = await dimensionRepository.GetForwardRiskAsync(UserId, TenantId);
            forwardRiskControlTotals = result.ControlTotals;
            return result;
        });

        // Node 4a - UNCHANGED, same deterministic C# every real fixed_holistic run uses.
        var scoreOutput = ComputeScoreActivity.Run(new ComputeScoreInput(dimensionResultsJson));
        dimensionResultsJson["Score"] = scoreOutput.ScoreDimensionResultJson;
        assertions.AddRange(scoreOutput.Assertions);
        output.WriteLine($"Composite score: {scoreOutput.OverallHealth.Score} ({scoreOutput.OverallHealth.Band}, trend {scoreOutput.OverallHealth.Trend})");
        output.WriteLine($"Total: {dimensionResultsJson.Count} dimensions fetched (incl. Score), {assertions.Count} assertions, {findings.Count} findings.");

        // Composition: UNCHANGED - the exact same 6-block fixed plan every real run uses.
        var plan = FixedHolisticComposition.Build();

        // Narrate: NEW - the holistic freehand method, real cross-dimension SQL tool live across
        // ALL fetched dimensions at once, self-reflection, root-cause pattern checklist.
        var writeConnectionString = RequireConfig("ConnectionStrings:RegTrackReportsWrite");
        var toolLog = new SqlToolInvocationRecorder(writeConnectionString);
        var labRunId = $"lab-minda-entity-holistic-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        var callLog = new List<(string Sql, int ResultLength)>();
        var narrateInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "v2/03_narrative_analyst_holistic.md"));
        var narrateAgent = new MafAnalystNarrativeAgent(
            MafAgentFactory.CreateJsonAgent(endpoint, model, apiKey, "AnalystNarrativeHolisticAgent", "Traces cross-dimension root cause for the whole fixed Holistic Insights report.", narrateInstructions),
            readOnlySqlConnectionString: connectionString,
            onSqlToolInvoked: async (runId, sql, result, success) =>
            {
                callLog.Add((sql, result.Length));
                output.WriteLine($"*** SQL TOOL FIRED *** query: {sql}");
                output.WriteLine($"    result ({result.Length} chars, success={success}): {result[..Math.Min(400, result.Length)]}");
                await toolLog.RecordAsync(runId, "analyze_and_narrate_holistic", "fetch_scoped_sql_data", sql, success, result.Length);
            });

        var narrateResult = await narrateAgent.AnalyzeAndNarrateHolisticAsync(
            plan, assertions, findings, rowsJsonByDim, controlTotalsJsonByDim, dataQualityJsonByDim,
            userId: UserId, customerId: TenantId, runId: labRunId);
        output.WriteLine($"Narrate: {narrateResult.TotalTokens} tokens, {narrateResult.Value.Blocks.Count} blocks. SQL tool calls: {callLog.Count}");
        foreach (var block in narrateResult.Value.Blocks)
            output.WriteLine($"  [{block.Block}] {block.Prose?[..Math.Min(160, block.Prose?.Length ?? 0)]}");

        // Render: UNCHANGED - the exact same template every real fixed_holistic run uses.
        var renderInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "05_report_html_fixed_holistic.md"));
        var renderAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "FixedHolisticReportHtmlAgent", "Renders the fixed Holistic Insights report as self-contained HTML.",
            renderInstructions));
        var renderResult = await renderAgent.RenderAsync(
            plan, narrateResult.Value, assertions, "Minda Corporation Group", "fixed_holistic", DateTime.UtcNow,
            locationRows: locationRows,
            dimensionRowsJson: null,
            dimensionControlTotalsJson: null);

        // [ADDED 2026-09-23, EXTENDED 2026-09-23] The real deterministic post-generation injectors
        // production runs - the render agent never authors any of these panes itself (each one's
        // own [BUG FOUND LIVE] doc comment: asked to hand-author a mechanical, per-row or
        // per-bucket shape, it silently sampled/dropped/omitted CSS). Same order the real
        // orchestrator uses (InsightsReportOrchestrator.cs: Font -> Grid -> Css -> Script ->
        // BacklogAgeBar -> BacklogAgeBarCss -> ForwardLook -> ForwardLookCss), called directly here
        // rather than through their Activity wrappers - each Activity is a thin wrapper with no
        // logic of its own (confirmed by reading all of them).
        // [ADDED 2026-09-23] The render agent kept writing the forbidden di-caveats footer even
        // after the prompt was updated to explicitly ban it - see CaveatsFooterRemover's own doc
        // comment. Deterministic strip, same reasoning as every other injector below (do not trust
        // a single negative instruction buried in a huge prompt).
        var html = CaveatsFooterRemover.Remove(PoppinsFontInjector.Inject(renderResult.Value));
        html = CoverageGridInjector.Inject(html, locationRows);
        html = CoverageCssInjector.Inject(html);
        html = CoverageScriptInjector.Inject(html);
        html = BacklogAgeBarInjector.Inject(html, backlogAgingRows, backlogAgingControlTotals);
        html = BacklogAgeBarCssInjector.Inject(html);
        html = ForwardLookInjector.Inject(html, forwardRiskControlTotals, forwardPipelineControlTotals, forwardPipelineRows);
        html = ForwardLookCssInjector.Inject(html);
        const string outPath = @"D:\trpl-reginsights-dev\local-report-entity-holistic-minda-freehand.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"Written: {outPath}");

        var loggedRows = await ReadBackAsync(writeConnectionString, labRunId);
        output.WriteLine($"dbo.InsightsToolInvocationLog rows for {labRunId}: {loggedRows}");

        Assert.NotEmpty(narrateResult.Value.Blocks);
    }

    private static async Task<int> ReadBackAsync(string connectionString, string runId)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT COUNT(*) FROM dbo.InsightsToolInvocationLog WHERE RunId = @RunId", connection);
        command.Parameters.AddWithValue("@RunId", runId);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
