#pragma warning disable OPENAI001 // GetResponsesClient()/AsIChatClient(...) - experimental in this SDK, same pragma MafAgentFactory already carries.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.Identity;
using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Insights.Worker;
using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// THROWAWAY, personal lab work - NOT part of the shipped product, never wired into CI, never
/// referenced by anything else in this repo. Built for one task: compare the report-HTML render
/// step across the newly-granted Foundry models (gpt-5.6-sol, gpt-5.6-terra, DeepSeek-V4-Flash)
/// against real report content, with per-model cost, so the outputs can go to Vinay alongside a
/// LangFuse cost breakdown.
///
/// TWO-PHASE, BY DESIGN ("don't run the whole pipeline for this"):
///   Phase 1 (CaptureSnapshotAsync, run ONCE) - gather scope, fetch all 9 dimensions, compose,
///   reflect, narrate, reflect, publish-gate - the expensive/slow real-data part - and freeze the
///   RESULT to a JSON file on disk.
///   Phase 2 (RenderWithXAsync, run ONCE PER MODEL, reusing the same frozen snapshot) - only the
///   render-HTML step differs per model. Nothing before it re-runs.
///
/// Render agents use Entra ID (DefaultAzureCredential), not an API key - the whole point of
/// getting Foundry User role assigned. Compose/narrate/reflection and the REVIEWER stay on the
/// existing trusted model (Llm:Maf:*, API-key auth, unchanged) so the comparison judges three
/// different renderers against one constant, fair judge - never a model grading itself.
///
/// Requires: ConnectionStrings__RegTrack, Llm:Maf:Endpoint/Model/ApiKey via MAF_ENDPOINT/MAF_MODEL/
/// MAF_API_KEY (same env vars every other manual test here needs), AZURE_BLOB_CONNECTION_STRING is
/// NOT required - this never persists to GeneratedReport, only to local files. `az login` must
/// already have an active session (DefaultAzureCredential picks up AzureCliCredential from it).
/// </summary>
public sealed class ModelComparisonLabTests(ITestOutputHelper output)
{
    // [2026-09-10] Designated test tenant for RegTrack Insights lab runs against the
    // regtech_dev01_readonly login (10.224.254.4). Read-only checks only against this tenant -
    // never any write. Was tenant 29 / user 38 (a CLAUDE.md stress profile) up to this date.
    private const int TenantId = 1300;
    private const int UserId = 22426;
    private const string ReportType = "compliance_health";
    private const string Period = "FY2025-26";

    private static readonly string LabRoot = Path.Combine(AppContext.BaseDirectory, "model-comparison-lab-output");
    private static readonly string SnapshotPath = Path.Combine(LabRoot, "snapshot.json");
    // Separate file from the dynamic-composition snapshot above - different CompositionPlan shape
    // (FixedHolisticComposition's 6 fixed blocks, not an LLM's per-tenant choice), never mix them up.
    private static readonly string FixedHolisticSnapshotPath = Path.Combine(LabRoot, "snapshot-fixed-holistic.json");
    private static readonly string BrandHandoffPath = @"D:\trpl-reginsights-dev\holistic-insights-handoff\AI-INSIGHTS-BRAND-HANDOFF.md";
    // [v2 handoff, 2026-08-26] The designer's new Sec.6/7 names gpt-5.6-terra-approved.html as
    // the ground-truth reference ("when a rule and instinct disagree, match this file") and
    // explicitly says to feed both before generating - the doc's own Sec."How to use this
    // package" already listed tokens.css as required reading too. Neither was actually being
    // fed before this - only the .md summary was.
    private static readonly string GroundTruthHtmlPath = Path.Combine(Path.GetDirectoryName(BrandHandoffPath)!, "reference", "gpt-5.6-terra-approved.html");
    private static readonly string TokensCssPath = Path.Combine(Path.GetDirectoryName(BrandHandoffPath)!, "reference", "tokens.css");

    /*  [FIX - found live] gpt-5.6-terra-approved.html is the approved reference for
        05_report_html.md/05_report_html_holistic.md's numbered-01-08-sections, non-interactive-tabs
        layout - a DIFFERENT report entirely from 05_report_html_fixed_holistic.md's 6-tab clone of
        the real detailed-insights.component.html product UI. Every fixed-holistic render was being
        reviewed against that wrong reference, which explains 4 of 5 persistent review issues on
        every single run: "doesn't match 01-08 structure", "component vocabulary restriction"
        flagging the real di-tabsroot/di-snapgrid/di-covgrid vocabulary this template is SUPPOSED to
        use, and the visual-comparison mismatch - all direct consequences of comparing against the
        wrong page. The 5th (an @font-face rejection) was ALSO a false positive from the same root
        file: the old ground truth was hand-saved before InjectFontActivity ran on it, so it has
        ZERO @font-face blocks, while every real candidate (correctly) has two, injected
        deterministically AFTER the LLM writes it (see RenderAndReviewAsync's InjectFontActivity
        call) - the reviewer had no way to tell "candidate declared this itself" (a real violation)
        from "the pipeline injected this, exactly as designed" (correct) when its only reference
        point never went through that same step.
        This ground truth is a hand-verified fixed-holistic candidate (all 6 tabs real CSS-only
        click-to-switch, real assertion-backed data in every tile that has one, honest "not
        available yet" for the rest, real injected font) with the one bug that render happened to
        have (a self-contradicting Backlog tile) corrected before being promoted - see
        DiagnoseFixedHolisticReviewIssuesAsync for how to re-verify review issues against it. */
    private static readonly string FixedHolisticGroundTruthHtmlPath =
        Path.Combine(Path.GetDirectoryName(BrandHandoffPath)!, "reference", "gpt-5.6-terra-fixed-holistic-approved.html");

    // [RAISED 2 -> 4, 2026-09-02] Coverage's deterministic-gate failures (grid shape, driving
    // script) were exhausting all 3 attempts (MaxReviewIterations + 1) before ever reaching the
    // cheaper LLM-reviewer stage, on renders taken well before CoverageScriptInjector removed the
    // render agent's own script-authoring requirement entirely (see that class's own [BUG FOUND
    // LIVE] note) - that fix should make this moot going forward, but more real attempts is a
    // reasonable second line of defence for whatever the NEXT template-compliance gap turns out to
    // be, at the cost of more real tokens per render when a run does need to retry.
    private const int MaxReviewIterations = 4;

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new() { WriteIndented = true };

    /// <summary>The three Foundry deployments under test - endpoint + deployment name, confirmed live via `az cognitiveservices account deployment list`, NOT guessed from the handoff message (which had the region wrong for two of them).</summary>
    private static readonly IReadOnlyList<ModelTarget> Targets =
    [
        new("gpt-5.6-sol", "https://trpl-prod-saas-ai-2.openai.azure.com/", "gpt-5.6-sol"),
        new("gpt-5.6-terra", "https://trpl-prod-saas-ai-3.cognitiveservices.azure.com/", "gpt-5.6-terra"),
        new("DeepSeek-V4-Flash", "https://trpl-prod-saas-ai-3.cognitiveservices.azure.com/", "DeepSeek-V4-Flash"),
    ];

    private sealed record ModelTarget(string Label, string Endpoint, string Deployment);

    private sealed record ReportSnapshot(
        int TenantId, string TenantName, string ReportType, string Period, string TenantShape, DateTime GeneratedAtUtc,
        CompositionPlan Plan, NarrativeResult Narrative, IReadOnlyList<Assertion> Assertions, IReadOnlyList<Finding> Findings,
        IReadOnlyList<LocationRow>? LocationRows = null);

    private sealed record ModelRunResult(
        string Model, long TotalTokens, int ReviewIterations, bool Approved, string? HtmlPath,
        IReadOnlyList<HtmlReviewIssue>? RemainingIssues = null, string? Error = null);

    private static IConfiguration BuildConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:RegTrack"] = RequireEnv("ConnectionStrings__RegTrack"),
            ["Llm:Maf:Endpoint"] = RequireEnv("MAF_ENDPOINT"),
            ["Llm:Maf:Model"] = RequireEnv("MAF_MODEL"),
            ["Llm:Maf:ApiKey"] = RequireEnv("MAF_API_KEY"),
            ["Agents:PromptDirectory"] = "./prompts",
        })
        .Build();

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test - see the class doc comment.");

    // [DELETED 2026-09-11] CaptureSnapshotAsync (Phase 1 for the dynamic "compliance_health"
    // path - ComposeActivity/ReflectOnCompositionActivity, 01_composition.md/
    // 02_composition_reflection.md) - both deleted along with "compliance_health" itself.
    // CaptureFixedHolisticSnapshotAsync immediately below is the surviving Phase 1 for the one
    // report type this file's remaining methods still exercise. Any method further down that
    // still reads SnapshotPath (the dynamic snapshot's file, as opposed to
    // FixedHolisticSnapshotPath) now depends on a snapshot.json this file can no longer produce
    // itself - this personal lab file (never wired into CI, never referenced elsewhere) was not
    // otherwise touched, so those methods are left as historical/manual-only, same as before.

    /// <summary>
    /// PHASE 1 for the FIXED holistic template (matches the real product UI - see
    /// FixedHolisticComposition's own doc comment). Same gather/fetch/score steps the deleted
    /// CaptureSnapshotAsync used, but skips ComposeActivity/ReflectOnCompositionActivity entirely -
    /// FixedHolisticComposition.Build() is deterministic, C#-only, always the same 6 blocks, so
    /// there is no LLM composition step to run or reflect on for this report type. Cheaper AND
    /// more reliable than the dynamic path: two LLM calls removed, and the "does the composition
    /// agent remember to include composite_score" reliability problem found live earlier today
    /// cannot happen here by construction.
    /// </summary>
    [Fact]
    public async Task CaptureFixedHolisticSnapshotAsync()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddInsightsData(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddTransient<GatherScopeActivity>();
        services.AddTransient<FetchDimensionsActivity>();
        services.AddTransient<NarrateActivity>();
        services.AddTransient<ReflectOnNarrativeActivity>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var gathered = await sp.GetRequiredService<GatherScopeActivity>().RunAsync(new GatherScopeInput(UserId, TenantId));
        output.WriteLine($"Scope gathered: {gathered.ScopePairs.Count} pairs, shape={gathered.TenantShape}");

        var dimensions = await sp.GetRequiredService<FetchDimensionsActivity>().RunAsync(new FetchDimensionsInput(UserId, TenantId));
        output.WriteLine($"Dimensions fetched: {dimensions.DimensionResults.Count}/10 succeeded, failed=[{string.Join(",", dimensions.FailedDimensions)}]");

        var scoreResult = ComputeScoreActivity.Run(new ComputeScoreInput(dimensions.DimensionResults));
        var dimensionResultsWithScore = new Dictionary<string, string>(dimensions.DimensionResults) { ["Score"] = scoreResult.ScoreDimensionResultJson };
        dimensions = dimensions with
        {
            DimensionResults = dimensionResultsWithScore,
            Assertions = dimensions.Assertions.Concat(scoreResult.Assertions).ToList(),
        };
        output.WriteLine($"Composite score: {scoreResult.OverallHealth.Score} ({scoreResult.OverallHealth.Band}), {scoreResult.Assertions.Count} component(s) scored");

        var plan = FixedHolisticComposition.Build();
        output.WriteLine($"Fixed composition: hero={plan.Hero.Block}, blocks=[{string.Join(",", plan.Blocks.Select(b => b.Block))}]");

        var narrateActivity = sp.GetRequiredService<NarrateActivity>();
        var reflectNarrativeActivity = sp.GetRequiredService<ReflectOnNarrativeActivity>();

        var narrateResult = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, null, null));
        var narrative = narrateResult.Narrative;
        for (var i = 0; i < MaxReviewIterations; i++)
        {
            var reflection = await reflectNarrativeActivity.RunAsync(new ReflectOnNarrativeInput(narrative, dimensions.Assertions, dimensions.Findings));
            if (reflection.Result.Verdict == ReflectionVerdict.Approve) break;
            var revised = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, narrative, reflection.Result.Issues));
            narrative = revised.Narrative;
        }
        output.WriteLine($"Narrative approved: {narrative.Blocks.Count} blocks");

        var publishGate = sp.GetRequiredService<PublishGate>();
        var gateResult = await publishGate.EvaluateAsync(UserId, TenantId, narrative, dimensions.Assertions);
        if (!gateResult.Approved)
            throw new InvalidOperationException($"Publish gate refused - snapshot would not be a realistic input for the render comparison: {string.Join("; ", gateResult.InternalDiagnostics)}");
        output.WriteLine("Publish gate: approved.");

        // [DELIBERATE, SCOPED EXCEPTION - see IReportHtmlAgent.RenderAsync's locationRows doc
        // comment] captured alongside the snapshot so a render-only run can reuse it without
        // re-fetching dimensions.
        var locationRows = dimensions.DimensionResults.TryGetValue("Location", out var locationJson)
            ? JsonSerializer.Deserialize<DimensionResult<LocationControlTotals, LocationRow>>(locationJson, SnapshotJsonOptions)?.Rows
            : null;

        var snapshot = new ReportSnapshot(
            TenantId, $"Tenant {TenantId}", ReportType, Period, gathered.TenantShape, DateTime.UtcNow,
            plan, narrative, dimensions.Assertions, dimensions.Findings, locationRows);

        Directory.CreateDirectory(LabRoot);
        await File.WriteAllTextAsync(FixedHolisticSnapshotPath, JsonSerializer.Serialize(snapshot, SnapshotJsonOptions));
        output.WriteLine($"Snapshot written to {FixedHolisticSnapshotPath} - reuse it for RenderFixedHolisticWithGpt56Terra below, no need to capture again.");
    }

    /// <summary>
    /// PRODUCTION-EQUIVALENT single-run generator - not a model comparison. Runs the exact same
    /// chain InsightsReportOrchestrator runs for a "fixed_holistic" report (gather -> fetch 14
    /// dimensions -> score -> deterministic composition -> narrate/reflect -> publish gate ->
    /// render -> inject font/coverage-grid/coverage-css/coverage-script -> normalize -> sanitize
    /// -> re-normalize -> structure gate -> advisory Playwright QA), using the SAME single
    /// API-key-authenticated IReportHtmlAgent production actually uses (AddInsightsPaidReportAgents)
    /// - no Entra ID, no az login, no multi-model comparison, no reviewer loop against the brand
    /// handoff. Built to answer "what does a real report look like right now" directly, reusing
    /// this file's own proven DI wiring rather than a fresh throwaway console app. Bounded retry
    /// (MaxReviewIterations+1 attempts) on a deterministic-gate refusal or a malformed/truncated
    /// render, same reasoning as RenderAndReviewAsync's own retry loop below - LLM output is not
    /// deterministic, one bad attempt does not mean the next one fails the same way. Never persists
    /// (no PersistActivity/blob/GeneratedReport row) - writes straight to a local file for manual
    /// inspection, same as every other method in this file.
    /// </summary>
    [Fact]
    public async Task GenerateFixedHolisticReportAsync()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddInsightsData(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddTransient<GatherScopeActivity>();
        services.AddTransient<FetchDimensionsActivity>();
        services.AddTransient<NarrateActivity>();
        services.AddTransient<ReflectOnNarrativeActivity>();
        services.AddTransient<InjectFontActivity>();
        services.AddTransient<InjectCoverageGridActivity>();
        services.AddTransient<InjectCoverageCssActivity>();
        services.AddTransient<InjectCoverageScriptActivity>();
        services.AddTransient<InjectBacklogAgeBarActivity>();
        services.AddTransient<InjectBacklogAgeBarCssActivity>();
        services.AddTransient<InjectForwardLookActivity>();
        services.AddTransient<InjectForwardLookCssActivity>();
        services.AddTransient<NormalizeActivity>();
        services.AddTransient<SanitizeActivity>();
        services.AddTransient<ValidateFixedHolisticStructureActivity>();
        services.AddTransient<PlaywrightQaActivity>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var gathered = await sp.GetRequiredService<GatherScopeActivity>().RunAsync(new GatherScopeInput(UserId, TenantId));
        output.WriteLine($"Scope gathered: {gathered.ScopePairs.Count} pairs, shape={gathered.TenantShape}");

        // [2026-09-10] Run the two windowed dimensions (TimelinessFY, EvidenceIntegrity) over the
        // LAST 90 DAYS rather than the current-FY-to-date default - the period-picker "Last 90
        // days" option, resolved to concrete dates the same way ReportPeriodResolver will in prod.
        var window = ReportPeriodResolver.Resolve(ReportPeriodChoice.Last90Days, DateTime.UtcNow);
        output.WriteLine($"Report window: {window.Label} [{window.StartInclusive:yyyy-MM-dd} .. {window.EndExclusive:yyyy-MM-dd})");

        var dimensions = await sp.GetRequiredService<FetchDimensionsActivity>().RunAsync(
            new FetchDimensionsInput(UserId, TenantId, WindowStart: window.StartInclusive, WindowEnd: window.EndExclusive));
        output.WriteLine($"Dimensions fetched: {dimensions.DimensionResults.Count}/15 succeeded, failed=[{string.Join(",", dimensions.FailedDimensions)}]");


        var scoreResult = ComputeScoreActivity.Run(new ComputeScoreInput(dimensions.DimensionResults));
        var dimensionResultsWithScore = new Dictionary<string, string>(dimensions.DimensionResults) { ["Score"] = scoreResult.ScoreDimensionResultJson };
        dimensions = dimensions with
        {
            DimensionResults = dimensionResultsWithScore,
            Assertions = dimensions.Assertions.Concat(scoreResult.Assertions).ToList(),
        };
        output.WriteLine($"Composite score: {scoreResult.OverallHealth.Score} ({scoreResult.OverallHealth.Band}), {scoreResult.Assertions.Count} component(s) scored");

        var plan = FixedHolisticComposition.Build();
        output.WriteLine($"Fixed composition: hero={plan.Hero.Block}, blocks=[{string.Join(",", plan.Blocks.Select(b => b.Block))}]");

        var narrateActivity = sp.GetRequiredService<NarrateActivity>();
        var reflectNarrativeActivity = sp.GetRequiredService<ReflectOnNarrativeActivity>();
        var narrateResult = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, null, null));
        var narrative = narrateResult.Narrative;
        var narrateTokens = narrateResult.TotalTokens;
        for (var i = 0; i < MaxReviewIterations; i++)
        {
            var reflection = await reflectNarrativeActivity.RunAsync(new ReflectOnNarrativeInput(narrative, dimensions.Assertions, dimensions.Findings));
            narrateTokens += reflection.TotalTokens;
            if (reflection.Result.Verdict == ReflectionVerdict.Approve) break;
            var revised = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, narrative, reflection.Result.Issues));
            narrateTokens += revised.TotalTokens;
            narrative = revised.Narrative;
        }
        output.WriteLine($"Narrative approved: {narrative.Blocks.Count} block(s), tokens={narrateTokens}");

        var publishGate = sp.GetRequiredService<PublishGate>();
        var gateResult = await publishGate.EvaluateAsync(UserId, TenantId, narrative, dimensions.Assertions);
        if (!gateResult.Approved)
            throw new InvalidOperationException($"Publish gate refused: {string.Join("; ", gateResult.InternalDiagnostics)}");
        output.WriteLine("Publish gate: approved.");

        var locationRows = dimensions.DimensionResults.TryGetValue("Location", out var locationJson)
            ? JsonSerializer.Deserialize<DimensionResult<LocationControlTotals, LocationRow>>(locationJson, SnapshotJsonOptions)?.Rows
            : null;
        var backlogAgingResult = dimensions.DimensionResults.TryGetValue("BacklogAging", out var backlogAgingJson)
            ? JsonSerializer.Deserialize<DimensionResult<BacklogAgingControlTotals, BacklogAgingRow>>(backlogAgingJson, SnapshotJsonOptions)
            : null;
        var forwardRiskResult = dimensions.DimensionResults.TryGetValue("ForwardRisk", out var forwardRiskJson)
            ? JsonSerializer.Deserialize<DimensionResult<ForwardRiskControlTotals, ForwardRiskRow>>(forwardRiskJson, SnapshotJsonOptions)
            : null;
        var forwardPipelineResult = dimensions.DimensionResults.TryGetValue("ForwardPipeline", out var forwardPipelineJson)
            ? JsonSerializer.Deserialize<DimensionResult<ForwardPipelineControlTotals, ForwardPipelineRow>>(forwardPipelineJson, SnapshotJsonOptions)
            : null;

        // [FIX] PaidReportAgentsRegistration no longer registers a plain IReportHtmlAgent
        // directly - since the "dimension_selection" report type needs its own render agent,
        // both are registered as one IReadOnlyDictionary<string, IReportHtmlAgent> keyed by
        // ReportType (see RenderHtmlActivity, the real production call site, already updated
        // to match). This lab test predates that change; resolve the same way.
        var htmlAgent = sp.GetRequiredService<IReadOnlyDictionary<string, IReportHtmlAgent>>()[FixedHolisticComposition.ReportType];
        var injectFontActivity = sp.GetRequiredService<InjectFontActivity>();
        var injectCoverageGridActivity = sp.GetRequiredService<InjectCoverageGridActivity>();
        var injectCoverageCssActivity = sp.GetRequiredService<InjectCoverageCssActivity>();
        var injectCoverageScriptActivity = sp.GetRequiredService<InjectCoverageScriptActivity>();
        var injectBacklogAgeBarActivity = sp.GetRequiredService<InjectBacklogAgeBarActivity>();
        var injectBacklogAgeBarCssActivity = sp.GetRequiredService<InjectBacklogAgeBarCssActivity>();
        var injectForwardLookActivity = sp.GetRequiredService<InjectForwardLookActivity>();
        var injectForwardLookCssActivity = sp.GetRequiredService<InjectForwardLookCssActivity>();
        var normalizeActivity = sp.GetRequiredService<NormalizeActivity>();
        var sanitizeActivity = sp.GetRequiredService<SanitizeActivity>();
        var structureActivity = sp.GetRequiredService<ValidateFixedHolisticStructureActivity>();
        var qaActivity = sp.GetRequiredService<PlaywrightQaActivity>();

        Directory.CreateDirectory(LabRoot);
        var htmlPath = Path.Combine(LabRoot, "production-fixed-holistic.html");

        var renderTokens = 0L;
        Exception? lastFailure = null;
        for (var attempt = 0; attempt <= MaxReviewIterations; attempt++)
        {
            try
            {
                var renderResult = await htmlAgent.RenderAsync(
                    plan, narrative, dimensions.Assertions, $"Tenant {TenantId}", FixedHolisticComposition.ReportType, DateTime.UtcNow, locationRows);
                renderTokens += renderResult.TotalTokens;
                var html = PartialDimensionPlaceholder.InsertPlaceholders(renderResult.Value, dimensions.FailedDimensions);

                var fonted = await injectFontActivity.RunAsync(new InjectFontInput(html));
                var coverageGridded = await injectCoverageGridActivity.RunAsync(new InjectCoverageGridInput(fonted.Html, locationRows));
                var coverageStyled = await injectCoverageCssActivity.RunAsync(new InjectCoverageCssInput(coverageGridded.Html));
                var coverageScripted = await injectCoverageScriptActivity.RunAsync(new InjectCoverageScriptInput(coverageStyled.Html));
                var agebarred = await injectBacklogAgeBarActivity.RunAsync(new InjectBacklogAgeBarInput(coverageScripted.Html, backlogAgingResult?.Rows, backlogAgingResult?.ControlTotals));
                var agebarStyled = await injectBacklogAgeBarCssActivity.RunAsync(new InjectBacklogAgeBarCssInput(agebarred.Html));
                var forwarded = await injectForwardLookActivity.RunAsync(new InjectForwardLookInput(
                    agebarStyled.Html, forwardRiskResult?.ControlTotals,
                    forwardPipelineResult?.ControlTotals, forwardPipelineResult?.Rows));
                var forwardStyled = await injectForwardLookCssActivity.RunAsync(new InjectForwardLookCssInput(forwarded.Html));
                var normalized = await normalizeActivity.RunAsync(new NormalizeInput(forwardStyled.Html));
                var sanitized = await sanitizeActivity.RunAsync(new SanitizeInput(normalized.Html));
                var reNormalized = await normalizeActivity.RunAsync(new NormalizeInput(sanitized.Html));
                var structureChecked = await structureActivity.RunAsync(new ValidateFixedHolisticStructureInput(reNormalized.Html, FixedHolisticComposition.ReportType));

                await File.WriteAllTextAsync(htmlPath, structureChecked.Html);
                output.WriteLine($"Report written to {htmlPath} - attempt {attempt}, renderTokens={renderTokens}, totalTokens={narrateTokens + renderTokens}");

                var qaResult = await qaActivity.RunAsync(new PlaywrightQaInput(structureChecked.Html));
                output.WriteLine($"Playwright QA (advisory only): hasIssues={qaResult.Result.HasIssues}, consoleErrors={qaResult.Result.ConsoleErrors.Count}, horizontalOverflow={qaResult.Result.HasHorizontalOverflow}");
                return;
            }
            catch (OrchestrationRefusedException ex)
            {
                lastFailure = ex;
                output.WriteLine($"Attempt {attempt}: {ex.ReasonCode} refused - {string.Join("; ", ex.InternalDiagnostics)}");
            }
            catch (InvalidOperationException ex)
            {
                // Same shape as InjectFontActivity's one throw (missing/malformed <head>) - a
                // recoverable, non-deterministic LLM failure, not a bug in this loop.
                lastFailure = ex;
                output.WriteLine($"Attempt {attempt}: render/inject failed - {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Every attempt (0..{MaxReviewIterations}) failed the deterministic gate chain. Last failure: {lastFailure?.Message}", lastFailure);
    }

    /// <summary>
    /// [TEMPORARY, NOT COMMITTED - user asked to look, not to keep] Real end-to-end run of the
    /// new Trent-styled dimension_selection prompt against real UAT tenant 29, single dimension
    /// (Location, the one directly comparable to trent/02 - Location.html). Mirrors
    /// GenerateFixedHolisticReportAsync's real pipeline shape immediately above, with the
    /// dimension_selection-specific differences: no ComputeScoreActivity (this report type never
    /// computes one), DimensionSelectionComposition.Build instead of FixedHolisticComposition.Build,
    /// the "dimension_selection" key resolved from the render-agent dictionary, and the real
    /// dimensionRowsJson construction the orchestrator itself does (InsightsReportOrchestrator.cs).
    /// </summary>
    [Fact]
    public async Task GenerateDimensionSelectionLocationReportAsync()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddInsightsData(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddTransient<GatherScopeActivity>();
        services.AddTransient<FetchDimensionsActivity>();
        services.AddTransient<NarrateActivity>();
        services.AddTransient<ReflectOnNarrativeActivity>();
        services.AddTransient<InjectFontActivity>();
        services.AddTransient<InjectCoverageGridActivity>();
        services.AddTransient<InjectCoverageCssActivity>();
        services.AddTransient<InjectCoverageScriptActivity>();
        services.AddTransient<NormalizeActivity>();
        services.AddTransient<SanitizeActivity>();
        services.AddTransient<ValidateFixedHolisticStructureActivity>();
        services.AddTransient<PlaywrightQaActivity>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        IReadOnlyList<string> requestedDimensions = ["Location"];

        var gathered = await sp.GetRequiredService<GatherScopeActivity>().RunAsync(new GatherScopeInput(UserId, TenantId));
        output.WriteLine($"Scope gathered: {gathered.ScopePairs.Count} pairs, shape={gathered.TenantShape}");

        var dimensions = await sp.GetRequiredService<FetchDimensionsActivity>().RunAsync(new FetchDimensionsInput(UserId, TenantId, requestedDimensions));
        output.WriteLine($"Dimensions fetched: {dimensions.DimensionResults.Count}/{requestedDimensions.Count} succeeded, failed=[{string.Join(",", dimensions.FailedDimensions)}]");

        // No ComputeScoreActivity - dimension_selection never computes a composite score
        // (InsightsReportOrchestrator's own real branch skips it for this ReportType).

        var plan = DimensionSelectionComposition.Build(requestedDimensions);
        output.WriteLine($"Dimension-selection composition: hero={plan.Hero.Block}, blocks=[{string.Join(",", plan.Blocks.Select(b => b.Block))}]");

        var narrateActivity = sp.GetRequiredService<NarrateActivity>();
        var reflectNarrativeActivity = sp.GetRequiredService<ReflectOnNarrativeActivity>();
        var narrateResult = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, null, null));
        var narrative = narrateResult.Narrative;
        var narrateTokens = narrateResult.TotalTokens;
        for (var i = 0; i < MaxReviewIterations; i++)
        {
            var reflection = await reflectNarrativeActivity.RunAsync(new ReflectOnNarrativeInput(narrative, dimensions.Assertions, dimensions.Findings));
            narrateTokens += reflection.TotalTokens;
            if (reflection.Result.Verdict == ReflectionVerdict.Approve) break;
            var revised = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, narrative, reflection.Result.Issues));
            narrateTokens += revised.TotalTokens;
            narrative = revised.Narrative;
        }
        output.WriteLine($"Narrative approved: {narrative.Blocks.Count} block(s), tokens={narrateTokens}");

        var publishGate = sp.GetRequiredService<PublishGate>();
        var gateResult = await publishGate.EvaluateAsync(UserId, TenantId, narrative, dimensions.Assertions);
        if (!gateResult.Approved)
            throw new InvalidOperationException($"Publish gate refused: {string.Join("; ", gateResult.InternalDiagnostics)}");
        output.WriteLine("Publish gate: approved.");

        var locationRows = dimensions.DimensionResults.TryGetValue("Location", out var locationJson)
            ? JsonSerializer.Deserialize<DimensionResult<LocationControlTotals, LocationRow>>(locationJson, SnapshotJsonOptions)?.Rows
            : null;

        // Real orchestrator logic (InsightsReportOrchestrator.cs), replicated exactly - the raw
        // "Rows" JSON array per requested dimension, not just the curated assertions.
        var dimensionRowsJson = dimensions.DimensionResults
            .Where(kv => requestedDimensions.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(kv.Value).RootElement.GetProperty("Rows").GetRawText());
        var dimensionControlTotalsJson = dimensions.DimensionResults
            .Where(kv => requestedDimensions.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(kv.Value).RootElement.GetProperty("ControlTotals").GetRawText());

        // [FIX 2026-09-09] Was hardcoded to the generic "dimension_selection" key - written
        // before Location got its own "dimension_selection:Location" specific-key registration
        // (Sambram's approved dimension-view rewrite), so this test kept silently exercising the
        // old Trent-based generic prompt instead of the real specific-key path RenderHtmlActivity
        // itself uses in production. Same specific-key-first lookup the Users/Departments test
        // methods already use.
        var htmlAgentsForLocation = sp.GetRequiredService<IReadOnlyDictionary<string, IReportHtmlAgent>>();
        var locationSpecificKey = $"{DimensionSelectionComposition.ReportType}:{plan.Blocks[0].Block}";
        var htmlAgent = htmlAgentsForLocation.TryGetValue(locationSpecificKey, out var locationSpecificAgent) ? locationSpecificAgent : htmlAgentsForLocation[DimensionSelectionComposition.ReportType];
        output.WriteLine($"Render agent key used: {(htmlAgentsForLocation.ContainsKey(locationSpecificKey) ? locationSpecificKey : DimensionSelectionComposition.ReportType)}");
        var injectFontActivity = sp.GetRequiredService<InjectFontActivity>();
        var injectCoverageGridActivity = sp.GetRequiredService<InjectCoverageGridActivity>();
        var injectCoverageCssActivity = sp.GetRequiredService<InjectCoverageCssActivity>();
        var injectCoverageScriptActivity = sp.GetRequiredService<InjectCoverageScriptActivity>();
        var normalizeActivity = sp.GetRequiredService<NormalizeActivity>();
        var sanitizeActivity = sp.GetRequiredService<SanitizeActivity>();
        var structureActivity = sp.GetRequiredService<ValidateFixedHolisticStructureActivity>();
        var qaActivity = sp.GetRequiredService<PlaywrightQaActivity>();

        Directory.CreateDirectory(LabRoot);
        var htmlPath = Path.Combine(LabRoot, "dimension-selection-location.html");

        var renderTokens = 0L;
        Exception? lastFailure = null;
        for (var attempt = 0; attempt <= MaxReviewIterations; attempt++)
        {
            try
            {
                var renderResult = await htmlAgent.RenderAsync(
                    plan, narrative, dimensions.Assertions, $"Tenant {TenantId}", DimensionSelectionComposition.ReportType, DateTime.UtcNow, locationRows, dimensionRowsJson, dimensionControlTotalsJson);
                renderTokens += renderResult.TotalTokens;
                var html = PartialDimensionPlaceholder.InsertPlaceholders(renderResult.Value, dimensions.FailedDimensions);

                var fonted = await injectFontActivity.RunAsync(new InjectFontInput(html));
                var coverageGridded = await injectCoverageGridActivity.RunAsync(new InjectCoverageGridInput(fonted.Html, locationRows));
                var coverageStyled = await injectCoverageCssActivity.RunAsync(new InjectCoverageCssInput(coverageGridded.Html));
                var coverageScripted = await injectCoverageScriptActivity.RunAsync(new InjectCoverageScriptInput(coverageStyled.Html));
                var normalized = await normalizeActivity.RunAsync(new NormalizeInput(coverageScripted.Html));
                var sanitized = await sanitizeActivity.RunAsync(new SanitizeInput(normalized.Html));
                var reNormalized = await normalizeActivity.RunAsync(new NormalizeInput(sanitized.Html));
                var structureChecked = await structureActivity.RunAsync(new ValidateFixedHolisticStructureInput(reNormalized.Html, DimensionSelectionComposition.ReportType));

                await File.WriteAllTextAsync(htmlPath, structureChecked.Html);
                output.WriteLine($"Report written to {htmlPath} - attempt {attempt}, renderTokens={renderTokens}, totalTokens={narrateTokens + renderTokens}");

                var qaResult = await qaActivity.RunAsync(new PlaywrightQaInput(structureChecked.Html));
                output.WriteLine($"Playwright QA (advisory only): hasIssues={qaResult.Result.HasIssues}, consoleErrors={qaResult.Result.ConsoleErrors.Count}, horizontalOverflow={qaResult.Result.HasHorizontalOverflow}");
                return;
            }
            catch (OrchestrationRefusedException ex)
            {
                lastFailure = ex;
                output.WriteLine($"Attempt {attempt}: {ex.ReasonCode} refused - {string.Join("; ", ex.InternalDiagnostics)}");
            }
            catch (InvalidOperationException ex)
            {
                lastFailure = ex;
                output.WriteLine($"Attempt {attempt}: render/inject failed - {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Every attempt (0..{MaxReviewIterations}) failed the deterministic gate chain. Last failure: {lastFailure?.Message}", lastFailure);
    }

    // [DELETED 2026-09-11] GenerateDimensionSelectionWithEntityReportAsync - exercised the
    // Entity-mixed render path (DimensionSelectionComposition.EntityMixedRenderKey), reverted the
    // same day per final product direction: a real "Generate" click naming several dimensions now
    // produces one independent report PER dimension (fanned out in RunEndpoints.cs), never one
    // combined document. Entity picked with others now just means two ordinary single-dimension
    // runs happen - GenerateFixedHolisticReportAsync above already covers Entity's own shape.

    /// <summary>
    /// [TEMPORARY, NOT COMMITTED] Real end-to-end run of the new dimension-specific "Users"
    /// render prompt (05_report_html_dimension_selection_user.md, the real .di-/Poppins "By User
    /// & Role" page reproduction) against real UAT tenant 29. Same shape as
    /// GenerateDimensionSelectionLocationReportAsync above, except the render agent is resolved
    /// from the SPECIFIC "dimension_selection:Users" key (PaidReportAgentsRegistration.cs) rather
    /// than the generic "dimension_selection" key - this test bypasses RenderHtmlActivity itself
    /// (same as the Location test above), so it has to replicate that activity's own
    /// specific-key-first lookup manually.
    /// </summary>
    [Fact]
    public async Task GenerateDimensionSelectionUsersReportAsync()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddInsightsData(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddTransient<GatherScopeActivity>();
        services.AddTransient<FetchDimensionsActivity>();
        services.AddTransient<NarrateActivity>();
        services.AddTransient<ReflectOnNarrativeActivity>();
        services.AddTransient<InjectFontActivity>();
        services.AddTransient<InjectCoverageGridActivity>();
        services.AddTransient<InjectCoverageCssActivity>();
        services.AddTransient<InjectCoverageScriptActivity>();
        services.AddTransient<NormalizeActivity>();
        services.AddTransient<SanitizeActivity>();
        services.AddTransient<ValidateFixedHolisticStructureActivity>();
        services.AddTransient<PlaywrightQaActivity>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        IReadOnlyList<string> requestedDimensions = ["Users"];

        var gathered = await sp.GetRequiredService<GatherScopeActivity>().RunAsync(new GatherScopeInput(UserId, TenantId));
        output.WriteLine($"Scope gathered: {gathered.ScopePairs.Count} pairs, shape={gathered.TenantShape}");

        var dimensions = await sp.GetRequiredService<FetchDimensionsActivity>().RunAsync(new FetchDimensionsInput(UserId, TenantId, requestedDimensions));
        output.WriteLine($"Dimensions fetched: {dimensions.DimensionResults.Count}/{requestedDimensions.Count} succeeded, failed=[{string.Join(",", dimensions.FailedDimensions)}]");

        var plan = DimensionSelectionComposition.Build(requestedDimensions);
        output.WriteLine($"Dimension-selection composition: hero={plan.Hero.Block}, blocks=[{string.Join(",", plan.Blocks.Select(b => b.Block))}]");

        var narrateActivity = sp.GetRequiredService<NarrateActivity>();
        var reflectNarrativeActivity = sp.GetRequiredService<ReflectOnNarrativeActivity>();
        var narrateResult = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, null, null));
        var narrative = narrateResult.Narrative;
        var narrateTokens = narrateResult.TotalTokens;
        for (var i = 0; i < MaxReviewIterations; i++)
        {
            var reflection = await reflectNarrativeActivity.RunAsync(new ReflectOnNarrativeInput(narrative, dimensions.Assertions, dimensions.Findings));
            narrateTokens += reflection.TotalTokens;
            if (reflection.Result.Verdict == ReflectionVerdict.Approve) break;
            var revised = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, narrative, reflection.Result.Issues));
            narrateTokens += revised.TotalTokens;
            narrative = revised.Narrative;
        }
        output.WriteLine($"Narrative approved: {narrative.Blocks.Count} block(s), tokens={narrateTokens}");

        var publishGate = sp.GetRequiredService<PublishGate>();
        var gateResult = await publishGate.EvaluateAsync(UserId, TenantId, narrative, dimensions.Assertions);
        if (!gateResult.Approved)
            throw new InvalidOperationException($"Publish gate refused: {string.Join("; ", gateResult.InternalDiagnostics)}");
        output.WriteLine("Publish gate: approved.");

        var dimensionRowsJson = dimensions.DimensionResults
            .Where(kv => requestedDimensions.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(kv.Value).RootElement.GetProperty("Rows").GetRawText());
        var dimensionControlTotalsJson = dimensions.DimensionResults
            .Where(kv => requestedDimensions.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(kv.Value).RootElement.GetProperty("ControlTotals").GetRawText());

        // Specific-key-first, same rule RenderHtmlActivity itself applies (Plan.Blocks.Count == 1
        // -> try "{ReportType}:{Block}" before falling back to the plain ReportType key).
        var htmlAgents = sp.GetRequiredService<IReadOnlyDictionary<string, IReportHtmlAgent>>();
        var specificKey = $"{DimensionSelectionComposition.ReportType}:{plan.Blocks[0].Block}";
        var htmlAgent = htmlAgents.TryGetValue(specificKey, out var specific) ? specific : htmlAgents[DimensionSelectionComposition.ReportType];
        output.WriteLine($"Render agent key used: {(htmlAgents.ContainsKey(specificKey) ? specificKey : DimensionSelectionComposition.ReportType)}");

        var injectFontActivity = sp.GetRequiredService<InjectFontActivity>();
        var injectCoverageGridActivity = sp.GetRequiredService<InjectCoverageGridActivity>();
        var injectCoverageCssActivity = sp.GetRequiredService<InjectCoverageCssActivity>();
        var injectCoverageScriptActivity = sp.GetRequiredService<InjectCoverageScriptActivity>();
        var normalizeActivity = sp.GetRequiredService<NormalizeActivity>();
        var sanitizeActivity = sp.GetRequiredService<SanitizeActivity>();
        var structureActivity = sp.GetRequiredService<ValidateFixedHolisticStructureActivity>();
        var qaActivity = sp.GetRequiredService<PlaywrightQaActivity>();

        Directory.CreateDirectory(LabRoot);
        var htmlPath = Path.Combine(LabRoot, "dimension-selection-users.html");

        var renderTokens = 0L;
        Exception? lastFailure = null;
        for (var attempt = 0; attempt <= MaxReviewIterations; attempt++)
        {
            try
            {
                var renderResult = await htmlAgent.RenderAsync(
                    plan, narrative, dimensions.Assertions, $"Tenant {TenantId}", DimensionSelectionComposition.ReportType, DateTime.UtcNow, null, dimensionRowsJson, dimensionControlTotalsJson);
                renderTokens += renderResult.TotalTokens;
                var html = PartialDimensionPlaceholder.InsertPlaceholders(renderResult.Value, dimensions.FailedDimensions);

                var fonted = await injectFontActivity.RunAsync(new InjectFontInput(html));
                var coverageGridded = await injectCoverageGridActivity.RunAsync(new InjectCoverageGridInput(fonted.Html, null));
                var coverageStyled = await injectCoverageCssActivity.RunAsync(new InjectCoverageCssInput(coverageGridded.Html));
                var coverageScripted = await injectCoverageScriptActivity.RunAsync(new InjectCoverageScriptInput(coverageStyled.Html));
                var normalized = await normalizeActivity.RunAsync(new NormalizeInput(coverageScripted.Html));
                var sanitized = await sanitizeActivity.RunAsync(new SanitizeInput(normalized.Html));
                var reNormalized = await normalizeActivity.RunAsync(new NormalizeInput(sanitized.Html));
                var structureChecked = await structureActivity.RunAsync(new ValidateFixedHolisticStructureInput(reNormalized.Html, DimensionSelectionComposition.ReportType));

                await File.WriteAllTextAsync(htmlPath, structureChecked.Html);
                output.WriteLine($"Report written to {htmlPath} - attempt {attempt}, renderTokens={renderTokens}, totalTokens={narrateTokens + renderTokens}");

                var qaResult = await qaActivity.RunAsync(new PlaywrightQaInput(structureChecked.Html));
                output.WriteLine($"Playwright QA (advisory only): hasIssues={qaResult.Result.HasIssues}, consoleErrors={qaResult.Result.ConsoleErrors.Count}, horizontalOverflow={qaResult.Result.HasHorizontalOverflow}");
                return;
            }
            catch (OrchestrationRefusedException ex)
            {
                lastFailure = ex;
                output.WriteLine($"Attempt {attempt}: {ex.ReasonCode} refused - {string.Join("; ", ex.InternalDiagnostics)}");
            }
            catch (InvalidOperationException ex)
            {
                lastFailure = ex;
                output.WriteLine($"Attempt {attempt}: render/inject failed - {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Every attempt (0..{MaxReviewIterations}) failed the deterministic gate chain. Last failure: {lastFailure?.Message}", lastFailure);
    }

    /// <summary>
    /// [TEMPORARY, NOT COMMITTED] Real end-to-end run of the new dimension-specific
    /// "Departments" render prompt (05_report_html_dimension_selection_department.md) against
    /// real UAT tenant 29. Same shape as GenerateDimensionSelectionUsersReportAsync above,
    /// resolved from the "dimension_selection:Departments" specific key.
    /// </summary>
    [Fact]
    public async Task GenerateDimensionSelectionDepartmentsReportAsync()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddInsightsData(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddTransient<GatherScopeActivity>();
        services.AddTransient<FetchDimensionsActivity>();
        services.AddTransient<NarrateActivity>();
        services.AddTransient<ReflectOnNarrativeActivity>();
        services.AddTransient<InjectFontActivity>();
        services.AddTransient<InjectCoverageGridActivity>();
        services.AddTransient<InjectCoverageCssActivity>();
        services.AddTransient<InjectCoverageScriptActivity>();
        services.AddTransient<NormalizeActivity>();
        services.AddTransient<SanitizeActivity>();
        services.AddTransient<ValidateFixedHolisticStructureActivity>();
        services.AddTransient<PlaywrightQaActivity>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        IReadOnlyList<string> requestedDimensions = ["Departments"];

        var gathered = await sp.GetRequiredService<GatherScopeActivity>().RunAsync(new GatherScopeInput(UserId, TenantId));
        output.WriteLine($"Scope gathered: {gathered.ScopePairs.Count} pairs, shape={gathered.TenantShape}");

        var dimensions = await sp.GetRequiredService<FetchDimensionsActivity>().RunAsync(new FetchDimensionsInput(UserId, TenantId, requestedDimensions));
        output.WriteLine($"Dimensions fetched: {dimensions.DimensionResults.Count}/{requestedDimensions.Count} succeeded, failed=[{string.Join(",", dimensions.FailedDimensions)}]");

        var plan = DimensionSelectionComposition.Build(requestedDimensions);
        output.WriteLine($"Dimension-selection composition: hero={plan.Hero.Block}, blocks=[{string.Join(",", plan.Blocks.Select(b => b.Block))}]");

        var narrateActivity = sp.GetRequiredService<NarrateActivity>();
        var reflectNarrativeActivity = sp.GetRequiredService<ReflectOnNarrativeActivity>();
        var narrateResult = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, null, null));
        var narrative = narrateResult.Narrative;
        var narrateTokens = narrateResult.TotalTokens;
        for (var i = 0; i < MaxReviewIterations; i++)
        {
            var reflection = await reflectNarrativeActivity.RunAsync(new ReflectOnNarrativeInput(narrative, dimensions.Assertions, dimensions.Findings));
            narrateTokens += reflection.TotalTokens;
            if (reflection.Result.Verdict == ReflectionVerdict.Approve) break;
            var revised = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, narrative, reflection.Result.Issues));
            narrateTokens += revised.TotalTokens;
            narrative = revised.Narrative;
        }
        output.WriteLine($"Narrative approved: {narrative.Blocks.Count} block(s), tokens={narrateTokens}");

        var publishGate = sp.GetRequiredService<PublishGate>();
        var gateResult = await publishGate.EvaluateAsync(UserId, TenantId, narrative, dimensions.Assertions);
        if (!gateResult.Approved)
            throw new InvalidOperationException($"Publish gate refused: {string.Join("; ", gateResult.InternalDiagnostics)}");
        output.WriteLine("Publish gate: approved.");

        var dimensionRowsJson = dimensions.DimensionResults
            .Where(kv => requestedDimensions.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(kv.Value).RootElement.GetProperty("Rows").GetRawText());
        var dimensionControlTotalsJson = dimensions.DimensionResults
            .Where(kv => requestedDimensions.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(kv.Value).RootElement.GetProperty("ControlTotals").GetRawText());

        var htmlAgents = sp.GetRequiredService<IReadOnlyDictionary<string, IReportHtmlAgent>>();
        var specificKey = $"{DimensionSelectionComposition.ReportType}:{plan.Blocks[0].Block}";
        var htmlAgent = htmlAgents.TryGetValue(specificKey, out var specific) ? specific : htmlAgents[DimensionSelectionComposition.ReportType];
        output.WriteLine($"Render agent key used: {(htmlAgents.ContainsKey(specificKey) ? specificKey : DimensionSelectionComposition.ReportType)}");

        var injectFontActivity = sp.GetRequiredService<InjectFontActivity>();
        var injectCoverageGridActivity = sp.GetRequiredService<InjectCoverageGridActivity>();
        var injectCoverageCssActivity = sp.GetRequiredService<InjectCoverageCssActivity>();
        var injectCoverageScriptActivity = sp.GetRequiredService<InjectCoverageScriptActivity>();
        var normalizeActivity = sp.GetRequiredService<NormalizeActivity>();
        var sanitizeActivity = sp.GetRequiredService<SanitizeActivity>();
        var structureActivity = sp.GetRequiredService<ValidateFixedHolisticStructureActivity>();
        var qaActivity = sp.GetRequiredService<PlaywrightQaActivity>();

        Directory.CreateDirectory(LabRoot);
        var htmlPath = Path.Combine(LabRoot, "dimension-selection-departments.html");

        var renderTokens = 0L;
        Exception? lastFailure = null;
        for (var attempt = 0; attempt <= MaxReviewIterations; attempt++)
        {
            try
            {
                var renderResult = await htmlAgent.RenderAsync(
                    plan, narrative, dimensions.Assertions, $"Tenant {TenantId}", DimensionSelectionComposition.ReportType, DateTime.UtcNow, null, dimensionRowsJson, dimensionControlTotalsJson);
                renderTokens += renderResult.TotalTokens;
                var html = PartialDimensionPlaceholder.InsertPlaceholders(renderResult.Value, dimensions.FailedDimensions);

                var fonted = await injectFontActivity.RunAsync(new InjectFontInput(html));
                var coverageGridded = await injectCoverageGridActivity.RunAsync(new InjectCoverageGridInput(fonted.Html, null));
                var coverageStyled = await injectCoverageCssActivity.RunAsync(new InjectCoverageCssInput(coverageGridded.Html));
                var coverageScripted = await injectCoverageScriptActivity.RunAsync(new InjectCoverageScriptInput(coverageStyled.Html));
                var normalized = await normalizeActivity.RunAsync(new NormalizeInput(coverageScripted.Html));
                var sanitized = await sanitizeActivity.RunAsync(new SanitizeInput(normalized.Html));
                var reNormalized = await normalizeActivity.RunAsync(new NormalizeInput(sanitized.Html));
                var structureChecked = await structureActivity.RunAsync(new ValidateFixedHolisticStructureInput(reNormalized.Html, DimensionSelectionComposition.ReportType));

                await File.WriteAllTextAsync(htmlPath, structureChecked.Html);
                output.WriteLine($"Report written to {htmlPath} - attempt {attempt}, renderTokens={renderTokens}, totalTokens={narrateTokens + renderTokens}");

                var qaResult = await qaActivity.RunAsync(new PlaywrightQaInput(structureChecked.Html));
                output.WriteLine($"Playwright QA (advisory only): hasIssues={qaResult.Result.HasIssues}, consoleErrors={qaResult.Result.ConsoleErrors.Count}, horizontalOverflow={qaResult.Result.HasHorizontalOverflow}");
                return;
            }
            catch (OrchestrationRefusedException ex)
            {
                lastFailure = ex;
                output.WriteLine($"Attempt {attempt}: {ex.ReasonCode} refused - {string.Join("; ", ex.InternalDiagnostics)}");
            }
            catch (InvalidOperationException ex)
            {
                lastFailure = ex;
                output.WriteLine($"Attempt {attempt}: render/inject failed - {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Every attempt (0..{MaxReviewIterations}) failed the deterministic gate chain. Last failure: {lastFailure?.Message}", lastFailure);
    }

    /// <summary>
    /// [ADDED 2026-09-10] One parameterised live run for the three NEW dimension-specific views
    /// built the same day (BacklogAging / Act / Licence - Sambram's single-section system, content
    /// mimicked from Trent's 03/05/06). Same pipeline shape as
    /// GenerateDimensionSelectionDepartmentsReportAsync above; the render agent resolves from the
    /// "dimension_selection:{Name}" specific key. Real UAT tenant 29. Not part of CI - run with
    /// --filter and ConnectionStrings__RegTrack + MAF_* set.
    /// </summary>
    [Theory]
    [InlineData("BacklogAging", "dimension-selection-backlogaging.html")]
    [InlineData("Act", "dimension-selection-act.html")]
    [InlineData("Licence", "dimension-selection-licence.html")]
    public async Task GenerateDimensionSelectionReportAsync(string dimensionName, string outputFile)
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddInsightsData(configuration);
        services.AddInsightsWorker();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddTransient<GatherScopeActivity>();
        services.AddTransient<FetchDimensionsActivity>();
        services.AddTransient<NarrateActivity>();
        services.AddTransient<ReflectOnNarrativeActivity>();
        services.AddTransient<InjectFontActivity>();
        services.AddTransient<InjectCoverageGridActivity>();
        services.AddTransient<InjectCoverageCssActivity>();
        services.AddTransient<InjectCoverageScriptActivity>();
        services.AddTransient<NormalizeActivity>();
        services.AddTransient<SanitizeActivity>();
        services.AddTransient<ValidateFixedHolisticStructureActivity>();
        services.AddTransient<PlaywrightQaActivity>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        IReadOnlyList<string> requestedDimensions = [dimensionName];

        var gathered = await sp.GetRequiredService<GatherScopeActivity>().RunAsync(new GatherScopeInput(UserId, TenantId));
        output.WriteLine($"Scope gathered: {gathered.ScopePairs.Count} pairs, shape={gathered.TenantShape}");

        var dimensions = await sp.GetRequiredService<FetchDimensionsActivity>().RunAsync(new FetchDimensionsInput(UserId, TenantId, requestedDimensions));
        output.WriteLine($"Dimensions fetched: {dimensions.DimensionResults.Count}/{requestedDimensions.Count} succeeded, failed=[{string.Join(",", dimensions.FailedDimensions)}]");
        if (!dimensions.DimensionResults.ContainsKey(dimensionName))
            throw new InvalidOperationException($"{dimensionName} did not return - failed=[{string.Join(",", dimensions.FailedDimensions)}]. Cannot render a view with no data.");

        var plan = DimensionSelectionComposition.Build(requestedDimensions);
        output.WriteLine($"Dimension-selection composition: hero={plan.Hero.Block}, blocks=[{string.Join(",", plan.Blocks.Select(b => b.Block))}]");

        var narrateActivity = sp.GetRequiredService<NarrateActivity>();
        var reflectNarrativeActivity = sp.GetRequiredService<ReflectOnNarrativeActivity>();
        var narrateResult = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, null, null));
        var narrative = narrateResult.Narrative;
        var narrateTokens = narrateResult.TotalTokens;
        for (var i = 0; i < MaxReviewIterations; i++)
        {
            var reflection = await reflectNarrativeActivity.RunAsync(new ReflectOnNarrativeInput(narrative, dimensions.Assertions, dimensions.Findings));
            narrateTokens += reflection.TotalTokens;
            if (reflection.Result.Verdict == ReflectionVerdict.Approve) break;
            var revised = await narrateActivity.RunAsync(new NarrateInput(plan, dimensions.Assertions, dimensions.Findings, narrative, reflection.Result.Issues));
            narrateTokens += revised.TotalTokens;
            narrative = revised.Narrative;
        }
        output.WriteLine($"Narrative approved: {narrative.Blocks.Count} block(s), tokens={narrateTokens}");

        var publishGate = sp.GetRequiredService<PublishGate>();
        var gateResult = await publishGate.EvaluateAsync(UserId, TenantId, narrative, dimensions.Assertions);
        if (!gateResult.Approved)
            throw new InvalidOperationException($"Publish gate refused: {string.Join("; ", gateResult.InternalDiagnostics)}");
        output.WriteLine("Publish gate: approved.");

        var dimensionRowsJson = dimensions.DimensionResults
            .Where(kv => requestedDimensions.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(kv.Value).RootElement.GetProperty("Rows").GetRawText());
        var dimensionControlTotalsJson = dimensions.DimensionResults
            .Where(kv => requestedDimensions.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(kv.Value).RootElement.GetProperty("ControlTotals").GetRawText());

        var htmlAgents = sp.GetRequiredService<IReadOnlyDictionary<string, IReportHtmlAgent>>();
        var specificKey = $"{DimensionSelectionComposition.ReportType}:{plan.Blocks[0].Block}";
        var htmlAgent = htmlAgents.TryGetValue(specificKey, out var specific) ? specific : htmlAgents[DimensionSelectionComposition.ReportType];
        output.WriteLine($"Render agent key used: {(htmlAgents.ContainsKey(specificKey) ? specificKey : DimensionSelectionComposition.ReportType)}");

        var injectFontActivity = sp.GetRequiredService<InjectFontActivity>();
        var injectCoverageGridActivity = sp.GetRequiredService<InjectCoverageGridActivity>();
        var injectCoverageCssActivity = sp.GetRequiredService<InjectCoverageCssActivity>();
        var injectCoverageScriptActivity = sp.GetRequiredService<InjectCoverageScriptActivity>();
        var normalizeActivity = sp.GetRequiredService<NormalizeActivity>();
        var sanitizeActivity = sp.GetRequiredService<SanitizeActivity>();
        var structureActivity = sp.GetRequiredService<ValidateFixedHolisticStructureActivity>();
        var qaActivity = sp.GetRequiredService<PlaywrightQaActivity>();

        Directory.CreateDirectory(LabRoot);
        var htmlPath = Path.Combine(LabRoot, outputFile);

        var renderTokens = 0L;
        Exception? lastFailure = null;
        for (var attempt = 0; attempt <= MaxReviewIterations; attempt++)
        {
            try
            {
                var renderResult = await htmlAgent.RenderAsync(
                    plan, narrative, dimensions.Assertions, $"Tenant {TenantId}", DimensionSelectionComposition.ReportType, DateTime.UtcNow, null, dimensionRowsJson, dimensionControlTotalsJson);
                renderTokens += renderResult.TotalTokens;
                var html = PartialDimensionPlaceholder.InsertPlaceholders(renderResult.Value, dimensions.FailedDimensions);

                var fonted = await injectFontActivity.RunAsync(new InjectFontInput(html));
                var coverageGridded = await injectCoverageGridActivity.RunAsync(new InjectCoverageGridInput(fonted.Html, null));
                var coverageStyled = await injectCoverageCssActivity.RunAsync(new InjectCoverageCssInput(coverageGridded.Html));
                var coverageScripted = await injectCoverageScriptActivity.RunAsync(new InjectCoverageScriptInput(coverageStyled.Html));
                var normalized = await normalizeActivity.RunAsync(new NormalizeInput(coverageScripted.Html));
                var sanitized = await sanitizeActivity.RunAsync(new SanitizeInput(normalized.Html));
                var reNormalized = await normalizeActivity.RunAsync(new NormalizeInput(sanitized.Html));
                var structureChecked = await structureActivity.RunAsync(new ValidateFixedHolisticStructureInput(reNormalized.Html, DimensionSelectionComposition.ReportType));

                await File.WriteAllTextAsync(htmlPath, structureChecked.Html);
                output.WriteLine($"Report written to {htmlPath} - attempt {attempt}, renderTokens={renderTokens}, totalTokens={narrateTokens + renderTokens}");

                var qaResult = await qaActivity.RunAsync(new PlaywrightQaInput(structureChecked.Html));
                output.WriteLine($"Playwright QA (advisory only): hasIssues={qaResult.Result.HasIssues}, consoleErrors={qaResult.Result.ConsoleErrors.Count}, horizontalOverflow={qaResult.Result.HasHorizontalOverflow}");
                return;
            }
            catch (OrchestrationRefusedException ex)
            {
                lastFailure = ex;
                output.WriteLine($"Attempt {attempt}: {ex.ReasonCode} refused - {string.Join("; ", ex.InternalDiagnostics)}");
            }
            catch (InvalidOperationException ex)
            {
                lastFailure = ex;
                output.WriteLine($"Attempt {attempt}: render/inject failed - {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Every attempt (0..{MaxReviewIterations}) failed the deterministic gate chain. Last failure: {lastFailure?.Message}", lastFailure);
    }

    // ================================================================================
    // PHASE 2 - run once per model. Loads the frozen snapshot, renders with ONE target
    // model over Entra ID auth, runs it through the real safety pipeline (Normalize ->
    // Sanitize -> re-Normalize -> Playwright QA), then a bounded review loop against the
    // brand handoff rules. Saves the final HTML + a screenshot + a result summary.
    // ================================================================================
    [Fact]
    public Task RenderWithGpt56Sol() => RenderAndReviewAsync(Targets[0]);

    [Fact]
    public Task RenderWithGpt56Terra() => RenderAndReviewAsync(Targets[1]);

    [Fact]
    public Task RenderWithDeepSeekV4Flash() => RenderAndReviewAsync(Targets[2]);

    /// <summary>
    /// Render-ONLY lab run for the tabs/score-components/frontend-vocabulary prompt experiment
    /// (05_report_html_holistic.md) - reuses whatever snapshot is already on disk, does NOT
    /// re-run gather/compose/narrate. Run CaptureSnapshotAsync first if the snapshot predates a
    /// change that should show up here (e.g. it was captured before the Licence dimension or the
    /// composite score were wired in).
    /// </summary>
    [Fact]
    public Task RenderHolisticWithGpt56Terra() => RenderAndReviewAsync(Targets[1], "05_report_html_holistic.md", SnapshotPath);

    /// <summary>
    /// Render-ONLY lab run for the FIXED 6-tab template (05_report_html_fixed_holistic.md) against
    /// the fixed-composition snapshot - run CaptureFixedHolisticSnapshotAsync first.
    /// </summary>
    [Fact]
    public Task RenderFixedHolisticWithGpt56Terra() => RenderAndReviewAsync(Targets[1], "05_report_html_fixed_holistic.md", FixedHolisticSnapshotPath);

    /// <summary>
    /// Runs all three sequentially and writes one combined comparison summary - convenience over
    /// running the three [Fact]s separately. One model's failure (e.g. a network-restricted
    /// resource) is recorded, not fatal to the other two - the whole point of comparing three
    /// candidates is that one being unreachable should not hide results for the other two.
    /// </summary>
    [Fact]
    public async Task RenderAllModelsAndWriteComparisonSummary()
    {
        var results = new List<ModelRunResult>();
        foreach (var target in Targets)
        {
            try
            {
                results.Add(await RenderAndReviewAsync(target));
            }
            catch (Exception ex)
            {
                output.WriteLine($"[{target.Label}] FAILED: {ex.Message}");
                results.Add(new ModelRunResult(target.Label, 0, 0, false, null, Error: ex.Message));
            }
        }

        var summaryPath = Path.Combine(LabRoot, "comparison-summary.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(results, SnapshotJsonOptions));
        output.WriteLine($"Comparison summary written to {summaryPath}");
        foreach (var r in results)
            output.WriteLine(r.Error is null
                ? $"  {r.Model}: tokens={r.TotalTokens}, reviewIterations={r.ReviewIterations}, approved={r.Approved}, html={r.HtmlPath}"
                : $"  {r.Model}: FAILED - {r.Error}");
    }

    private Task<ModelRunResult> RenderAndReviewAsync(ModelTarget target) => RenderAndReviewAsync(target, "05_report_html.md", SnapshotPath);

    /// <summary>
    /// promptFile lets a lab run try a DIFFERENT copy of the render prompt (e.g.
    /// "05_report_html_holistic.md") against a frozen snapshot - reuses Phase 1's expensive
    /// gather/compose/narrate output untouched, only the render step changes. snapshotPath lets a
    /// run pick which FROZEN snapshot to render from (the dynamic-composition one, or the fixed
    /// 6-tab one from CaptureFixedHolisticSnapshotAsync - different CompositionPlan shapes, never
    /// mix them). Output files are suffixed with the prompt variant so different prompt
    /// experiments do not overwrite each other on disk.
    /// </summary>
    private async Task<ModelRunResult> RenderAndReviewAsync(ModelTarget target, string promptFile, string snapshotPath)
    {
        if (!File.Exists(snapshotPath))
            throw new InvalidOperationException($"No snapshot at {snapshotPath} - run the matching CaptureXSnapshotAsync once first.");
        var snapshot = JsonSerializer.Deserialize<ReportSnapshot>(await File.ReadAllTextAsync(snapshotPath), SnapshotJsonOptions)
            ?? throw new InvalidOperationException("Snapshot deserialised to null.");

        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        // AddInsightsPaidReportAgents alone provides everything this phase needs: IBrowser,
        // IDomPurifySanitizer, IReportQaRunner, IPromptLoader, and the trusted reviewer model's
        // own agent dependencies. PublishGate (AddInsightsWorker) already ran during capture -
        // not needed again here.
        services.AddInsightsPaidReportAgents(configuration);
        services.AddTransient<InjectFontActivity>();
        services.AddTransient<InjectCoverageGridActivity>();
        services.AddTransient<InjectCoverageCssActivity>();
        services.AddTransient<InjectCoverageScriptActivity>();
        services.AddTransient<NormalizeActivity>();
        services.AddTransient<SanitizeActivity>();
        services.AddTransient<ValidateFixedHolisticStructureActivity>();
        services.AddTransient<PlaywrightQaActivity>();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var promptLoader = sp.GetRequiredService<IPromptLoader>();
        var (brandHandoff, groundTruthHtml) = await LoadGroundTruthAndContractAsync(promptLoader, promptFile);
        var tokensCss = await File.ReadAllTextAsync(TokensCssPath);
        var renderInstructions = await BuildRenderInstructionsAsync(promptLoader, brandHandoff, groundTruthHtml, tokensCss, promptFile);
        var reviewerAgent = BuildReviewerAgent(brandHandoff, groundTruthHtml);

        // Rendered once - the ground-truth reference's own layout is fixed, does not vary per
        // iteration or per candidate model, so there is no reason to re-render it every retry.
        var referenceScreenshot = await RenderScreenshotAsync(sp.GetRequiredService<Microsoft.Playwright.IBrowser>(), groundTruthHtml);

        var totalTokens = 0L;
        string html = "";
        string? lastGoodHtml = null; // last attempt that actually passed the deterministic gates - see its use below
        var iterations = 0;
        HtmlReviewResult review = new(false, []);
        ReportQaResult? qa = null;

        Directory.CreateDirectory(LabRoot);
        var variantSuffix = promptFile == "05_report_html.md" ? "" : $"-{Slug(Path.GetFileNameWithoutExtension(promptFile))}";
        var htmlPath = Path.Combine(LabRoot, $"{Slug(target.Label)}{variantSuffix}.html");
        // [HARDENED - found live] the real path was written only after this whole for-loop
        // returns. A crash mid-loop (any exception this method's own try/catch does not
        // recognise - confirmed live: InjectFontActivity's PoppinsFontInjector throwing a plain
        // InvalidOperationException on a malformed/truncated LLM response, on iteration 1, after
        // iteration 0 had ALREADY produced a real, gate-passing render) lost that render
        // entirely - nothing on disk, ~98k real tokens spent for nothing. This in-progress path
        // is overwritten after every iteration that gets AT LEAST THROUGH the deterministic gates
        // (Normalize/Sanitize/ValidateFixedHolisticStructure) - not the raw agent output, and not
        // gated on reviewer approval - so a later crash still leaves the best gate-passing attempt
        // on disk instead of nothing.
        var inProgressPath = Path.Combine(LabRoot, $"{Slug(target.Label)}{variantSuffix}-inprogress.html");

        for (iterations = 0; iterations < MaxReviewIterations + 1; iterations++)
        {
            // Prior-iteration issues get folded into the INSTRUCTIONS, not passed to RenderAsync -
            // IReportHtmlAgent has no revision parameter (unlike Compose/Narrate), and extending the
            // production interface for lab-only retry feedback is out of scope here.
            var instructionsThisAttempt = iterations == 0
                ? renderInstructions
                : renderInstructions + $"\n\n---\n\nA PREVIOUS ATTEMPT WAS REJECTED. Fix every issue below and re-render the WHOLE document:\n{JsonSerializer.Serialize(review.Issues)}";
            var renderAgent = BuildEntraIdRenderAgent(target, instructionsThisAttempt);
            var htmlAgent = new MafReportHtmlAgent(renderAgent);

            var normalizeActivity = sp.GetRequiredService<NormalizeActivity>();
            var sanitizeActivity = sp.GetRequiredService<SanitizeActivity>();
            var structureActivity = sp.GetRequiredService<ValidateFixedHolisticStructureActivity>();
            var qaActivity = sp.GetRequiredService<PlaywrightQaActivity>();

            // Same node 8a the real orchestrator now runs (InsightsReportOrchestrator 1.5) -
            // embeds the REAL vendored Poppins font before anything validates the document, so the
            // lab exercises the exact same pipeline production does, not a stand-in.
            //
            // [HARDENED - found live] InjectFontActivity now lives INSIDE this try, not before it.
            // PoppinsFontInjector fails LOUDLY (CLAUDE.md non-negotiable 2, by design) on a missing
            // <head> tag - correct for production (a malformed render must not silently ship), but
            // in THIS lab retry loop a missing <head> is exactly the same class of "doesn't follow
            // the provided rules" as a normalizer violation: the render agent truncated or
            // malformed its response (same failure family ReportHtmlAgent's own StripMarkdownFence
            // trap and the DeepSeek-V4-Flash truncation note both document), and the fix is to tell
            // it so and let it re-render - not to crash the whole test and lose iteration 0's
            // already-good, gate-passing output.
            //
            // [BUG FOUND LIVE, 2026-09-02] The render call itself used to sit OUTSIDE this try
            // (only its own bounded network-retry wrapped it), on the reasoning "a content problem
            // would just waste tokens repeating the same bad call" - wrong: RenderAsync's own
            // <head>-tag check (added the same session, for the same reason PoppinsFontInjector's
            // throw is caught below) is a real, RECOVERABLE failure mode - LLM output is not
            // deterministic, a truncated response on one call does not mean the next call
            // truncates too (this is the exact same lesson the MaxOutputTokens 16000->24000 fix
            // was built on, earlier the same session). Confirmed live: raising MaxReviewIterations
            // gave a later attempt (iteration 4) more room to hit this exact failure mode, and it
            // crashed the whole 13-minute, ~480k-token run instead of retrying with feedback like
            // every other deterministic-gate failure already does.
            //
            // NormalizeActivity/SanitizeActivity/ValidateFixedHolisticStructureActivity throw
            // OrchestrationRefusedException on a deterministic-rule violation (external ref, wrong
            // doctype count, wrong score-component count, fake blocked-tab badge, etc.).
            try
            {
                var renderResult = await RenderWithNetworkRetryAsync(
                    () => htmlAgent.RenderAsync(snapshot.Plan, snapshot.Narrative, snapshot.Assertions, snapshot.TenantName, snapshot.ReportType, snapshot.GeneratedAtUtc, snapshot.LocationRows),
                    target.Label, iterations, output);
                totalTokens += renderResult.TotalTokens;
                html = renderResult.Value;

                var injectFontActivity = sp.GetRequiredService<InjectFontActivity>();
                var fonted = await injectFontActivity.RunAsync(new InjectFontInput(html));

                var injectCoverageGridActivity = sp.GetRequiredService<InjectCoverageGridActivity>();
                var coverageGridded = await injectCoverageGridActivity.RunAsync(new InjectCoverageGridInput(fonted.Html, snapshot.LocationRows));

                var injectCoverageCssActivity = sp.GetRequiredService<InjectCoverageCssActivity>();
                var coverageStyled = await injectCoverageCssActivity.RunAsync(new InjectCoverageCssInput(coverageGridded.Html));

                var injectCoverageScriptActivity = sp.GetRequiredService<InjectCoverageScriptActivity>();
                var coverageScripted = await injectCoverageScriptActivity.RunAsync(new InjectCoverageScriptInput(coverageStyled.Html));

                var normalized = await normalizeActivity.RunAsync(new NormalizeInput(coverageScripted.Html));
                var sanitized = await sanitizeActivity.RunAsync(new SanitizeInput(normalized.Html));
                var reNormalized = await normalizeActivity.RunAsync(new NormalizeInput(sanitized.Html));
                var structureChecked = await structureActivity.RunAsync(new ValidateFixedHolisticStructureInput(reNormalized.Html, snapshot.ReportType));
                html = structureChecked.Html;
            }
            catch (OrchestrationRefusedException ex)
            {
                review = new HtmlReviewResult(false, ex.InternalDiagnostics.Select(d => new HtmlReviewIssue(ex.ReasonCode, d, "Fix the document so it satisfies this rule.")).ToList());
                output.WriteLine($"[{target.Label}] iteration {iterations}: {ex.ReasonCode} refused - {ex.InternalDiagnostics.Count} violation(s), tokensSoFar={totalTokens}");
                continue;
            }
            catch (InvalidOperationException ex)
            {
                // PoppinsFontInjector's one throw shape (see its own doc comment) - anything else
                // of this type is a genuine bug this loop should NOT swallow, but its message is
                // specific enough to trust here without a broader catch(Exception).
                review = new HtmlReviewResult(false, [new HtmlReviewIssue("InjectFontActivity", ex.Message, "Re-render the WHOLE document as a complete, well-formed HTML document with a real <head> tag - the previous attempt was truncated or malformed.")]);
                output.WriteLine($"[{target.Label}] iteration {iterations}: InjectFontActivity failed - {ex.Message}, tokensSoFar={totalTokens}");
                continue;
            }

            // Best gate-passing attempt so far - see inProgressPath's own comment above for why
            // this is not deferred to the end of the loop. lastGoodHtml is the SAME content kept
            // in memory too - see its use after the loop for why the file alone was not enough.
            lastGoodHtml = html;
            await File.WriteAllTextAsync(inProgressPath, html);

            var qaResult = await qaActivity.RunAsync(new PlaywrightQaInput(html));
            qa = qaResult.Result;

            review = await ReviewAsync(reviewerAgent, html, qa, referenceScreenshot);
            output.WriteLine($"[{target.Label}] iteration {iterations}: approved={review.Approved}, issues={review.Issues.Count}, tokensSoFar={totalTokens}");

            if (review.Approved)
                break;
        }

        // [BUG FOUND LIVE] The unconditional `File.WriteAllTextAsync(htmlPath, html)` this used to
        // be wrote whatever `html` held at loop exit - including a RAW, un-normalized, truncated
        // attempt from an iteration that hit a deterministic-gate refusal on its own final try
        // (confirmed live: iteration 2 hit NOT_NORMALIZABLE - a truncated response missing its
        // closing tags entirely - and the loop then exhausted its max attempts, so that broken
        // html was the last thing in the variable). The cleanup line right after it then DELETED
        // inProgressPath - which was still holding iteration 1's real, gate-passing (just
        // reviewer-flagged) result - discarding something strictly better than what got kept, with
        // nothing in the file name or its mere existence to say it was broken. Fall back to the
        // last gate-passing attempt whenever the final one did not itself pass.
        if (html != lastGoodHtml && lastGoodHtml is not null)
        {
            output.WriteLine($"[{target.Label}] WARNING: the final iteration ({iterations}) did not pass the deterministic gates (Normalize/Sanitize/Structure) - writing the last gate-passing attempt instead of the raw/refused output.");
            html = lastGoodHtml;
        }

        // [FAIL CLOSED, 2026-09-02] Previously this wrote raw html to htmlPath unconditionally even
        // when lastGoodHtml was null - EVERY iteration failed the deterministic chain, so this
        // silently persisted a raw, never-fonted, possibly-broken document under the same filename
        // a caller expects a real gate-passing report at. Matches CLAUDE.md non-negotiable 2 (fail
        // closed AND fail loudly): a refused report is a good outcome; a broken one that looks like
        // a real result under the normal filename is not. This exact gap turned every one of the
        // several "why is the font/script missing" investigations this session into a 20-minute
        // forensic dig instead of one line of exception text - the raw content is still saved (to
        // a clearly-named -failed-raw.html, for post-mortem inspection), just never at htmlPath.
        if (lastGoodHtml is null)
        {
            var failedRawPath = Path.Combine(LabRoot, $"{Slug(target.Label)}{variantSuffix}-failed-raw.html");
            await File.WriteAllTextAsync(failedRawPath, html);
            if (File.Exists(inProgressPath))
                File.Delete(inProgressPath);
            throw new InvalidOperationException(
                $"[{target.Label}] every attempt (0..{iterations}) failed the deterministic gate chain (Inject/Normalize/Sanitize/Structure) - " +
                $"never reached the reviewer stage even once. Last attempt's issue: {(review.Issues.Count > 0 ? review.Issues[0].Problem : "(none recorded)")}. " +
                $"Raw last attempt saved for inspection at {failedRawPath}.");
        }

        await File.WriteAllTextAsync(htmlPath, html);
        // The real result landed at htmlPath above (whether from this iteration or the fallback
        // just above) - the in-progress copy was only ever a crash/fallback safety net, and
        // leaving both around after a clean finish is confusing, not helpful.
        if (File.Exists(inProgressPath))
            File.Delete(inProgressPath);
        if (qa is not null && qa.Screenshot.Length > 0)
            await File.WriteAllBytesAsync(Path.Combine(LabRoot, $"{Slug(target.Label)}{variantSuffix}.png"), qa.Screenshot);

        return new ModelRunResult(target.Label, totalTokens, iterations + 1, review.Approved, htmlPath, review.Approved ? [] : review.Issues);
    }

    /// <summary>
    /// Bounded retry (2 attempts, 3s backoff) around the Entra ID render call specifically - found
    /// live: a TLS/socket read timeout against the Azure endpoint hit on a RETRY iteration (never
    /// iteration 0) 3 of 4 times this call was exercised, always crashing the whole test because
    /// this call sat outside every try/catch in the loop. Only retries when the exception (walking
    /// both `InnerException` and, for an AggregateException, every `InnerExceptions` entry) is a
    /// genuinely transient network failure - TaskCanceledException, HttpRequestException, or
    /// SocketException. Anything else (e.g. RenderAsync's own "no text" InvalidOperationException)
    /// throws straight through unretried - retrying a content problem would just waste tokens
    /// repeating the same bad call, not fix it.
    /// </summary>
    private static async Task<AgentCallResult<string>> RenderWithNetworkRetryAsync(
        Func<Task<AgentCallResult<string>>> render, string targetLabel, int iteration, ITestOutputHelper output)
    {
        const int maxAttempts = 2;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await render();
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientNetworkFailure(ex))
            {
                output.WriteLine($"[{targetLabel}] iteration {iteration}: transient network failure on render attempt {attempt} ({ex.GetType().Name}) - retrying in 3s.");
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    private static bool IsTransientNetworkFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is TaskCanceledException or HttpRequestException or System.Net.Sockets.SocketException)
                return true;
            if (current is AggregateException agg && agg.InnerExceptions.Any(IsTransientNetworkFailure))
                return true;
        }
        return false;
    }

    private static string Slug(string label) => label.ToLowerInvariant().Replace(" ", "-");

    /// <summary>Same viewport/full-page settings as PlaywrightReportQa, minus the console/overflow checks the ground-truth reference does not need.</summary>
    private static async Task<byte[]> RenderScreenshotAsync(Microsoft.Playwright.IBrowser browser, string html)
    {
        var page = await browser.NewPageAsync(new Microsoft.Playwright.BrowserNewPageOptions { ViewportSize = new Microsoft.Playwright.ViewportSize { Width = 1200, Height = 800 } });
        try
        {
            await page.SetContentAsync(html, new Microsoft.Playwright.PageSetContentOptions { WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle });
            return await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions { FullPage = true });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// [v2 handoff fix] The token-ladder + ground-truth-reference feed stayed (real, working
    /// improvement - cut the reviewer's issue count from 4-5 down to 2).
    ///
    /// [font fix, now REAL] The earlier @font-face override was pulled back out for a while:
    /// confirmed live that no model can actually produce real font binary data -
    /// gpt-5.6-terra's own "self-hosted Poppins" @font-face block decoded to 6 bytes (just the
    /// WOFF2 magic number, no glyph data at all). That is still true and always will be - but it
    /// is no longer this method's problem. PoppinsFontInjector (Insights.Presentation, called
    /// from the real orchestrator's node 8a, InjectFontActivity) now embeds the REAL vendored
    /// Poppins file deterministically, AFTER the model renders and BEFORE anything validates the
    /// document - see vendor/README.md for provenance. So the render agent's job shrank to
    /// exactly what it can actually do: write the CSS name "Poppins" and never touch @font-face
    /// itself. The FONT RULE NOTE below reflects that split.
    /// </summary>
    /// <summary>
    /// Selects the ground-truth reference + "brand contract" text to review a candidate against,
    /// keyed on which render prompt produced it. 05_report_html_fixed_holistic.md targets a
    /// completely different approved page (the real detailed-insights.component.html 6-tab product
    /// UI) than every other prompt here (the numbered-01-08-sections report gpt-5.6-terra-approved.html
    /// documents) - see FixedHolisticGroundTruthHtmlPath's own comment for the defect this fixes.
    /// For that variant, the "brand contract" fed to the reviewer is this template's OWN prompt
    /// rather than AI-INSIGHTS-BRAND-HANDOFF.md, which documents the other report's contract.
    /// Every other promptFile keeps the original brandHandoff/groundTruth pair unchanged - no
    /// behaviour change for the already-working holistic path.
    ///
    /// [FIX, 2026-09-02] Used to also load 05_report_html.md and append it as a "shared base
    /// contract" (Output constraints/Security/Accessibility 05_report_html_fixed_holistic.md used
    /// to defer to that file for). That file was removed the same session
    /// ("compliance_health"/the plain no-fixed-tabs render path was retired) - its three sections
    /// are now INLINED directly in 05_report_html_fixed_holistic.md itself (see that file's own
    /// "[FIX - inlined 2026-09-02]" note), so fixedHolisticPrompt alone is already the complete
    /// contract - nothing left to append.
    /// </summary>
    private static async Task<(string BrandHandoff, string GroundTruthHtml)> LoadGroundTruthAndContractAsync(
        IPromptLoader promptLoader, string promptFile)
    {
        if (promptFile == "05_report_html_fixed_holistic.md")
        {
            var contract = await promptLoader.LoadAsync("05_report_html_fixed_holistic.md");
            var groundTruthHtml = await File.ReadAllTextAsync(FixedHolisticGroundTruthHtmlPath);
            return (contract, groundTruthHtml);
        }

        var brandHandoff = await File.ReadAllTextAsync(BrandHandoffPath);
        var defaultGroundTruthHtml = await File.ReadAllTextAsync(GroundTruthHtmlPath);
        return (brandHandoff, defaultGroundTruthHtml);
    }

    private static async Task<string> BuildRenderInstructionsAsync(IPromptLoader promptLoader, string brandHandoff, string groundTruthHtml, string tokensCss, string promptFile = "05_report_html.md")
    {
        var basePrompt = await promptLoader.LoadAsync(promptFile);
        return basePrompt +
            "\n\n---\n\n# BRAND CONTRACT (binding, on top of everything above, EXCEPT the font rule - see note below)\n\n" + brandHandoff +
            "\n\n---\n\n# FONT RULE NOTE\n\n" +
            "Use font-family: 'Poppins', sans-serif EVERYWHERE - on body, and explicitly on every " +
            "SVG <text> element's font-family attribute too (SVG text does not inherit from body " +
            "CSS). This matches the ground-truth reference exactly. Do NOT declare @font-face " +
            "yourself and do NOT emit a Google Fonts <link>/CDN reference of any kind (the brand " +
            "contract's newer wiring instruction saying to add a fonts.googleapis.com <link> is " +
            "OVERRIDDEN here - zero external references stays a hard rule, per the base prompt's " +
            "own Rule 4). A real, self-hosted Poppins @font-face is injected automatically after " +
            "you render, by a deterministic step that embeds real vendored font bytes - not " +
            "something you can produce yourself, so you never need to try. Just write the name." +
            "\n\n---\n\n# TOKEN LADDER (reference/tokens.css, verbatim - the real --fs-di-*/--gap-*/--r-* values; author sizes from these vars, not hardcoded px/rem)\n\n```css\n" + tokensCss + "\n```" +
            "\n\n---\n\n# FONT SIZE BUMP (readability fix - binding override on top of the ladder above)\n\n" +
            "Text has been reported as too small on rendered output. Every `--fs-*` custom property " +
            "declared in the token ladder above MUST be redeclared in your `:root` (and inside each " +
            "`@media` breakpoint block that also redeclares it) at +1px over the value printed above " +
            "- e.g. a ladder value of `11px` becomes `12px`, `9.5px` becomes `10.5px`, `2.3rem` " +
            "(=`36.8px`, i.e. `--fs-di-score`) becomes `37.8px`. Keep every other token (`--gap-*`, " +
            "`--r-*`, colors) exactly as printed - only the `--fs-*` sizes shift. This also applies to " +
            "the donut SVG hardcoded `font-size` attributes below (22 -> 23, 9 -> 10) and to any other " +
            "hardcoded px font-size you author outside a `--fs-*` var. Do not scale line-height, " +
            "padding, or box dimensions to compensate - only the font-size numbers move.\n" +
            "\n\n---\n\n# GROUND-TRUTH APPROVED REFERENCE (reference/gpt-5.6-terra-approved.html, verbatim)\n\n" +
            "This is the designer-approved reference implementation for this exact report. When a rule " +
            "above and your instinct disagree, match this file - including its font-family: 'Poppins' " +
            "usage (see FONT RULE NOTE above: that name is now backed by a real injected font, so " +
            "matching the reference's Poppins usage exactly is correct, not a trap). " +
            "Match its structure, markup patterns, and styling. Do not copy its report DATA.\n\n```html\n" + groundTruthHtml + "\n```" +
            "\n\n---\n\n# DONUT CHART - VERIFIED-SAFE MARKUP (measured in a real headless browser, not a guess)\n\n" +
            "For the donut score (viewBox=\"0 0 120 120\", circle cx=60 cy=60 r=48 stroke-width=12), " +
            "do NOT use font-size: var(--fs-di-score) on the SVG <text> elements - that token is sized " +
            "for page-level rem context (2.3rem/~37px) and is far too large for this 120-unit viewBox; " +
            "measured overlap between the number and its label by 1.6px when tried. Instead use the SAME " +
            "SVG-native hardcoded sizes the ground-truth reference uses (correct precisely because they " +
            "are scaled to THIS SVG's own coordinate space, not the page's token ladder - the token " +
            "ladder rule does not apply inside a small fixed-viewBox SVG): " +
            "<text x=\"60\" y=\"59.2\" text-anchor=\"middle\" font-size=\"23\" font-weight=\"600\">{number}</text> " +
            "and <text x=\"60\" y=\"76.2\" text-anchor=\"middle\" font-size=\"10\">{label}</text> - sizes " +
            "carry the same +1px readability bump as the FONT SIZE BUMP section above; re-verify " +
            "clearance with MeasureDonutOverlapAsync after changing these.";
    }

    /// <summary>
    /// Entra ID auth (DefaultAzureCredential -> az login session), not an API key - see class doc
    /// comment.
    ///
    /// [TRAP - found live] Azure.AI.OpenAI's AzureOpenAIClient (the "obvious" TokenCredential-
    /// accepting client) constructs requests against the CLASSIC /openai/deployments/{name}/...
    /// route. These Foundry Models deployments only support the NEW unified /openai/v1/ route -
    /// confirmed by curl: an identical REST call to https://{resource}/openai/v1/responses with a
    /// bearer token succeeded (200) for gpt-5.6-terra and DeepSeek-V4-Flash, while AzureOpenAIClient
    /// against the exact same resource/deployment returned 404 for ALL THREE targets, including
    /// the two curl proved work. So this builds a plain OpenAI.OpenAIClient instead - the exact
    /// same v1-route shape MafAgentFactory already uses for the API-key path - with a hand-rolled
    /// AuthenticationPolicy that wraps DefaultAzureCredential, since the plain OpenAI package's
    /// BearerTokenPolicy needs an AuthenticationTokenProvider adapter neither Azure.Identity 1.21.0
    /// nor OpenAI 2.11.0 supply one for out of the box.
    /// </summary>
    /// <summary>
    /// Set USE_MANAGED_IDENTITY=1 to force ManagedIdentityCredential ONLY - no fallback to
    /// AzureCliCredential/EnvironmentCredential/etc, so a real MI failure surfaces as a real
    /// failure instead of silently succeeding off your az-login session. Only meaningful when run
    /// from a resource an identity is actually assigned to (Azure VM/App Service/AKS pod/etc with
    /// IMDS reachable) - on a plain dev machine ManagedIdentityCredential has nothing to talk to
    /// and will throw. AZURE_CLIENT_ID selects a user-assigned identity by client id; unset means
    /// system-assigned. Default (unset/not "1") stays DefaultAzureCredential, unchanged.
    /// </summary>
    private static Azure.Core.TokenCredential BuildCredential()
    {
        if (Environment.GetEnvironmentVariable("USE_MANAGED_IDENTITY") != "1")
            return new DefaultAzureCredential();

        var miClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var id = string.IsNullOrEmpty(miClientId) ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(miClientId);
        return new ManagedIdentityCredential(id);
    }

    private static AIAgent BuildEntraIdRenderAgent(ModelTarget target, string instructions)
    {
        var authPolicy = new BearerTokenPolicy(new AzureIdentityTokenProvider(BuildCredential()), "https://ai.azure.com/.default");
        var client = new OpenAIClient(authPolicy, new OpenAIClientOptions { Endpoint = new Uri(target.Endpoint.TrimEnd('/') + "/openai/v1/"), NetworkTimeout = TimeSpan.FromMinutes(5) } /* [BUG FOUND LIVE, 2026-09-01] SDK default is 100s - too short for a large-document render/review call, see MafAgentFactory.cs's own note */);
        IChatClient chatClient = client.GetResponsesClient().AsIChatClient(target.Deployment);

        var options = new ChatClientAgentOptions
        {
            Name = $"ReportHtmlAgent-{target.Label}",
            Description = "Renders the approved report as self-contained HTML - model comparison lab.",
            // [TRAP - found live] With no explicit cap, DeepSeek-V4-Flash's response truncated
            // mid-document (confirmed: the saved HTML had no closing </html> at all, cut off
            // mid-tag) - the render agent's whole document (brand contract + full report data)
            // needs headroom well past whatever a provider's own default happens to be.
            // [RAISED 16000 -> 24000, found live 2026-09-01] gpt-5.6-terra hit the same SHAPE of
            // failure (confirmed: saved HTML cut off mid-<script>, no closing tags) once the
            // Coverage tab grew a real interactive script + data-*-laden tiles + section-number
            // chips + the expanded caveats card. NOT the same root cause DiagnoseDeepSeekTruncationAsync
            // found for THAT model, though - that diagnostic's own finding was raising this value
            // did NOT move DeepSeek's cutoff (finishReason=Stop, not Length - it was choosing to
            // stop, not hitting the ceiling). Raising this for gpt-5.6-terra is a reasonable next
            // step given the document is now genuinely bigger, not a proven fix carried over from
            // that finding - re-check finishReason if truncation recurs at 24000.
            ChatOptions = new ChatOptions { Instructions = instructions, ResponseFormat = ChatResponseFormat.Text, MaxOutputTokens = 24000 },
        };
        return new ChatClientAgent(chatClient, options);
    }

    /// <summary>
    /// Bridges Azure.Identity's TokenCredential (DefaultAzureCredential) into
    /// System.ClientModel.AuthenticationTokenProvider (note: System.ClientModel, NOT
    /// System.ClientModel.Primitives - confirmed by reflection against the real assembly after an
    /// earlier guess at the namespace failed to compile), which is what BearerTokenPolicy actually
    /// needs. No built-in adapter ships in these package versions. Small on purpose - lab-only
    /// glue, not something the production MafAgentFactory path needs (it stays on API keys).
    /// </summary>
    private sealed class AzureIdentityTokenProvider(Azure.Core.TokenCredential credential) : AuthenticationTokenProvider
    {
        public override GetTokenOptions CreateTokenOptions(IReadOnlyDictionary<string, object> properties) => new(properties);

        public override AuthenticationToken GetToken(GetTokenOptions options, CancellationToken cancellationToken)
        {
            var scopes = ((ReadOnlyMemory<string>)options.Properties[GetTokenOptions.ScopesPropertyName]!).ToArray();
            var token = credential.GetToken(new Azure.Core.TokenRequestContext(scopes), cancellationToken);
            return new AuthenticationToken(token.Token, "Bearer", token.ExpiresOn, null);
        }

        public override async ValueTask<AuthenticationToken> GetTokenAsync(GetTokenOptions options, CancellationToken cancellationToken)
        {
            var scopes = ((ReadOnlyMemory<string>)options.Properties[GetTokenOptions.ScopesPropertyName]!).ToArray();
            var token = await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(scopes), cancellationToken);
            return new AuthenticationToken(token.Token, "Bearer", token.ExpiresOn, null);
        }
    }

    /// <summary>
    /// The trusted, FIXED judge - one model, ENTRA ID auth (no API key - same BuildCredential()
    /// used for the render targets, so USE_MANAGED_IDENTITY=1 covers this too), unchanged, for
    /// every candidate. Never lets a model grade itself. gpt-5.2 on the same trpl-prod-saas-ai-3
    /// resource terra lives on - confirmed by appsettings.json's Llm:Maf:Endpoint pointing at that
    /// same resource - so the RBAC/network posture Entra already works for terra covers this
    /// deployment too, not a separate access grant.
    /// </summary>
    /// <summary>
    /// [TRAP - found live] The instructions used to CLAIM "you will be given the full brand
    /// contract rules" but never actually included them - the reviewer only ever saw QA findings
    /// + HTML, so it was inventing/guessing what the fixed palette was instead of checking against
    /// it. Confirmed live: it rejected #eef1fe/#f7f0fb/#fdf6fb/#f4f8ff and #e8eeff/#adb9d2 as
    /// "non-palette" - every one of those is a literal hex value straight out of the brand
    /// handoff's own Sec.3.1/3.2 surface recipes (the dark AI band ink, the pastel hero gradient
    /// stops), not a violation at all. Fixed by actually appending the real brand handoff text -
    /// same document the render agent gets - so the reviewer checks against the real rules.
    /// </summary>
    private static readonly ModelTarget ReviewerTarget = new("gpt-5.2-reviewer", "https://trpl-prod-saas-ai-3.cognitiveservices.azure.com/", "gpt-5.2");

    private static AIAgent BuildReviewerAgent(string brandHandoff, string groundTruthHtml)
    {
        var authPolicy = new BearerTokenPolicy(new AzureIdentityTokenProvider(BuildCredential()), "https://ai.azure.com/.default");
        var client = new OpenAIClient(authPolicy, new OpenAIClientOptions { Endpoint = new Uri(ReviewerTarget.Endpoint.TrimEnd('/') + "/openai/v1/"), NetworkTimeout = TimeSpan.FromMinutes(5) } /* same NetworkTimeout fix - the reviewer reads a whole rendered document too */);
        IChatClient chatClient = client.GetResponsesClient().AsIChatClient(ReviewerTarget.Deployment);

        var options = new ChatClientAgentOptions
        {
            Name = "ModelComparisonReviewer",
            Description = "Checks rendered HTML against the brand handoff and Playwright findings.",
            ChatOptions = new ChatOptions { Instructions = BuildReviewerInstructions(brandHandoff, groundTruthHtml), ResponseFormat = ChatResponseFormat.Json },
        };
        return new ChatClientAgent(chatClient, options);
    }

    /// <summary>
    /// [vision pass added] This report renders directly in front of CEOs/CFOs - a visual defect
    /// (overlapping text, clipped content, misaligned elements) is not a minor nit, it is the kind
    /// of mistake an executive audience notices immediately. Confirmed live that the text-only
    /// review pass CANNOT catch this class of bug: a donut chart's number visually overlapped its
    /// own label by a measured 1.6px (getBBox() in a real headless browser) while the CSS/HTML
    /// SOURCE looked completely reasonable (used the correct approved token, no hardcoded px) -
    /// nothing in the markup itself signals the defect, only the actual rendered pixels do. Two
    /// screenshots are now attached to every review call: the CANDIDATE render, and the
    /// designer-approved GROUND-TRUTH reference rendered fresh (same browser, same viewport) -
    /// compare them side by side for structural/visual fidelity. Report DATA (numbers, entity
    /// names) will differ between the two - that is expected, never a violation.
    /// </summary>
    private static string BuildReviewerInstructions(string brandHandoff, string groundTruthHtml) => $$"""
        You are a strict brand/QA reviewer for an AI-generated compliance report page. This report
        is shown DIRECTLY to CEOs and CFOs - review it as thoroughly as a real design lead would
        before it reaches an executive's screen. A visual glitch that would embarrass the company
        in front of a CFO is a hard rejection, even if every rule below is technically satisfied.

        Below is the FULL brand contract this page must follow - colors, fonts, radii, component
        vocabulary, the acceptance checklist. ANY hex value, gradient, or literal shown ANYWHERE in
        this contract (including inside the surface-recipe CSS blocks in Sec.3) is APPROVED, even if
        it is not also a named CSS token in Sec.2's table - Sec.3's recipes are part of the fixed
        identity, not an exception to it. Only flag a color as a violation if it does NOT appear
        anywhere in the contract below, in any form.

        --- BRAND CONTRACT (verbatim) ---
        {{brandHandoff}}
        --- END BRAND CONTRACT ---

        Below is the designer-APPROVED ground-truth reference implementation for this exact report.
        Sec.6/7 of the contract above says: "when a rule and your instinct disagree, match this
        file." Use it to judge structure, markup patterns, and styling choices (section numbering,
        hero layout, spacing rhythm, etc) - NOT its report data/numbers, which belong to a
        different run.

        --- GROUND-TRUTH APPROVED REFERENCE (verbatim) ---
        {{groundTruthHtml}}
        --- END GROUND-TRUTH APPROVED REFERENCE ---

        You will also be given, per review: automated QA findings (console errors, horizontal
        overflow), the full rendered HTML, and TWO SCREENSHOTS - the CANDIDATE render first, then
        the GROUND-TRUTH reference rendered fresh. Look at both images carefully before answering.

        VISUAL COMPARISON CHECKLIST (this is the part text-only review misses - spend real
        attention here, element by element, not just an overall impression):
        - Does any text visually overlap, collide with, or sit too close to another element (a
          number crowding its own label, a chip crowding adjacent text, a pill overlapping a
          neighbouring badge)? Look specifically at donut/gauge centers, KPI tile numbers, chips,
          and table cells - these are the tightest-packed elements and where overlap is most likely.
        - Is any text clipped, cut off, or overflowing its container?
        - Is spacing/alignment/component sizing visually consistent with the reference screenshot,
          accounting for the candidate having different (and possibly longer/shorter) real data?
        - Does color usage, hierarchy, and overall visual weight match the reference's look, not
          just individual hex values in isolation?
        - Does anything look structurally "off" a careful human reviewer would flag on sight, even
          if you cannot name which specific rule it breaks?

        FONT NOTE: a real, self-hosted Poppins face is injected automatically, DETERMINISTICALLY,
        into every candidate BEFORE you ever see it (a pipeline step, not an LLM choice) - so the
        HTML you are shown will ALWAYS contain @font-face declarations for Poppins, on every single
        review, regardless of what the LLM itself wrote. [FIX - found live] Do NOT reject on
        @font-face's mere presence - you cannot tell "the LLM declared this" from "the pipeline
        injected this after the LLM ran" from the final HTML text alone, and by construction it is
        always the latter. Only reject the font on: the PRIMARY font-family not being 'Poppins' (a
        system-font stack like -apple-system/Segoe UI/Roboto used instead), font-family missing on
        an SVG <text> element (SVG text does not inherit from body CSS), or any font loading from an
        external CDN/Google Fonts <link> - self-hosted only stays a hard rule, just not via this
        signal.

        Reject if: any hex colour appears that does not appear anywhere in the contract above; more
        than one dark surface exists; red/amber/green is used without carrying a real status meaning;
        px values are hardcoded where the contract's own token variables should be used instead (not
        the contract's OWN px values in its token definitions - those are the source of truth);
        layout properties (width/height/top/left) are animated; any console error or horizontal
        overflow was reported by QA; the document does not read as a single coherent RegTrack
        screen; OR any issue found in the VISUAL COMPARISON CHECKLIST above.

        Respond as JSON only: {"approved": bool, "issues": [{"rule": string, "problem": string, "fix": string}]}.
        Empty issues array when approved. Never approve if QA reported any console error or overflow,
        or if the candidate screenshot shows a visual defect from the checklist above.
        """;

    private static async Task<HtmlReviewResult> ReviewAsync(AIAgent reviewerAgent, string html, ReportQaResult qa, byte[] referenceScreenshot)
    {
        var qaSummary = JsonSerializer.Serialize(new { qa.HasIssues, qa.ConsoleErrors, qa.HasHorizontalOverflow });
        // [TRAP] Same one CompositionAgent.cs already documents: the Responses API 400s on
        // ResponseFormat=Json unless the literal word "json" appears in the INPUT message itself -
        // instructions containing it is not enough. Confirmed live against gpt-5.6-terra.
        var text_ = $"Review this as JSON:\nQA findings:\n{qaSummary}\n\nRendered HTML:\n{html}\n\nFirst image below is the CANDIDATE render. Second image is the GROUND-TRUTH reference render.";
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(text_),
            new DataContent(qa.Screenshot, "image/png"),
            new DataContent(referenceScreenshot, "image/png"),
        ]);
        var response = await reviewerAgent.RunAsync(message);
        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            return new HtmlReviewResult(false, [new HtmlReviewIssue("reviewer", "empty response from reviewer agent", "retry")]);

        try
        {
            var parsed = JsonSerializer.Deserialize<HtmlReviewResult>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed ?? new HtmlReviewResult(false, [new HtmlReviewIssue("reviewer", "reviewer returned null", "retry")]);
        }
        catch (JsonException ex)
        {
            return new HtmlReviewResult(false, [new HtmlReviewIssue("reviewer", $"reviewer returned unparsable JSON: {ex.Message}", "retry")]);
        }
    }

    private sealed record HtmlReviewIssue(string Rule, string Problem, string Fix);
    private sealed record HtmlReviewResult(bool Approved, IReadOnlyList<HtmlReviewIssue> Issues);

    // ================================================================================
    // DIAGNOSTIC ONLY - not part of the two-phase pipeline above. Calls the raw
    // IChatClient directly instead of going through ChatClientAgent/MafReportHtmlAgent,
    // because AgentRunResponse (what agent.RunAsync returns) does not surface
    // FinishReason - only ChatResponse (what IChatClient.GetResponseAsync returns) does.
    // Built to answer one question: is DeepSeek-V4-Flash's ~11.4-11.9K char cutoff a
    // MaxOutputTokens ceiling (finishReason=Length) or the model choosing to stop on its
    // own (finishReason=Stop) - raising MaxOutputTokens from default to 16000 earlier did
    // NOT move the cutoff length, which already points at Stop, not Length.
    // ================================================================================
    [Fact]
    public async Task DiagnoseDeepSeekTruncationAsync()
    {
        if (!File.Exists(SnapshotPath))
            throw new InvalidOperationException($"No snapshot at {SnapshotPath} - run CaptureSnapshotAsync once first.");
        var snapshot = JsonSerializer.Deserialize<ReportSnapshot>(await File.ReadAllTextAsync(SnapshotPath), SnapshotJsonOptions)
            ?? throw new InvalidOperationException("Snapshot deserialised to null.");

        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddInsightsPaidReportAgents(configuration);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var promptLoader = sp.GetRequiredService<IPromptLoader>();
        var brandHandoff = await File.ReadAllTextAsync(BrandHandoffPath);
        var groundTruthHtml = await File.ReadAllTextAsync(GroundTruthHtmlPath);
        var tokensCss = await File.ReadAllTextAsync(TokensCssPath);
        var instructions = await BuildRenderInstructionsAsync(promptLoader, brandHandoff, groundTruthHtml, tokensCss);

        var target = Targets[2]; // DeepSeek-V4-Flash
        var authPolicy = new BearerTokenPolicy(new AzureIdentityTokenProvider(BuildCredential()), "https://ai.azure.com/.default");
        var client = new OpenAIClient(authPolicy, new OpenAIClientOptions { Endpoint = new Uri(target.Endpoint.TrimEnd('/') + "/openai/v1/"), NetworkTimeout = TimeSpan.FromMinutes(5) } /* [BUG FOUND LIVE, 2026-09-01] SDK default is 100s - too short for a large-document render/review call, see MafAgentFactory.cs's own note */);
        IChatClient chatClient = client.GetResponsesClient().AsIChatClient(target.Deployment);

        // Same message shape MafReportHtmlAgent.RenderAsync builds - reproduced here (not
        // called through it) so the raw ChatResponse is reachable.
        var payload = JsonSerializer.Serialize(
            new
            {
                composition_plan = snapshot.Plan,
                narrative = snapshot.Narrative,
                tenant_name = snapshot.TenantName,
                report_type = snapshot.ReportType,
                generated_at = snapshot.GeneratedAtUtc,
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        var message = "Render this approved report as a single self-contained HTML document:\n" + payload;

        foreach (var maxTokens in new[] { 16000, 32000, 65536 })
        {
            var chatOptions = new ChatOptions { Instructions = instructions, ResponseFormat = ChatResponseFormat.Text, MaxOutputTokens = maxTokens };
            var response = await chatClient.GetResponseAsync(message, chatOptions);
            var text = response.Text ?? "";
            var endsClean = text.TrimEnd().EndsWith("</html>", StringComparison.OrdinalIgnoreCase);
            output.WriteLine(
                $"maxOutputTokens={maxTokens}: finishReason={response.FinishReason}, chars={text.Length}, " +
                $"inputTokens={response.Usage?.InputTokenCount}, outputTokens={response.Usage?.OutputTokenCount}, endsWithClosingHtml={endsClean}");
        }
    }

    /// <summary>
    /// DIAGNOSTIC ONLY - same raw-IChatClient technique as DiagnoseDeepSeekTruncationAsync,
    /// pointed at gpt-5.6-terra, to get a real input/output token SPLIT (AgentRunResponse from
    /// the normal agent path only exposes the combined total, which is all comparison-summary.json
    /// has). Three calls: iteration 0 with plain instructions, iterations 1-2 with the SAME real
    /// issues text terra's own actual run produced (captured verbatim from
    /// comparison-summary.json, not invented) appended - reproducing the exact shape
    /// RenderAndReviewAsync sends on a retry, so the input-token growth from iteration to
    /// iteration is real, not estimated.
    /// </summary>
    [Fact]
    public async Task DiagnoseTerraCostAsync()
    {
        if (!File.Exists(SnapshotPath))
            throw new InvalidOperationException($"No snapshot at {SnapshotPath} - run CaptureSnapshotAsync once first.");
        var snapshot = JsonSerializer.Deserialize<ReportSnapshot>(await File.ReadAllTextAsync(SnapshotPath), SnapshotJsonOptions)
            ?? throw new InvalidOperationException("Snapshot deserialised to null.");

        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddInsightsPaidReportAgents(configuration);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var promptLoader = sp.GetRequiredService<IPromptLoader>();
        var brandHandoff = await File.ReadAllTextAsync(BrandHandoffPath);
        var groundTruthHtml = await File.ReadAllTextAsync(GroundTruthHtmlPath);
        var tokensCss = await File.ReadAllTextAsync(TokensCssPath);
        var baseInstructions = await BuildRenderInstructionsAsync(promptLoader, brandHandoff, groundTruthHtml, tokensCss);

        var target = Targets[1]; // gpt-5.6-terra
        var authPolicy = new BearerTokenPolicy(new AzureIdentityTokenProvider(BuildCredential()), "https://ai.azure.com/.default");
        var client = new OpenAIClient(authPolicy, new OpenAIClientOptions { Endpoint = new Uri(target.Endpoint.TrimEnd('/') + "/openai/v1/"), NetworkTimeout = TimeSpan.FromMinutes(5) } /* [BUG FOUND LIVE, 2026-09-01] SDK default is 100s - too short for a large-document render/review call, see MafAgentFactory.cs's own note */);
        IChatClient chatClient = client.GetResponsesClient().AsIChatClient(target.Deployment);

        var payload = JsonSerializer.Serialize(
            new
            {
                composition_plan = snapshot.Plan,
                narrative = snapshot.Narrative,
                tenant_name = snapshot.TenantName,
                report_type = snapshot.ReportType,
                generated_at = snapshot.GeneratedAtUtc,
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        var message = "Render this approved report as a single self-contained HTML document:\n" + payload;

        // Verbatim from the real comparison-summary.json run - the actual issues terra's own
        // reviewer sent back on a retry, not a made-up example.
        const string realIssuesFromLastRun = """
            [{"Rule":"Font is Poppins only; no other font families allowed","Problem":"The page uses system fonts instead of Poppins.","Fix":"Load self-hosted Poppins via @fontsource and set font-family: \"Poppins\", sans-serif globally."},{"Rule":"Sizes should be authored via token ladder vars","Problem":"Hardcoded px/rem sizes appear instead of token variables.","Fix":"Replace hardcoded values with the contract's token variables."}]
            """;

        long cumulativeInput = 0, cumulativeOutput = 0;
        for (var i = 0; i < 3; i++)
        {
            var instructions = i == 0
                ? baseInstructions
                : baseInstructions + $"\n\n---\n\nA PREVIOUS ATTEMPT WAS REJECTED. Fix every issue below and re-render the WHOLE document:\n{realIssuesFromLastRun}";
            var chatOptions = new ChatOptions { Instructions = instructions, ResponseFormat = ChatResponseFormat.Text, MaxOutputTokens = 16000 };
            var response = await chatClient.GetResponseAsync(message, chatOptions);
            var inTok = response.Usage?.InputTokenCount ?? 0;
            var outTok = response.Usage?.OutputTokenCount ?? 0;
            cumulativeInput += inTok;
            cumulativeOutput += outTok;
            output.WriteLine(
                $"iteration {i}: finishReason={response.FinishReason}, inputTokens={inTok}, outputTokens={outTok}, " +
                $"chars={response.Text?.Length ?? 0}, cumulativeInput={cumulativeInput}, cumulativeOutput={cumulativeOutput}");
        }
        output.WriteLine($"TOTALS: input={cumulativeInput}, output={cumulativeOutput}, combined={cumulativeInput + cumulativeOutput}");
    }

    /// <summary>
    /// DIAGNOSTIC ONLY - re-runs QA + the reviewer (cheap trusted model, NOT terra) against
    /// whatever HTML is currently saved at gpt-5.6-terra.html, to print the actual issue text.
    /// The main render loop only logs review.Issues.Count, not the messages - and re-rendering
    /// with terra again just to read the last iteration's issues would burn another ~$8-25 for
    /// information already sitting on disk.
    /// </summary>
    [Fact]
    public Task DiagnoseTerraReviewIssuesAsync() => DiagnoseReviewIssuesAsync("gpt-5.6-terra.html");

    [Fact]
    public Task DiagnoseHolisticReviewIssuesAsync() => DiagnoseReviewIssuesAsync("gpt-5.6-terra-05_report_html_holistic.html");

    [Fact]
    public Task DiagnoseFixedHolisticReviewIssuesAsync() => DiagnoseReviewIssuesAsync("gpt-5.6-terra-05_report_html_fixed_holistic.html");

    private async Task DiagnoseReviewIssuesAsync(string htmlFileName)
    {
        var htmlPath = Path.Combine(LabRoot, htmlFileName);
        var html = await File.ReadAllTextAsync(htmlPath);

        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddInsightsPaidReportAgents(configuration);
        services.AddTransient<PlaywrightQaActivity>();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var promptLoader = sp.GetRequiredService<IPromptLoader>();
        var promptFileForContract = htmlFileName.Contains("fixed_holistic") ? "05_report_html_fixed_holistic.md" : "05_report_html.md";
        var (brandHandoff, groundTruthHtml) = await LoadGroundTruthAndContractAsync(promptLoader, promptFileForContract);
        var reviewerAgent = BuildReviewerAgent(brandHandoff, groundTruthHtml);
        var referenceScreenshot = await RenderScreenshotAsync(sp.GetRequiredService<Microsoft.Playwright.IBrowser>(), groundTruthHtml);

        var qaActivity = sp.GetRequiredService<PlaywrightQaActivity>();
        var qaResult = await qaActivity.RunAsync(new PlaywrightQaInput(html));
        output.WriteLine($"QA: hasIssues={qaResult.Result.HasIssues}, consoleErrors={qaResult.Result.ConsoleErrors.Count}, horizontalOverflow={qaResult.Result.HasHorizontalOverflow}");

        var review = await ReviewAsync(reviewerAgent, html, qaResult.Result, referenceScreenshot);
        output.WriteLine($"approved={review.Approved}, issues={review.Issues.Count}");
        foreach (var issue in review.Issues)
            output.WriteLine($"  [{issue.Rule}] {issue.Problem} -> FIX: {issue.Fix}");
    }

    /// <summary>
    /// DIAGNOSTIC ONLY - empirically tests whether the models actually reachable from here can
    /// see an image at all, before building a real vision-review pass on top of one. Not guessed
    /// from a model name - gpt-5.2/gpt-5.4/gpt-5.6-* are not real documented OpenAI releases as of
    /// this codebase's knowledge, so "does it support vision" is only answerable by actually
    /// sending it the real screenshot and reading what comes back. gpt-4o IS a known real vision
    /// model, but (confirmed via `az cognitiveservices account deployment list`) it is deployed
    /// on trpl-prod-saas-ai-2 - the SAME VNet-restricted resource already blocking gpt-5.6-sol -
    /// so it is unreachable from here for the same network-firewall reason, not a code problem.
    /// </summary>
    [Fact]
    public async Task ProbeVisionCapabilityAsync()
    {
        var pngPath = Path.Combine(LabRoot, "gpt-5.6-terra.png");
        var pngBytes = await File.ReadAllBytesAsync(pngPath);
        var configuration = BuildConfiguration();
        var question = "Look at this screenshot of a compliance report. Focus on the donut chart in the top-left corner. Does the percentage number inside it visually overlap or collide with the colored ring around it? Answer in one sentence.";

        // Probe 1: gpt-5.2, the existing trusted reviewer model - API key auth, cheapest to test.
        {
            var endpoint = configuration["Llm:Maf:Endpoint"]!;
            var model = configuration["Llm:Maf:Model"]!;
            var apiKey = configuration["Llm:Maf:ApiKey"]!;
            var client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(endpoint), NetworkTimeout = TimeSpan.FromMinutes(5) });
            IChatClient chatClient = client.GetResponsesClient().AsIChatClient(model);
            try
            {
                var message = new ChatMessage(ChatRole.User, [new TextContent(question), new DataContent(pngBytes, "image/png")]);
                var response = await chatClient.GetResponseAsync([message]);
                output.WriteLine($"[{model}] VISION OK: {response.Text}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"[{model}] VISION FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Probe 2: gpt-5.6-terra, Entra ID auth - the model actually reachable for rendering.
        {
            var target = Targets[1];
            var authPolicy = new BearerTokenPolicy(new AzureIdentityTokenProvider(BuildCredential()), "https://ai.azure.com/.default");
            var client = new OpenAIClient(authPolicy, new OpenAIClientOptions { Endpoint = new Uri(target.Endpoint.TrimEnd('/') + "/openai/v1/"), NetworkTimeout = TimeSpan.FromMinutes(5) } /* [BUG FOUND LIVE, 2026-09-01] SDK default is 100s - too short for a large-document render/review call, see MafAgentFactory.cs's own note */);
            IChatClient chatClient = client.GetResponsesClient().AsIChatClient(target.Deployment);
            try
            {
                var message = new ChatMessage(ChatRole.User, [new TextContent(question), new DataContent(pngBytes, "image/png")]);
                var response = await chatClient.GetResponseAsync([message]);
                output.WriteLine($"[{target.Label}] VISION OK: {response.Text}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"[{target.Label}] VISION FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// DIAGNOSTIC ONLY - both vision models in ProbeVisionCapabilityAsync said "no overlap" for
    /// the donut, which contradicts the CSS-math read (--fs-di-score resolves to 2.3rem, ~37 user
    /// units, inside a 120-unit viewBox with ~42-unit inner clear radius - "84.7%" at that size
    /// should be wider than the inner circle). Rather than trust either a vision model's impression
    /// or hand math, this measures the REAL rendered geometry in the actual headless Chromium via
    /// getBBox() - the only source of truth for "does element A's rendered box overlap element B's".
    /// </summary>
    [Fact]
    public async Task MeasureDonutOverlapAsync()
    {
        var htmlPath = Path.Combine(LabRoot, "gpt-5.6-terra.html");
        var html = await File.ReadAllTextAsync(htmlPath);

        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddInsightsPaidReportAgents(configuration);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var browser = scope.ServiceProvider.GetRequiredService<Microsoft.Playwright.IBrowser>();

        var page = await browser.NewPageAsync(new Microsoft.Playwright.BrowserNewPageOptions { ViewportSize = new Microsoft.Playwright.ViewportSize { Width = 1200, Height = 800 } });
        try
        {
            await page.SetContentAsync(html, new Microsoft.Playwright.PageSetContentOptions { WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle });

            var result = await page.EvaluateAsync<string>("""
                () => {
                    const num = document.querySelector('.donut-number');
                    const label = document.querySelector('.donut-label');
                    const ring = document.querySelector('.donut circle:nth-of-type(2)') || document.querySelector('.donut circle');
                    if (!num || !ring) return JSON.stringify({ error: 'elements not found', hasNum: !!num, hasLabel: !!label, hasRing: !!ring });
                    const numBox = num.getBBox();
                    const labelBox = label ? label.getBBox() : null;
                    const ringBox = ring.getBoundingClientRect();
                    const svg = num.ownerSVGElement;
                    const svgBox = svg.getBoundingClientRect();
                    // convert SVG user-space bbox to real screen pixels using the svg's CTM
                    const ctm = num.getScreenCTM();
                    const toScreen = (x, y) => { const p = svg.createSVGPoint(); p.x = x; p.y = y; return p.matrixTransform(ctm); };
                    const numTL = toScreen(numBox.x, numBox.y);
                    const numBR = toScreen(numBox.x + numBox.width, numBox.y + numBox.height);
                    let labelScreen = null;
                    if (labelBox) {
                        const lTL = toScreen(labelBox.x, labelBox.y);
                        const lBR = toScreen(labelBox.x + labelBox.width, labelBox.y + labelBox.height);
                        labelScreen = { left: lTL.x, top: lTL.y, right: lBR.x, bottom: lBR.y };
                    }
                    const numScreen = { left: numTL.x, top: numTL.y, right: numBR.x, bottom: numBR.y };
                    const ringInnerRadiusPx = (ringBox.width / 2); // outer edge; ring stroke eats into this
                    const ringCenter = { x: ringBox.left + ringBox.width / 2, y: ringBox.top + ringBox.height / 2 };
                    const numWidthPx = numScreen.right - numScreen.left;
                    const numHalfDiag = numWidthPx / 2;
                    let numAndLabelOverlap = false;
                    if (labelScreen) {
                        numAndLabelOverlap = numScreen.bottom > labelScreen.top;
                    }
                    return JSON.stringify({
                        numScreenBox: numScreen,
                        labelScreenBox: labelScreen,
                        ringOuterDiameterPx: ringBox.width,
                        ringCenter,
                        numWidthExceedsRingDiameter: numWidthPx > ringBox.width,
                        numWidthPx,
                        numberAndLabelVerticallyOverlap: numAndLabelOverlap
                    }, null, 2);
                }
                """);
            output.WriteLine(result);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// DIAGNOSTIC ONLY, no LLM call - reads whatever is currently saved at gpt-5.6-terra.html and
    /// runs it through the real NormalizeActivity to print the EXACT violation text. The main loop
    /// only logs ex.InternalDiagnostics.Count, not the messages themselves.
    /// </summary>
    [Fact]
    public async Task DiagnoseTerraNormalizerViolationAsync()
    {
        var htmlPath = Path.Combine(LabRoot, "gpt-5.6-terra.html");
        var html = await File.ReadAllTextAsync(htmlPath);
        var normalizeActivity = new NormalizeActivity();
        try
        {
            var result = await normalizeActivity.RunAsync(new NormalizeInput(html));
            output.WriteLine($"NO VIOLATION - normalized cleanly, {result.Html.Length} chars.");
        }
        catch (OrchestrationRefusedException ex)
        {
            output.WriteLine($"{ex.InternalDiagnostics.Count} violation(s):");
            foreach (var d in ex.InternalDiagnostics)
                output.WriteLine($"  - {d}");
        }
    }
}
