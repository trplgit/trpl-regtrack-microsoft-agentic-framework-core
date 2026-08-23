namespace Insights.Agents;

/// <summary>Tokens billed by one LLM call.</summary>
/// <param name="Stage">
/// The agent that made the call - "composition", "narrative", "report_html". LOW CARDINALITY:
/// this becomes a metric tag, so it must be a closed set of stage names and must never carry a
/// tenant name, a user id or any customer data.
/// </param>
/// <param name="Model">The deployment/model name. Also a tag, also low cardinality.</param>
public sealed record LlmUsage(string Stage, string Model, long InputTokens, long OutputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// Where LLM spend is reported. Lives in Insights.Agents so the agent layer can report without
/// depending on the worker's metrics implementation - and so a manual-run test can pass a no-op.
///
/// [TRAP] Implementations MUST NOT throw. This is called after a successful, already-billed LLM
/// call; a metrics failure that propagated would discard a response the tenant has already paid
/// for, turning an observability problem into a spend problem.
/// </summary>
public interface ILlmUsageRecorder
{
    void Record(LlmUsage usage);

    /// <summary>Discards everything. The default when nobody wires metrics - a manual run, a test.</summary>
    public static ILlmUsageRecorder Null { get; } = new NullRecorder();

    private sealed class NullRecorder : ILlmUsageRecorder
    {
        public void Record(LlmUsage usage) { }
    }
}

/// <summary>
/// Raised when one LLM call bills more than its configured ceiling.
///
/// A refusal, not a fault: a paid report is 4 LLM calls plus up to 2 reflection loops, and
/// nothing else bounds how large any single one of them can get. The free digest already has
/// this ceiling (FreeDigestWriter falls back to the deterministic template); the paid path cannot
/// fall back to anything, so it refuses and the run fails visibly rather than billing silently.
/// </summary>
public sealed class LlmBudgetExceededException(LlmUsage usage, int capTokens)
    : Exception($"LLM call at stage '{usage.Stage}' billed {usage.TotalTokens} tokens, exceeding the per-call cap of {capTokens}. Refusing to continue.")
{
    public LlmUsage Usage { get; } = usage;
    public int CapTokens { get; } = capTokens;
}
