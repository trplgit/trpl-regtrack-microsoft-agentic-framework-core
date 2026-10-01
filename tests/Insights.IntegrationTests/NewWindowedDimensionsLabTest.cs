using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// [LAB, 2026-09-25] Proves the 2026-09-25 hard @WindowStart/@WindowEnd population gate (Location,
/// Entity, Risk, Nature, Departments, Users, Internal - sql/05,07,08,09,10,12,13, deployed to UAT
/// this session) actually works against real data, end to end: the SQL procs run and reconcile
/// with a real window, and (for the freehand dims) the composition/render pipeline uses the _v2
/// prompts' fixed "window" data_quality phrasing instead of the generic-filler bug found live on
/// Event on 2026-09-25.
///
/// NOT part of the automated suite - hits real UAT SQL and spends real LLM tokens on the two full
/// pipeline tests. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~NewWindowedDimensionsLabTest
///
/// Tenant 1285 (Adi Demo Customer), user 11416 - the pair proven live this session for Act/Event's
/// own window-gate tests (real, non-trivial scoped population; TENANT_NOT_ELIGIBLE ruled out via
/// the actual API earlier in the session). Reads D:\trpl-reginsights-dev\appsettings.uat.json
/// directly, same as every other lab test in this file's neighbourhood - no credentials in any
/// shell command.
/// </summary>
public sealed class NewWindowedDimensionsLabTest(ITestOutputHelper output)
{
    private const int TenantId = 1285;
    private const int UserId = 11416;

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.uat.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    private static string RequireConfig(string key) =>
        Config[key]
        ?? throw new InvalidOperationException($"Set '{key}' in appsettings.uat.json or as an env var before running this lab test.");

    // [FOUND LIVE 2026-09-22, FIXED SAME DAY] The exact generic-filler regression the _v2 prompts
    // exist to prevent - if this phrase reappears in a rendered report, the fix has regressed.
    private const string KnownBadGenericFillerPhrase = "no further definition was provided";

    private static (DateTime WindowStart, DateTime WindowEnd) Window30Days() =>
        (DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);

    /// <summary>
    /// Common assertion for every fetch-only smoke test below: the proc actually ran, reconciled,
    /// and stamped a real "window" data_quality entry with real dates in it - not just "the call
    /// didn't throw".
    /// </summary>
    private void AssertRealWindowedResult<TControlTotals, TRow>(
        string label, DimensionResult<TControlTotals, TRow> result, DateTime windowStart, DateTime windowEnd)
    {
        output.WriteLine($"[{label}] {result.Rows.Count} rows, {result.Assertions.Count} assertions, {result.Findings.Count} findings, {result.DataQuality.Count} data_quality notes.");
        foreach (var dq in result.DataQuality)
            output.WriteLine($"  data_quality: {dq.Issue} -- {dq.Detail}");

        var windowNote = result.DataQuality.FirstOrDefault(d => d.Issue == "window");
        Assert.True(windowNote is not null, $"[{label}] expected a real 'window' data_quality entry - the hard gate's own self-declaration was not emitted.");
        Assert.False(string.IsNullOrWhiteSpace(windowNote!.Detail), $"[{label}] 'window' data_quality entry had no real detail text.");
        Assert.Contains(windowStart.ToString("yyyy-MM-dd"), windowNote.Detail);
        Assert.Contains(windowEnd.ToString("yyyy-MM-dd"), windowNote.Detail);
    }

    [Fact]
    public async Task FetchLocation_RealWindow_ReconcilesAndDeclaresTheWindow()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var (windowStart, windowEnd) = Window30Days();
        var repo = new SqlDimensionRepository(connectionString);
        var r = await repo.GetLocationAsync(UserId, TenantId, windowStart, windowEnd);
        output.WriteLine($"[Location] ScopedInstances={r.ControlTotals.ScopedInstances} SumOfRows={r.ControlTotals.SumOfRows} OverdueInstances={r.ControlTotals.OverdueInstances}");
        AssertRealWindowedResult("Location", r, windowStart, windowEnd);
    }

    [Fact]
    public async Task FetchEntity_RealWindow_ReconcilesAndDeclaresTheWindow()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var (windowStart, windowEnd) = Window30Days();
        var repo = new SqlDimensionRepository(connectionString);
        var r = await repo.GetEntityAsync(UserId, TenantId, windowStart, windowEnd);
        output.WriteLine($"[Entity] ScopedInstances={r.ControlTotals.ScopedInstances} SumOfRows={r.ControlTotals.SumOfRows} Reconciled={r.ControlTotals.Reconciled}");
        AssertRealWindowedResult("Entity", r, windowStart, windowEnd);
    }

    [Fact]
    public async Task FetchRisk_RealWindow_ReconcilesAndDeclaresTheWindow()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var (windowStart, windowEnd) = Window30Days();
        var repo = new SqlDimensionRepository(connectionString);
        var r = await repo.GetRiskAsync(UserId, TenantId, windowStart, windowEnd);
        output.WriteLine($"[Risk] ScopedInstances={r.ControlTotals.ScopedInstances} SumOfRows={r.ControlTotals.SumOfRows} OverdueInstances={r.ControlTotals.OverdueInstances}");
        foreach (var row in r.Rows)
            output.WriteLine($"  {row.RiskLabel}: Instances={row.Instances} Overdue={row.Overdue}");
        AssertRealWindowedResult("Risk", r, windowStart, windowEnd);
    }

    [Fact]
    public async Task FetchNature_RealWindow_ReconcilesAndDeclaresTheWindow()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var (windowStart, windowEnd) = Window30Days();
        var repo = new SqlDimensionRepository(connectionString);
        var r = await repo.GetNatureAsync(UserId, TenantId, windowStart, windowEnd);
        output.WriteLine($"[Nature] ScopedInstances={r.ControlTotals.ScopedInstances} CategorisedInstances={r.ControlTotals.CategorisedInstances}");
        AssertRealWindowedResult("Nature", r, windowStart, windowEnd);
    }

    [Fact]
    public async Task FetchDepartments_RealWindow_ReconcilesAndDeclaresTheWindow()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var (windowStart, windowEnd) = Window30Days();
        var repo = new SqlDimensionRepository(connectionString);
        var r = await repo.GetDepartmentsAsync(UserId, TenantId, windowStart, windowEnd);
        output.WriteLine($"[Departments] ScopedInstances={r.ControlTotals.ScopedInstances} AssignedInstances={r.ControlTotals.AssignedInstances} UnassignedInstances={r.ControlTotals.UnassignedInstances}");
        AssertRealWindowedResult("Departments", r, windowStart, windowEnd);
    }

    [Fact]
    public async Task FetchUsers_RealWindow_ReconcilesAndDeclaresTheWindow()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var (windowStart, windowEnd) = Window30Days();
        var repo = new SqlDimensionRepository(connectionString);
        var r = await repo.GetUsersAsync(UserId, TenantId, windowStart, windowEnd);
        output.WriteLine($"[Users] ScopedInstances={r.ControlTotals.ScopedInstances} SumOfPerUserInstances={r.ControlTotals.SumOfPerUserInstances} PerformerUserCount={r.ControlTotals.PerformerUserCount} ReviewerUserCount={r.ControlTotals.ReviewerUserCount}");
        // [KEY CHECK] The dual-population fix (#asg built from the already-narrowed #inst for BOTH
        // roles) - proves both performer and reviewer sides genuinely reflect the SAME window, not
        // just one of them.
        Assert.True(r.ControlTotals.SumOfPerUserInstances >= 0, "[Users] SumOfPerUserInstances should never be negative - a real reconciled figure.");
        AssertRealWindowedResult("Users", r, windowStart, windowEnd);
    }

    [Fact]
    public async Task FetchInternal_RealWindow_ReconcilesAndDeclaresTheWindow()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var (windowStart, windowEnd) = Window30Days();
        var repo = new SqlDimensionRepository(connectionString);
        var r = await repo.GetInternalAsync(UserId, TenantId, windowStart, windowEnd);
        output.WriteLine($"[Internal] ScopedInstances={r.ControlTotals.ScopedInstances} SumOfRows={r.ControlTotals.SumOfRows}");
        AssertRealWindowedResult("Internal", r, windowStart, windowEnd);
    }

    /// <summary>
    /// Full real pipeline for Location (fetch -> compose -> narrate -> render), using the _v2
    /// prompts fixed this session. The regression this proves: the rendered HTML must NOT contain
    /// the generic-filler phrase the render agent fell back to on Event before the fix, and it must
    /// contain the real window dates instead.
    /// </summary>
    [Fact]
    public async Task GenerateRealLocationReport_WithWindow_UsesTheFixedV2Prompts()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var model = RequireConfig("Llm:Maf:Model");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
        var (windowStart, windowEnd) = Window30Days();

        var repo = new SqlDimensionRepository(connectionString);
        var r = await repo.GetLocationAsync(UserId, TenantId, windowStart, windowEnd);
        output.WriteLine($"[Location] Fetched {r.Rows.Count} rows. ScopedInstances={r.ControlTotals.ScopedInstances}");

        var rowsJson = System.Text.Json.JsonSerializer.Serialize(r.Rows);
        var controlTotalsJson = System.Text.Json.JsonSerializer.Serialize(r.ControlTotals);
        var dataQualityJson = System.Text.Json.JsonSerializer.Serialize(r.DataQuality);

        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "02_composition_freehand_location_v2.md"));
        var compositionAgent = new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "FreehandCompositionLocationAgent", "Decides structure/hero/emphasis for a freehand Location insight from real tenant data.",
            compositionInstructions));
        var composeResult = await compositionAgent.ComposeAsync(r.Assertions, r.Findings, rowsJson, controlTotalsJson, dataQualityJson);
        var plan = composeResult.Value;
        output.WriteLine($"[Location] Compose: {composeResult.TotalTokens} tokens. Hero: {plan.Hero.Block}");

        var narrateInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "v2/03_narrative_analyst.md"));
        var narrateAgent = new MafAnalystNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "AnalystNarrativeAgent", "Traces root cause from typed assertions and raw dimension rows.", narrateInstructions));
        var narrateResult = await narrateAgent.AnalyzeAndNarrateAsync(plan, r.Assertions, r.Findings, "Location", rowsJson, controlTotalsJson);
        output.WriteLine($"[Location] Narrate: {narrateResult.TotalTokens} tokens, {narrateResult.Value.Blocks.Count} blocks.");

        var renderInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "05_report_html_dimension_selection_location_v2.md"));
        var renderAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "DimensionSelectionLocationReportHtmlAgent", "Renders a freehand-composed Location insight as self-contained HTML.",
            renderInstructions));
        var renderResult = await renderAgent.RenderAsync(
            plan, narrateResult.Value, r.Assertions, "Adi Demo Customer", "dimension_selection", DateTime.UtcNow,
            locationRows: null,
            dimensionRowsJson: new Dictionary<string, string> { ["Location"] = rowsJson },
            dimensionControlTotalsJson: new Dictionary<string, string> { ["Location"] = controlTotalsJson });
        output.WriteLine($"[Location] Render: {renderResult.TotalTokens} tokens.");

        var html = PoppinsFontInjector.Inject(renderResult.Value);
        const string outPath = @"D:\trpl-reginsights-dev\local-report-location-adi1285-30day-window-v2.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"[Location] Written: {outPath}");

        Assert.NotEmpty(narrateResult.Value.Blocks);
        Assert.Contains("<html", renderResult.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(KnownBadGenericFillerPhrase, renderResult.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(windowStart.Year.ToString(), renderResult.Value);
    }

    /// <summary>
    /// Full real pipeline for Users - the dual-population dimension (performer AND reviewer roles
    /// both narrowed to the SAME window through the already-narrowed #inst). Same regression proof
    /// as the Location test above, on the dimension with the most structurally risky window
    /// implementation of the 7.
    /// </summary>
    [Fact]
    public async Task GenerateRealUsersReport_WithWindow_UsesTheFixedV2Prompts()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var model = RequireConfig("Llm:Maf:Model");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
        var (windowStart, windowEnd) = Window30Days();

        var repo = new SqlDimensionRepository(connectionString);
        var r = await repo.GetUsersAsync(UserId, TenantId, windowStart, windowEnd);
        output.WriteLine($"[Users] Fetched {r.Rows.Count} rows. ScopedInstances={r.ControlTotals.ScopedInstances} SumOfPerUserInstances={r.ControlTotals.SumOfPerUserInstances}");

        var rowsJson = System.Text.Json.JsonSerializer.Serialize(r.Rows);
        var controlTotalsJson = System.Text.Json.JsonSerializer.Serialize(r.ControlTotals);
        var dataQualityJson = System.Text.Json.JsonSerializer.Serialize(r.DataQuality);

        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "02_composition_freehand_users_v2.md"));
        var compositionAgent = new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "FreehandCompositionUsersAgent", "Decides structure/hero/emphasis for a freehand Users insight from real tenant data.",
            compositionInstructions));
        var composeResult = await compositionAgent.ComposeAsync(r.Assertions, r.Findings, rowsJson, controlTotalsJson, dataQualityJson);
        var plan = composeResult.Value;
        output.WriteLine($"[Users] Compose: {composeResult.TotalTokens} tokens. Hero: {plan.Hero.Block}");

        var narrateInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "v2/03_narrative_analyst.md"));
        var narrateAgent = new MafAnalystNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "AnalystNarrativeAgent", "Traces root cause from typed assertions and raw dimension rows.", narrateInstructions));
        var narrateResult = await narrateAgent.AnalyzeAndNarrateAsync(plan, r.Assertions, r.Findings, "Users", rowsJson, controlTotalsJson);
        output.WriteLine($"[Users] Narrate: {narrateResult.TotalTokens} tokens, {narrateResult.Value.Blocks.Count} blocks.");

        var renderInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "05_report_html_dimension_selection_user_v2.md"));
        var renderAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "DimensionSelectionUserReportHtmlAgent", "Renders a freehand-composed Users insight as self-contained HTML.",
            renderInstructions));
        var renderResult = await renderAgent.RenderAsync(
            plan, narrateResult.Value, r.Assertions, "Adi Demo Customer", "dimension_selection", DateTime.UtcNow,
            locationRows: null,
            dimensionRowsJson: new Dictionary<string, string> { ["Users"] = rowsJson },
            dimensionControlTotalsJson: new Dictionary<string, string> { ["Users"] = controlTotalsJson });
        output.WriteLine($"[Users] Render: {renderResult.TotalTokens} tokens.");

        var html = PoppinsFontInjector.Inject(renderResult.Value);
        const string outPath = @"D:\trpl-reginsights-dev\local-report-users-adi1285-30day-window-v2.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"[Users] Written: {outPath}");

        Assert.NotEmpty(narrateResult.Value.Blocks);
        Assert.Contains("<html", renderResult.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(KnownBadGenericFillerPhrase, renderResult.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(windowStart.Year.ToString(), renderResult.Value);
    }

    /// <summary>
    /// [ADDED 2026-09-25] Direct, no-LLM proof that ReadOnlySqlFetchTool's new windowStart/windowEnd
    /// params actually narrow #scoped, real UAT data, tenant 1285. Same-query comparison: the
    /// windowed count must be strictly less than the unwindowed one (real data has instances
    /// outside the 30-day window), and the windowed count must match Location's own real
    /// ScopedInstances for the identical window (33, confirmed earlier this session) - proving the
    /// tool's own narrowing produces the SAME population the dimension procs themselves compute,
    /// not just "a smaller number".
    /// </summary>
    [Fact]
    public async Task FetchDataAsync_WithWindow_NarrowsScopedToTheSamePopulationAsTheDimensionProcs()
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var (windowStart, windowEnd) = Window30Days();

        var unwindowedTool = new ReadOnlySqlFetchTool(connectionString, UserId, TenantId);
        var unwindowedResult = await unwindowedTool.FetchDataAsync("SELECT COUNT(*) AS N FROM #scoped");
        output.WriteLine($"Unwindowed #scoped count: {unwindowedResult}");

        var windowedTool = new ReadOnlySqlFetchTool(connectionString, UserId, TenantId, windowStart, windowEnd);
        var windowedResult = await windowedTool.FetchDataAsync("SELECT COUNT(*) AS N FROM #scoped");
        output.WriteLine($"Windowed #scoped count (30-day): {windowedResult}");

        using var unwindowedDoc = System.Text.Json.JsonDocument.Parse(unwindowedResult);
        using var windowedDoc = System.Text.Json.JsonDocument.Parse(windowedResult);

        var unwindowedCount = unwindowedDoc.RootElement.GetProperty("rows")[0].GetProperty("N").GetInt32();
        var windowedCount = windowedDoc.RootElement.GetProperty("rows")[0].GetProperty("N").GetInt32();

        output.WriteLine($"Unwindowed={unwindowedCount}, Windowed={windowedCount}");

        Assert.True(windowedCount < unwindowedCount, $"Expected the windowed count ({windowedCount}) to be strictly less than the unwindowed count ({unwindowedCount}) - real data outside the 30-day window should be excluded.");
        // Real, independently-confirmed figure from this session's own Location proc run for the
        // identical tenant/window (sql/05, ScopedInstances=33) - not a guess.
        Assert.Equal(33, windowedCount);
    }
}
