using System.Diagnostics;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-09-29] LangFuse tracing for the FREE digest's LLM calls (the "Insights Basic"
/// LangFuse project). The free digest talks to the model through <see cref="IClaudeClient"/>, not
/// MAF, so it never went through <see cref="MafAgentFactory"/>'s instrumented chat client and no
/// free-digest call ever reached LangFuse. <see cref="TracingClaudeClient"/> closes that gap.
///
/// <para>Own <see cref="ActivitySource"/>, so ObservabilityRegistration can route these spans to a
/// SEPARATE LangFuse project (its own key pair) while the paid report keeps its existing one. The
/// paid pipeline's tracer never subscribes to this source.</para>
/// </summary>
public static class FreeDigestTelemetry
{
    public const string ActivitySourceName = "Insights.FreeDigest.Llm";

    internal static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly AsyncLocal<FreeDigestTraceContext?> Current = new();

    public static FreeDigestTraceContext? CurrentContext => Current.Value;

    /// <summary>
    /// Tags every LLM call made inside the scope with the tenant, user and subject it belongs to -
    /// same AsyncLocal-per-call pattern as <see cref="LangfuseSessionContext"/>.
    /// <code>using var _ = FreeDigestTelemetry.Push(new FreeDigestTraceContext(...));</code>
    /// </summary>
    public static IDisposable Push(FreeDigestTraceContext context)
    {
        var previous = Current.Value;
        Current.Value = context;
        return new Popper(previous);
    }

    private sealed class Popper(FreeDigestTraceContext? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <param name="Lane">email | card</param>
/// <param name="Subject">overview | users | location | act | licence</param>
/// <param name="WeekEnding">The Sunday the edition belongs to - the LangFuse session is one tenant's week.</param>
public sealed record FreeDigestTraceContext(int TenantId, long UserId, string Lane, string Subject, DateOnly WeekEnding)
{
    public string SessionId => $"basic-{TenantId}-{WeekEnding:yyyy-MM-dd}";

    public string TraceName => $"{Lane}:{Subject}";

    /// <summary>The calendar month the edition belongs to (MonthlyDigestCalendar keys it on the Sunday).</summary>
    public string Month => WeekEnding.ToString("yyyy-MM");
}
