using DurableTask.Core;
using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record BuildReasoningTraceInput(
    Guid ReportId, int TenantId, string ReportType, DateTime GeneratedAtUtc,
    string DimensionName, CompositionPlan Plan, IReadOnlyList<Assertion> Assertions, IReadOnlyList<Finding> Findings,
    string DimensionRowsJson, string? DimensionControlTotalsJson, string? DataQualityJson);

public sealed record BuildReasoningTraceOutput(bool Written);

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

            var bundle = new ReasoningTraceBundle(
                input.DimensionName, runId, input.Plan, input.Assertions, input.Findings,
                input.DimensionRowsJson, input.DimensionControlTotalsJson, dataQuality, reasoningLog, toolInvocations);

            var explainResult = await explainerAgent.ExplainAsync(bundle);

            var pathContext = new BlobPathContext(input.TenantId, input.ReportType, DateOnly.FromDateTime(input.GeneratedAtUtc), input.ReportId);
            await traceStore.WriteAsync(explainResult.Value, pathContext);

            logger.LogInformation("BuildReasoningTraceActivity: wrote reasoning trace for report {ReportId} ({Tokens} explainer tokens).", input.ReportId, explainResult.TotalTokens);
            return new BuildReasoningTraceOutput(Written: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BuildReasoningTraceActivity: failed to build/write the reasoning trace for report {ReportId} - the report itself is unaffected.", input.ReportId);
            return new BuildReasoningTraceOutput(Written: false);
        }
    }
}
