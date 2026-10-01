using DurableTask.Core;
using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record BuildReasoningTraceInput(
    Guid ReportId, int TenantId, string ReportType, DateTime GeneratedAtUtc,
    string DimensionName, CompositionPlan Plan, IReadOnlyList<Assertion> Assertions, IReadOnlyList<Finding> Findings,
    string DimensionRowsJson, string? DimensionControlTotalsJson, string? DataQualityJson,
    // [ADDED 2026-09-27] The finished report the reader sees - its visible text and numbers are
    // what the testers' reasoning file must explain. Trailing optional: replay-safe, no bump.
    string? ReportHtml = null,
    // [ADDED 2026-09-27] Needed to fill the testers' database checks (ReasoningSourceMap) with
    // the exact user, tenant and period the report used. Trailing optional: replay-safe.
    int? UserId = null, DateTime? WindowStart = null, DateTime? WindowEnd = null,
    // [ADDED 2026-10-01] fixed_holistic (Entity) only - see ReasoningTraceBundle.AllDimensionDataJson's
    // own doc comment. Null for the freehand single-dimension path, unchanged behaviour.
    IReadOnlyDictionary<string, string>? AllDimensionDataJson = null);

// [FIX 2026-09-27] TotalTokens: the explainer's real token spend, so the orchestrator can add it to
// the tenant's recorded usage (it was billed but never counted). Trailing optional: a run recorded
// before this existed replays with 0 - its old behaviour.
public sealed record BuildReasoningTraceOutput(bool Written, long TotalTokens = 0);

/// <summary>
/// [ADDED 2026-09-26] Node 12b - runs after PersistActivity, only for the freehand single-dimension
/// path (the only shape with a real CompositionPlan + Assertions/Findings for one dimension - see
/// the orchestrator's own gating on freehandDimensionName). Builds the real ReasoningTraceBundle
/// (no new data - reuses exactly what this run already produced, plus a real read-back of
/// InsightsAgentReasoningLog/InsightsToolInvocationLog for this RunId), calls the gpt-4o-mini
/// explainer, and writes the result as a permanent plain-text sibling of the report's own blob
/// (IReasoningTraceStore - same container, deterministic path derived from ReportId/TenantId/
/// ReportType/GeneratedAtUtc, the SAME path ReportContentService re-derives at view time).
///
/// FAILS SOFT, ALWAYS - this is an internal QA aid, never allowed to fail or delay a real report.
/// Every exception is caught and logged; the report has already been persisted by the time this
/// runs, so there is nothing to roll back and nothing the caller needs to react to. Matches the
/// same "never block the real report" stance TenantMemoryTool and the reasoning-log/tool-
/// invocation-log recorders already take elsewhere in this pipeline.
/// </summary>
public sealed class BuildReasoningTraceActivity(
    ILogger<BuildReasoningTraceActivity> logger,
    IReasoningExplainerAgent? explainerAgent = null,
    IAgentReasoningRecorder? reasoningRecorder = null,
    IToolInvocationRecorder? toolInvocationRecorder = null,
    IReasoningTraceStore? traceStore = null)
    : AsyncTaskActivity<BuildReasoningTraceInput, BuildReasoningTraceOutput>
{
    protected override Task<BuildReasoningTraceOutput> ExecuteAsync(TaskContext context, BuildReasoningTraceInput input) =>
        RunAsync(input, context.OrchestrationInstance.InstanceId);

    internal async Task<BuildReasoningTraceOutput> RunAsync(BuildReasoningTraceInput input, string runId)
    {
        if (explainerAgent is null || traceStore is null)
            return new BuildReasoningTraceOutput(Written: false);

        long explainTokens = 0;
        try
        {
            var reasoningLog = reasoningRecorder is null
                ? (IReadOnlyList<AgentReasoningLogEntry>)[]
                : await reasoningRecorder.GetForRunAsync(runId);
            var toolInvocations = toolInvocationRecorder is null
                ? (IReadOnlyList<ToolInvocationLogEntry>)[]
                : await toolInvocationRecorder.GetForRunAsync(runId);

            var dataQuality = string.IsNullOrEmpty(input.DataQualityJson)
                ? []
                : System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<DataQualityNote>>(input.DataQualityJson) ?? [];

            var reportText = input.ReportHtml is null ? null : ReportClaimExtractor.VisibleText(input.ReportHtml);
            // [ADDED 2026-10-01] fixed_holistic (Entity) ignores across all contributing dimensions'
            // rows, not one - see ReportClaimExtractor.NumbersToIgnore's dictionary overload.
            var ignoreNumbers = input.AllDimensionDataJson is not null
                ? ReportClaimExtractor.NumbersToIgnore(input.AllDimensionDataJson)
                : ReportClaimExtractor.NumbersToIgnore(input.DimensionRowsJson);
            var numbers = reportText is null ? null : ReportClaimExtractor.ExtractNumbers(reportText, ignoreNumbers);

            // [REMOVED 2026-09-28] The testers' database checks (ReasoningSourceMap SQL) are no longer
            // put in the file - the testing team works from formulas, not SQL (user decision). The
            // source maps stay, verified by SourceMapVerificationTests, for internal use.
            var bundle = new ReasoningTraceBundle(
                input.DimensionName, runId, input.Plan, input.Assertions, input.Findings,
                input.DimensionRowsJson, input.DimensionControlTotalsJson, dataQuality, reasoningLog, toolInvocations,
                reportText, numbers, input.AllDimensionDataJson);

            var explainResult = await explainerAgent.ExplainAsync(bundle);
            explainTokens = explainResult.TotalTokens;
            var markdown = AppendCompletenessCheck(explainResult.Value, numbers);

            var pathContext = new BlobPathContext(input.TenantId, input.ReportType, DateOnly.FromDateTime(input.GeneratedAtUtc), input.ReportId);
            await traceStore.WriteAsync(markdown, pathContext);

            logger.LogInformation("BuildReasoningTraceActivity: wrote reasoning trace for report {ReportId} ({Tokens} explainer tokens).", input.ReportId, explainResult.TotalTokens);
            return new BuildReasoningTraceOutput(Written: true, explainTokens);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BuildReasoningTraceActivity: failed to build/write the reasoning trace for report {ReportId} - the report itself is unaffected.", input.ReportId);
            return new BuildReasoningTraceOutput(Written: false, explainTokens);
        }
    }

    /// <summary>
    /// Code-checked, not model-checked: every number on the finished report must appear in the file.
    /// Anything the explainer skipped is listed for the tester instead of silently missing.
    /// </summary>
    internal static string AppendCompletenessCheck(string markdown, IReadOnlyList<string>? numbersOnReport)
    {
        if (numbersOnReport is null)
            return markdown;

        var missing = ReportClaimExtractor.FindUnexplained(numbersOnReport, markdown);
        var section = missing.Count == 0
            // [2026-09-29] Plain text, not Markdown - the file is served as .txt (prompt 08 v3).
            ? $"\n\nCHECK DONE BY CODE\n\nAll {numbersOnReport.Count} numbers shown on the report appear in this file.\n"
            : $"\n\nNUMBERS NOT EXPLAINED ABOVE - PLEASE CHECK THESE BY HAND (found by code)\n\n" +
              $"{missing.Count} of the {numbersOnReport.Count} numbers shown on the report are not explained in this file. " +
              "Find each one on the report and check it against the data:\n\n" +
              string.Join("\n", missing.Select(n => $"- {n}")) + "\n";
        return markdown.TrimEnd() + section;
    }
}
