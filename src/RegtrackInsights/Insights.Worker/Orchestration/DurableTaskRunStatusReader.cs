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

    /// <summary>
    /// [ADDED 2026-09-30, FOUND LIVE] A run whose history was written under an orchestrator
    /// version this worker no longer registers (<see cref="InsightsReportOrchestrator.Version"/>
    /// bumped since the run started, e.g. 4.3 -> 4.4) can never be resumed by any pod running the
    /// new build - WorkerRegistration.cs registers exactly one version string, so there is no
    /// handler left for the old one once a version-bumping deploy lands. A run that has simply
    /// gone quiet for a long time looks the same from here regardless of whether its version
    /// still matches - the underlying symptom (a hung activity call the SQL provider's own
    /// retry/dequeue-count tracking never gives up on) is the same either way.
    ///
    /// Confirmed live: 3 real tenant-1285 runs stuck reporting "running" for 4+ hours after a
    /// version-bumping deploy landed mid-render - zero further history, no queued work item for
    /// any of them, cancel requests against them silently stuck in the same unreachable queue.
    /// </summary>
    private const int StaleMinutes = 30;

    /// <summary>
    /// [CHANGED 2026-09-30, code review finding on the multi-version dispatch design] Originally
    /// compared state.Version against ONLY InsightsReportOrchestrator.Version (the current one).
    /// Once a frozen version is kept registered alongside the current one (docs/superpowers/specs/
    /// 2026-09-30-orchestrator-multi-version-dispatch-design.md), every run the frozen class
    /// resumes would still fail that check and get reported "failed" the moment anyone polled it -
    /// defeating the whole point of freezing it (the run finishes for real; the user would be told
    /// it failed). Now checks registration-list MEMBERSHIP: any version WorkerRegistration still
    /// registers for this orchestrator name is not orphaned on version grounds, current or frozen.
    /// </summary>
    internal static bool IsOrphaned(OrchestrationState state, out string reason) =>
        IsOrphaned(state, Insights.Worker.WorkerRegistration.OrchestrationRegistrations, out reason);

    internal static bool IsOrphaned(
        OrchestrationState state,
        IReadOnlyList<(string Name, string Version, Type Type)> knownRegistrations,
        out string reason)
    {
        if (!knownRegistrations.Any(r => r.Name == state.Name && r.Version == state.Version))
        {
            reason = $"orchestrator version mismatch - '{state.Version}' is not registered by any " +
                $"currently-deployed worker version (run name={state.Name})";
            return true;
        }

        if (DateTime.UtcNow - state.LastUpdatedTime > TimeSpan.FromMinutes(StaleMinutes))
        {
            reason = $"no progress for over {StaleMinutes} minutes (last update {state.LastUpdatedTime:O})";
            return true;
        }

        reason = "";
        return false;
    }

    public async Task<InsightsRunStatus?> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        var state = await client.GetOrchestrationStateAsync(runId);
        if (state is null)
            return null;

        var status = MapStatus(state.OrchestrationStatus);

        // CLAUDE.md non-negotiable #2 (fail closed, fail loudly): report this as failed the
        // moment anyone asks, rather than leaving a false "running" spinner forever - no new
        // monitoring, no background job, just a truthful answer at read time. Scoped to "running"
        // only - a "queued" run legitimately has no progress yet, that is a capacity wait, not an
        // orphaned run, and is not what this check is for.
        if (status == "running" && IsOrphaned(state, out var orphanReason))
        {
            logger.LogError(
                "Report run {RunId} treated as failed - orphaned ({Reason}).", runId, orphanReason);
            status = "failed";
        }

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
