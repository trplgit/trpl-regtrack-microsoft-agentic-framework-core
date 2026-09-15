namespace Insights.Domain;

/// <summary>
/// Design doc Sec.4.4's priority lanes for the shared LLM concurrency gate, highest first. Free
/// weekly digest is deliberately NOT a third value here - it never contends for this gate at all
/// (a separate, small IClaudeClient budget, Sec.4.4/Sec.10: "must not contend with paid traffic"),
/// so there is nothing to rank it against. Only the two lanes that actually share
/// LlmConcurrencyGate's slots need a rank.
///
/// Numeric order IS priority order (lower value drains first) - LlmConcurrencyGate relies on this
/// to pick which waiter queue to check first, so do not reorder these without updating it.
/// </summary>
public enum LlmCallPriority
{
    /// <summary>A human clicked Generate and is waiting. Always drains first (Sec.4.4 item 1).</summary>
    Interactive = 0,

    /// <summary>Scheduled keep-warm (paid_batch). Drains only once no interactive call is waiting (Sec.4.4 item 2).</summary>
    Batch = 1,
}
