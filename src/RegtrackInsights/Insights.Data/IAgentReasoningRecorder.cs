namespace Insights.Data;

/// <summary>One real row from dbo.InsightsAgentReasoningLog, read back for a given RunId.</summary>
public sealed record AgentReasoningLogEntry(string Stage, string ReasoningSummary, DateTime RecordedAtUtc);

/// <summary>
/// Backed by sql/32_agent_reasoning_log.sql - a permanent home for each agent's own summary of
/// its reasoning, deliberately separate from dt.Payloads (the Durable Task hub's storage), which
/// is slated for a future purge job. Null implementation for callers with nothing configured yet
/// and for tests that don't care about this side channel.
/// </summary>
public interface IAgentReasoningRecorder
{
    /// <summary>
    /// Appends one row. Not idempotent on (RunId, Stage) - a retry loop (RenderHtmlActivity's own
    /// up to 3 attempts) makes a genuinely new call each time, with its own genuinely new
    /// reasoning, so each real call gets its own row. Does nothing when
    /// <paramref name="reasoningSummary"/> is null or empty - no value logging an empty row.
    /// </summary>
    Task RecordAsync(string runId, string stage, string? reasoningSummary, CancellationToken cancellationToken = default);

    /// <summary>
    /// [ADDED 2026-09-26] Every reasoning row for one run, in call order - the read half, backing
    /// the reasoning-trace explainer. Empty (never null) when nothing was ever recorded for this
    /// RunId - not an error, most stages may not have requested a reasoning summary.
    /// </summary>
    Task<IReadOnlyList<AgentReasoningLogEntry>> GetForRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>No-op implementation - used when reasoning capture isn't configured.</summary>
    public static IAgentReasoningRecorder Null { get; } = new NullRecorder();

    private sealed class NullRecorder : IAgentReasoningRecorder
    {
        public Task RecordAsync(string runId, string stage, string? reasoningSummary, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AgentReasoningLogEntry>> GetForRunAsync(string runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentReasoningLogEntry>>([]);
    }
}
