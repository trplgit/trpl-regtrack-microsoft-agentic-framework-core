namespace Insights.Data;

/// <summary>
/// [ADDED 2026-09-23] Hard-terminates a running report generation - API_CONTRACTS.md has no
/// endpoint for this yet; RunEndpoints.cs's new POST .../cancel is the first caller. Deliberately
/// a HARD terminate (DTFx TerminateInstanceAsync), not a cooperative flag an orchestrator polls
/// between activities - explicit product choice: simple, immediate, one call. It can land
/// mid-activity (e.g. mid an LLM call or mid a blob write) - DurableTaskRunStatusReader.MapStatus
/// already treats DTFx's Terminated status as "failed", same as any other crashed run, so nothing
/// downstream needed to change to report it correctly.
/// </summary>
public interface IInsightsRunCanceller
{
    /// <summary>
    /// Returns true if a live (non-terminal) instance was found and terminated, false if none was
    /// found - callers that need to distinguish "already finished" from "never existed" check
    /// IRunStatusReader first, same pattern the stream endpoint already uses.
    /// </summary>
    Task<bool> CancelAsync(string runId, string reason, CancellationToken cancellationToken = default);
}
