using Microsoft.Extensions.AI;

namespace Insights.Agents;

/// <summary>
/// Pulls the vendor's own reasoning-summary text back out of an agent call, when one was
/// requested (MafAgentFactory sets ResponseReasoningSummaryVerbosity.Auto) and the model actually
/// returned one. Reads the provider-agnostic <see cref="TextReasoningContent"/> MEAI translates
/// OpenAI's ReasoningResponseItem into - not the OpenAI-specific type directly - so this keeps
/// working unchanged if the provider wiring in MafAgentFactory ever moves to Claude (CLAUDE.md
/// 3.2/7's documented long-term provider), since Anthropic's own MEAI adapter maps extended
/// thinking into the same content type.
///
/// This is a SUMMARY the model wrote about its own reasoning, never the raw chain-of-thought -
/// OpenAI's own terms forbid extracting raw reasoning by any other means, so there is no "fuller"
/// version of this to reach for.
/// </summary>
internal static class ReasoningSummaryExtractor
{
    public static string? Extract(IEnumerable<ChatMessage> messages)
    {
        var parts = messages
            .SelectMany(m => m.Contents)
            .OfType<TextReasoningContent>()
            .Select(c => c.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
