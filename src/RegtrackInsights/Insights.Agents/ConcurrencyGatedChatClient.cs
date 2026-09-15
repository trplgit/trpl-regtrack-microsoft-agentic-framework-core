using Insights.Domain;
using Microsoft.Extensions.AI;

namespace Insights.Agents;

/// <summary>
/// Blocks until a slot is free on the shared <see cref="LlmConcurrencyGate"/> before letting a
/// call through, then releases it whether the call succeeds or throws (the `using` in
/// <see cref="LlmConcurrencyGate.AcquireAsync(LlmCallPriority,CancellationToken)"/>'s token handles
/// that). Wraps OUTERMOST in MafAgentFactory's decorator chain, so queue-wait time never pollutes
/// the OTel span's latency or MeteredChatClient's timing - both of those should measure the real
/// network call, not how long a burst of simultaneous runs spent waiting their turn for a slot.
///
/// Reads <see cref="LlmCallPriorityContext.CurrentOrDefault"/> rather than taking a priority
/// parameter - this class is built once, wrapped around one shared gate, and reused by every run
/// regardless of lane (see LlmConcurrencyGate's doc comment), so the lane has to come from ambient
/// context set by the activity that is calling in, not from anything fixed at construction time.
/// </summary>
public sealed class ConcurrencyGatedChatClient(IChatClient inner, LlmConcurrencyGate gate) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var _ = await gate.AcquireAsync(LlmCallPriorityContext.CurrentOrDefault, cancellationToken);
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }
}
