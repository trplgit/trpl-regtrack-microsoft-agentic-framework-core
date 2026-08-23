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
            Message: status == "failed" ? FailureMessage : null);
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
