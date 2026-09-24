using System.Text.Json;
using DurableTask.Core;
using Insights.Data;
using Insights.Domain;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration;

/// <summary>
/// Reads run progress out of the Durable Task instance store.
///
/// The stage payload comes from <see cref="InsightsReportOrchestrator.GetStatus"/>, which DTFx
/// persists as the instance's custom status on every checkpoint. That is why this class only
/// deserializes and never computes: the orchestrator is the single authority on which stage a run
/// is in, and a second implementation here would drift from it silently.
/// </summary>
public sealed class DurableTaskRunStatusReader(
    TaskHubClient client,
    ILogger<DurableTaskRunStatusReader> logger) : IRunStatusReader
{
    /// <summary>Matches InsightsReportOrchestrator's StagesTotal. Reported when no custom status exists yet.</summary>
    private const int StagesTotal = 7;

    /// <summary>
    /// What a failed run tells the customer. Deliberately says nothing.
    ///
    /// [TRAP] spec §11.3: the real reason - a reconciliation variance, a scope audit violation, a
    /// dictionary gap - is internal diagnostics. It is logged below and alerted on; it must never
    /// reach the response body.
    /// </summary>
    private const string FailureMessage = "Report generation failed. Please try again.";

    public async Task<InsightsRunStatus?> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        var state = await client.GetOrchestrationStateAsync(runId);
        if (state is null)
            return null;

        var status = MapStatus(state.OrchestrationStatus);

        if (status == "failed")
        {
            /*  The one place the real reason is allowed to exist. Logged at Error so it reaches
                the alerting path (spec §13) rather than being discarded with the response.      */
            logger.LogError("Report run {RunId} failed. Internal detail: {Detail}", runId, state.Output);
        }

        var (stage, stagesComplete) = ParseCustomStatus(state.Status, runId);

        return new InsightsRunStatus(
            RunId: runId,
            Status: status,
            Stage: stage,
            StagesComplete: stagesComplete,
            StagesTotal: StagesTotal,
            Message: status == "failed" ? FailureMessage : null,
            ReportId: status == "complete" ? ParseReportId(state.Output, runId) : null,
            Dimension: ParseDimension(state.Input, runId));
    }

    /// <summary>
    /// [ADDED 2026-09-24] Reads the real dimension this run is for straight out of the
    /// orchestration's OWN stored input (DTFx persists whatever object CreateOrchestrationInstanceAsync
    /// was called with as InputText) - no new column, no new write path, this data was already
    /// there. RequestedDimensions is a single-element list for a fanned-out dimension_selection unit
    /// (RunEndpoints.cs's GenerateOneReportAsync - one orchestration instance per requested
    /// dimension) - real, verified, never the ORIGINAL caller's full multi-dimension list. Null/
    /// absent RequestedDimensions with ReportType fixed_holistic means "Entity", the same product-
    /// facing label ReportTypeRouter's own Entity-alone redirect already uses - never invented here,
    /// just read back. Same defensive stance as ParseReportId/ParseCustomStatus: unreadable input
    /// degrades to null, never throws past a status read.
    /// </summary>
    private string? ParseDimension(string? input, string runId)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        try
        {
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;

            if (root.TryGetProperty("RequestedDimensions", out var dimensions)
                && dimensions.ValueKind == JsonValueKind.Array && dimensions.GetArrayLength() > 0)
                return dimensions[0].GetString();

            if (root.TryGetProperty("ReportType", out var reportType)
                && reportType.GetString() == FixedHolisticComposition.ReportType)
                return "Entity";

            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Run {RunId} has an unreadable orchestration input; dimension will be reported as unknown.", runId);
            return null;
        }
    }

    /// <summary>
    /// PersistActivity's own output (PersistOutput.ReportId) becomes the orchestration's terminal
    /// Output - state.Output was already being fetched above (and logged on failure) but never
    /// read on success. Null rather than throwing on anything unreadable: a client that already
    /// has a "complete" status should never have the response fail underneath it because this one
    /// extra field could not be parsed - see this class's own doc comment on ParseCustomStatus for
    /// the same reasoning applied there.
    /// </summary>
    private string? ParseReportId(string? output, string runId)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.TryGetProperty("ReportId", out var reportIdElement)
                ? reportIdElement.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Run {RunId} completed but its Output could not be parsed for ReportId.", runId);
            return null;
        }
    }

    /// <summary>
    /// DTFx's status enum collapsed onto the contract's four values. Terminated maps to failed:
    /// from the customer's side an operator-cancelled run and a crashed one are the same event,
    /// and both leave the cooldown open (spec §4.6).
    /// </summary>
    private static string MapStatus(OrchestrationStatus status) => status switch
    {
        OrchestrationStatus.Pending => "queued",
        OrchestrationStatus.Running => "running",
        OrchestrationStatus.ContinuedAsNew => "running",
        OrchestrationStatus.Suspended => "running",
        OrchestrationStatus.Completed => "complete",
        OrchestrationStatus.Failed => "failed",
        OrchestrationStatus.Terminated => "failed",
        OrchestrationStatus.Canceled => "failed",
        _ => "running",
    };

    /// <summary>
    /// Deserializes the orchestrator's custom status. Returns nulls rather than throwing when it
    /// is absent or unreadable: a run that has been created but has not yet reached its first
    /// checkpoint genuinely has no stage, and that window must report "queued, no stage" instead
    /// of erroring a progress poll.
    /// </summary>
    private (string? Stage, int StagesComplete) ParseCustomStatus(string? customStatus, string runId)
    {
        if (string.IsNullOrWhiteSpace(customStatus))
            return (null, 0);

        try
        {
            using var document = JsonDocument.Parse(customStatus);
            var root = document.RootElement;

            var stage = root.TryGetProperty("stage", out var stageElement)
                ? stageElement.GetString()
                : null;

            var complete = root.TryGetProperty("stagesComplete", out var completeElement)
                ? completeElement.GetInt32()
                : 0;

            return (stage, complete);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Run {RunId} has an unreadable custom status; reporting progress as unknown.", runId);
            return (null, 0);
        }
    }
}
