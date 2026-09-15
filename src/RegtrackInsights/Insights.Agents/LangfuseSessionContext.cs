namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-09-14] Carries the current report run's real id (DTFx OrchestrationInstance.InstanceId)
/// down into <see cref="LangfuseSessionTaggingChatClient"/> without changing MAF's
/// <c>AIAgent.RunAsync</c> call shape - same problem, same fix, as
/// <see cref="LlmCallPriorityContext"/> (see that class's own doc comment for why AsyncLocal is
/// safe here: one DTFx activity invocation is one self-contained async call, so the value set at
/// the top of an activity's RunAsync is still current several awaits later at the real chat call,
/// and cannot leak into the next activity invocation).
///
/// Exists so every real LLM call belonging to ONE report run shows up as ONE session in LangFuse
/// (`langfuse.session.id`), instead of 5-9 unrelated-looking individual traces - LangFuse requires
/// this attribute on every span, not just a parent/resource-level tag, which is exactly what
/// AsyncLocal-per-call gives us.
/// </summary>
public static class LangfuseSessionContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? CurrentSessionId => Current.Value;

    /// <code>using var _ = LangfuseSessionContext.Push(runId);</code>
    public static IDisposable Push(string? runId)
    {
        var previous = Current.Value;
        Current.Value = runId;
        return new Popper(previous);
    }

    private sealed class Popper(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
