using System.Text.Json;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// Writes the free weekly digest body from prompts/06_freetier_digest.md and the 13
/// pre-aggregated numbers - never raw rows, never a location/person/Act (prompt Section
/// "Your job"). One capped, non-reflective call; the paid tier's composition/narrative
/// reflection loop does not apply here (design doc Section 10.4).
/// </summary>
public sealed class FreeDigestWriter(IClaudeClient client, IPromptLoader promptLoader)
{
    private const string PromptFileName = "06_freetier_digest.md";

    /*  [BUG FOUND LIVE, 2026-09-11] `tokenCap` is the TOTAL (prompt + completion) budget per
        design doc 10.5 - it is NOT how many tokens the model may WRITE. Passing it straight
        through as the provider API's max_tokens (an OUTPUT-only limit) told the model it could
        write up to the WHOLE budget on top of a system prompt that already consumes most of it,
        so the total-budget check below discarded a well-formed, on-target-length email almost
        every call - not because the email was too long, but because the model had been handed
        output room it was never meant to have. Confirmed live: prompt+user message alone is
        ~1400-1460 tokens (06_freetier_digest.md is 5.4KB), completion for a near-zero-aggregate
        tenant only ~80-100 tokens - total 1520-1550, rejected every time against a 1500 cap that
        left it no room to exist at all. Every one of those rejected calls was pure waste: real
        LLM spend for an output that was then thrown away in favour of the free fallback.

        Fix: size the OUTPUT request from what is actually left of the budget AFTER the prompt,
        not from the whole budget. The prompt is already in hand before the call, so its cost is
        MEASURED (its own character count), not a fixed guess - a prompt edit is tracked
        automatically, never a number that quietly goes stale again. ~4 characters per token is
        the standard rule of thumb for English text (OpenAI's own guidance); CharsPerToken below
        matches it, so the estimate is not artificially padded in either direction - padding it
        low would waste completion headroom the model doesn't need, padding it high risks
        starving a legitimate email into truncation. Either way, the REAL, authoritative check
        stays the post-hoc totalTokens comparison two lines below using the provider's actual
        reported usage - if the estimate is ever wrong, this still fails closed to the fallback,
        exactly as before. Estimation error only costs efficiency, never correctness.            */
    private const double CharsPerToken = 4.0;
    private const int MinCompletionTokens = 150; // even the shortest legitimate (all-zero-aggregates) email is a full four short paragraphs - never starve it below what that needs.
    private const int MaxCompletionTokens = 600; // the prompt's own ~250-word target needs roughly 350-450 tokens including markdown bold markers; 600 is variance headroom, never "whatever budget happens to be left" - the model is never handed more room than the email could plausibly use.

    /// <summary>
    /// Returns null <c>Body</c> when the call would exceed <paramref name="tokenCap"/> (input
    /// + output) or the model truncated its response - the caller substitutes the fallback
    /// template rather than treating this as an error (spec Section 10.5: the email never
    /// fails to go out).
    /// </summary>
    public async Task<FreeDigestEmail> WriteAsync(FreeDigestAggregates aggregates, int tokenCap, CancellationToken cancellationToken = default)
    {
        var systemPrompt = await promptLoader.LoadAsync(PromptFileName, cancellationToken);
        var userMessage = JsonSerializer.Serialize(new
        {
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
            return new FreeDigestEmail(result.Text, FreeDigestSource.Fallback, "response truncated at the token cap", result.InputTokens, result.OutputTokens);

        if (totalTokens > tokenCap)
            return new FreeDigestEmail(result.Text, FreeDigestSource.Fallback, $"token usage {totalTokens} exceeded cap {tokenCap}", result.InputTokens, result.OutputTokens);

        return new FreeDigestEmail(result.Text, FreeDigestSource.Llm, null, result.InputTokens, result.OutputTokens);
    }

    /// <summary>
    /// How many OUTPUT tokens the model may use, given what the prompt itself is estimated to
    /// already cost against the total budget - never the whole <paramref name="tokenCap"/> (see
    /// this class's own doc comment). Clamped to [<see cref="MinCompletionTokens"/>,
    /// <see cref="MaxCompletionTokens"/>] so an unusually tight or loose cap never starves a
    /// legitimate email or hands the model room it has no reason to use.
    /// </summary>
    internal static int CompletionTokenBudget(string systemPrompt, string userMessage, int tokenCap)
    {
        var estimatedPromptTokens = (int)Math.Ceiling((systemPrompt.Length + userMessage.Length) / CharsPerToken);
        var remaining = tokenCap - estimatedPromptTokens;
        return Math.Clamp(remaining, MinCompletionTokens, MaxCompletionTokens);
    }
}
