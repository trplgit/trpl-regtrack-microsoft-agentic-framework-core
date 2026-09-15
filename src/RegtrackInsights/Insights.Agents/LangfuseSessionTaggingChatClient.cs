using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace Insights.Agents;

/// <summary>
/// [ADDED 2026-09-14] Tags the real chat-call span with `langfuse.session.id` (LangFuse's own
/// attribute name for trace-to-session grouping - not an OTel standard, LangFuse-specific) so every
/// real LLM call belonging to one report run shows up as one session in the UI.
///
/// MUST be the INNERMOST wrapper, placed BETWEEN the raw provider client and
/// OpenTelemetryChatClient - not outside it. OpenTelemetryChatClient starts its Activity, THEN
/// calls its inner client, THEN stops the Activity once that call returns; Activity.Current is only
/// the real chat span while code is running INSIDE that window. A wrapper placed outside
/// OpenTelemetryChatClient (like MeteredChatClient, deliberately outermost of the three chat-client
/// layers - see MafAgentFactory's own comments) runs before the span starts and after it has
/// already stopped, so tagging from there would silently tag nothing.
/// </summary>
public sealed class LangfuseSessionTaggingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (LangfuseSessionContext.CurrentSessionId is { } sessionId)
            Activity.Current?.SetTag("langfuse.session.id", sessionId);

        return base.GetResponseAsync(messages, options, cancellationToken);
    }
}
