using System.Text.Json;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// Result of one call to <see cref="InsightNarrativeWriter.WriteAsync"/>. Empty
/// <see cref="Headline"/>/<see cref="Explanation"/> with <see cref="Source"/> =
/// <see cref="FreeDigestSource.Fallback"/> means the caller must substitute the deterministic
/// fallback (InsightFallbackNarrative) - same "never blocks the run" shape FreeMonthlyDigestWriter uses.
/// </summary>
public sealed record InsightNarrativeDraft(string Headline, string Explanation, FreeDigestSource Source, string? SkippedReason, int InputTokens, int OutputTokens)
{
    public static InsightNarrativeDraft Fallback(int inputTokens, int outputTokens, string reason) =>
        new(string.Empty, string.Empty, FreeDigestSource.Fallback, reason, inputTokens, outputTokens);
}

/// <summary>
/// Writes the "current insight" headline + explanation for the per-user insight JSON (ADR-0002,
/// 2026-09-11) - prompts/07_insight_json_narrative.md. Same one-call, non-reflective, capped shape
/// as <see cref="FreeMonthlyDigestWriter"/>; <see cref="InsightFocus"/> is computed and fixed BEFORE this
/// call, never chosen by the model.
/// </summary>
public sealed class InsightNarrativeWriter(IClaudeClient client, IPromptLoader promptLoader)
{
    private const string PromptFileName = "07_insight_json_narrative.md";
    private const double CharsPerToken = 4.0;
    private const int MinCompletionTokens = 60;
    private const int MaxCompletionTokens = 200;

    public async Task<InsightNarrativeDraft> WriteAsync(FreeDigestAggregates aggregates, InsightFocus focus, int tokenCap, CancellationToken cancellationToken = default)
    {
        var systemPrompt = await promptLoader.LoadAsync(PromptFileName, cancellationToken);
        var userMessage = JsonSerializer.Serialize(new
        {
            focus.SeverityBand,
            focus.Metric,
            focus.Value,
            focus.Denominator,
            focus.Remainder,
            aggregates.TotalActiveObligations,
            aggregates.BranchesInScope,
            aggregates.DueNext7,
            aggregates.CriticalDueNext7,
            aggregates.ImprisonmentDueNext7,
            aggregates.DueNext14,
            aggregates.DueNext30,
            aggregates.CriticalDueNext30,
            aggregates.ImprisonmentDueNext30,
            aggregates.LicencesLapsingNext30,
            aggregates.DistinctImprisonmentObligations,
            aggregates.BranchesWithUpcoming,
            aggregates.CompletedLast7,
        });

        var completionTokenBudget = CompletionTokenBudget(systemPrompt, userMessage, tokenCap);

        var result = await client.CompleteAsync(systemPrompt, userMessage, completionTokenBudget, cancellationToken);
        var totalTokens = result.InputTokens + result.OutputTokens;

        if (result.WasTruncated)
            return InsightNarrativeDraft.Fallback(result.InputTokens, result.OutputTokens, "response truncated at the token cap");

        if (totalTokens > tokenCap)
            return InsightNarrativeDraft.Fallback(result.InputTokens, result.OutputTokens, $"token usage {totalTokens} exceeded cap {tokenCap}");

        var parsed = ParseLines(result.Text);
        if (parsed is null)
            return InsightNarrativeDraft.Fallback(result.InputTokens, result.OutputTokens, "response did not contain both a HEADLINE and an EXPLANATION line");

        return new InsightNarrativeDraft(parsed.Value.Headline, parsed.Value.Explanation, FreeDigestSource.Llm, null, result.InputTokens, result.OutputTokens);
    }

    /// <summary>Same estimate-then-clamp shape as FreeMonthlyDigestWriter.CompletionTokenBudget - see that class's own doc comment for why the prompt cost is measured, not guessed.</summary>
    internal static int CompletionTokenBudget(string systemPrompt, string userMessage, int tokenCap)
    {
        var estimatedPromptTokens = (int)Math.Ceiling((systemPrompt.Length + userMessage.Length) / CharsPerToken);
        var remaining = tokenCap - estimatedPromptTokens;
        return Math.Clamp(remaining, MinCompletionTokens, MaxCompletionTokens);
    }

    private static (string Headline, string Explanation)? ParseLines(string text)
    {
        string? headline = null;
        string? explanation = null;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("HEADLINE:", StringComparison.OrdinalIgnoreCase))
                headline = trimmed["HEADLINE:".Length..].Trim();
            else if (trimmed.StartsWith("EXPLANATION:", StringComparison.OrdinalIgnoreCase))
                explanation = trimmed["EXPLANATION:".Length..].Trim();
        }

        return headline is { Length: > 0 } && explanation is { Length: > 0 } ? (headline, explanation) : null;
    }
}
