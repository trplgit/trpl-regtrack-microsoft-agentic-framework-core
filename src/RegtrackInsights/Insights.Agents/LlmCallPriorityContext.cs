using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// Carries the current call's <see cref="LlmCallPriority"/> down into
/// <see cref="ConcurrencyGatedChatClient"/> without changing MAF's <c>AIAgent.RunAsync</c> call
/// shape or any of the IReportHtmlAgent-style interfaces. Every paid agent is built ONCE as
/// a singleton (see LlmConcurrencyGate's doc comment) and shared across every run regardless of
/// lane, so priority cannot be baked into the chat client at construction time - it has to travel
/// with the call itself.
///
/// AsyncLocal is safe here specifically because DTFx runs one activity invocation as ONE
/// self-contained async call (AsyncTaskActivity.ExecuteAsync -> RunAsync -> ...await the agent...),
/// never split across a Task.Run or a fresh unrelated async flow - so the value set at the top of
/// an activity's RunAsync is guaranteed to still be current by the time the call reaches
/// ConcurrencyGatedChatClient several awaits later, and cannot leak into the NEXT activity
/// invocation because each one gets its own async flow (and therefore its own AsyncLocal value)
/// from the runtime, not from anything this class does.
///
/// Defaults to Interactive when nothing has pushed a value - every call site that predates the
/// priority lanes (unit/manual tests, the CLI trigger in InsightsRunOnceWorker) never calls
/// <see cref="Push"/>, and Interactive is what they all meant before this existed: unconditional
/// admission, same as the old FIFO gate gave everyone.
/// </summary>
public static class LlmCallPriorityContext
{
    private static readonly AsyncLocal<LlmCallPriority?> Current = new();

    public static LlmCallPriority CurrentOrDefault => Current.Value ?? LlmCallPriority.Interactive;

    /// <summary>
    /// <code>using var _ = LlmCallPriorityContext.Push(input.Priority);</code>
    /// Restores whatever was current before on Dispose - nesting is safe, though nothing in this
    /// codebase currently nests a priority push inside another.
    /// </summary>
    public static IDisposable Push(LlmCallPriority priority)
    {
        var previous = Current.Value;
        Current.Value = priority;
        return new Popper(previous);
    }

    private sealed class Popper(LlmCallPriority? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
