namespace Insights.Domain;

/// <summary>
/// Rolls up the N sub-report statuses under one fan-out reqId (see RunEndpoints.cs) into one
/// combined status for the frontend's request-level progress badge.
///
/// [PRODUCT DECISION, 2026-09-11] Worst-first priority: error > in_progress > queued > completed.
/// A single failed dimension surfaces as "error" immediately, even while siblings are still
/// running or already complete - never hidden behind an optimistic in_progress/completed. This
/// is a SEPARATE vocabulary from InsightsRunStatus's own ("queued"/"running"/"complete"/"failed",
/// the single-run wire contract) - the combined status is a new concept with its own four values,
/// not required to match the per-run spelling.
/// </summary>
public static class RequestStatusAggregator
{
    public static string Aggregate(IReadOnlyList<string> subStatuses)
    {
        if (subStatuses.Count == 0)
            throw new ArgumentException("Cannot aggregate an empty status list - a reqId always has at least one runId.", nameof(subStatuses));

        if (subStatuses.Contains("failed"))
            return "error";

        if (subStatuses.Contains("running"))
            return "in_progress";

        if (subStatuses.Contains("queued"))
            return "queued";

        return "completed";
    }
}
