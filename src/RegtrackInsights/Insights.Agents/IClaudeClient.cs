namespace Insights.Agents;

/// <summary>Result of a single, non-streaming Claude completion call.</summary>
public sealed record ClaudeCompletionResult(string Text, int InputTokens, int OutputTokens, bool WasTruncated);

/// <summary>
/// Thin wrapper over a single Claude Messages API call. Deliberately not the full
/// MAF (Microsoft.Agents.AI) workflow graph - that is Phase 1d (build order step 11).
/// The free digest "skips agentic composition" (design doc Section 10.4) and needs only
/// one capped, non-streaming call, so it talks to the API directly.
/// </summary>
public interface IClaudeClient
{
    Task<ClaudeCompletionResult> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, CancellationToken cancellationToken = default);
}
