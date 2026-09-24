namespace Insights.Data;

/// <summary>
/// [ADDED 2026-09-23] Backed by sql/34_tool_invocation_log.sql - answers "did the agent actually
/// call fetch_scoped_sql_data or write_tenant_memory for this run" from a permanent record, not a
/// guess from CallsMade after the fact. Same shape and reasoning as IAgentReasoningRecorder: a
/// side channel deliberately separate from dt.Payloads (the Durable Task hub's own storage, slated
/// for a future purge).
/// </summary>
public interface IToolInvocationRecorder
{
    /// <summary>
    /// Appends one row. Not idempotent on (RunId, ToolName) - ReadOnlySqlFetchTool alone allows up
    /// to 3 calls in one narrate call, each a genuinely distinct query worth its own row.
    /// </summary>
    Task RecordAsync(
        string? runId, string stage, string toolName, string detail, bool success, int? resultLength,
        CancellationToken cancellationToken = default);

    /// <summary>No-op implementation - used when tool-invocation capture isn't configured.</summary>
    public static IToolInvocationRecorder Null { get; } = new NullRecorder();

    private sealed class NullRecorder : IToolInvocationRecorder
    {
        public Task RecordAsync(
            string? runId, string stage, string toolName, string detail, bool success, int? resultLength,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
