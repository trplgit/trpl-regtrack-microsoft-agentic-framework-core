using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// Versioned prompt file names for the monthly digest (spec Sec.10). Every file is write-once; a
/// revision ships as a new _vN file and config selects it - rollback is a config change.
/// </summary>
public static partial class FreeMonthlyPromptFiles
{
    public static string SharedRules() => "06_freetier_monthly_shared_rules.md";

    public static string ForSlot(MonthlyDigestSlot slot) => slot switch
    {
        MonthlyDigestSlot.Overview => "06a_freetier_monthly_overview.md",
        MonthlyDigestSlot.Users => "06b_freetier_monthly_users.md",
        MonthlyDigestSlot.Location => "06c_freetier_monthly_location.md",
        MonthlyDigestSlot.Act => "06d_freetier_monthly_act.md",
        MonthlyDigestSlot.Licence => "06e_freetier_monthly_licence.md",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null),
    };
}

/// <summary>
/// Writes one monthly digest body: shared rules + the slot's prompt as the system prompt, the
/// <see cref="FreeMonthlyDigestPrompt.UserMessage"/> as the only data. One capped, non-reflective call.
///
/// [BUG FOUND LIVE, 2026-09-11, in the weekly writer this replaced] The cap is a TOTAL (prompt +
/// completion) budget, NOT the provider's max_tokens. Passing the whole cap as max_tokens handed the
/// model output room on top of a prompt that already used most of the budget, so the post-hoc
/// total check discarded well-formed emails almost every call - real spend for nothing. So the
/// OUTPUT request is sized from what is left after the prompt (measured from its length at ~4
/// chars/token), clamped to [MinCompletionTokens, MaxCompletionTokens]; the provider's reported
/// usage is still the authoritative check, so a wrong estimate costs efficiency, never correctness.
/// </summary>
public sealed class FreeMonthlyDigestWriter(IClaudeClient client, IPromptLoader promptLoader)
{
    private const double CharsPerToken = 4.0;
    private const int MinCompletionTokens = 400;   // the shortest real monthly email (a no-licence Licence week) plus markdown.
    private const int MaxCompletionTokens = 1100;  // the overview's 360-word ceiling is ~500-600 tokens; the rest is variance headroom.

    public async Task<string> LoadSystemPromptAsync(
        MonthlyDigestSlot slot, CancellationToken cancellationToken = default)
    {
        var shared = await promptLoader.LoadAsync(FreeMonthlyPromptFiles.SharedRules(), cancellationToken);
        var slotPrompt = await promptLoader.LoadAsync(FreeMonthlyPromptFiles.ForSlot(slot), cancellationToken);
        return $"{shared}\n\n---\n\n{slotPrompt}";
    }

    /// <summary>
    /// Returns <see cref="FreeDigestSource.Fallback"/> (never throws) when the call would exceed
    /// <paramref name="tokenCap"/> or the model truncated - the caller substitutes the deterministic body.
    /// </summary>
    public async Task<FreeDigestEmail> WriteAsync(string systemPrompt, string userMessage, int tokenCap, CancellationToken cancellationToken = default)
    {
        var completionTokenBudget = CompletionTokenBudget(systemPrompt, userMessage, tokenCap);

        var result = await client.CompleteAsync(systemPrompt, userMessage, completionTokenBudget, cancellationToken);
        var totalTokens = result.InputTokens + result.OutputTokens;

        if (result.WasTruncated)
            return new FreeDigestEmail(result.Text, FreeDigestSource.Fallback, "response truncated at the token cap", result.InputTokens, result.OutputTokens);

        if (totalTokens > tokenCap)
            return new FreeDigestEmail(result.Text, FreeDigestSource.Fallback, $"token usage {totalTokens} exceeded cap {tokenCap}", result.InputTokens, result.OutputTokens);

        return new FreeDigestEmail(result.Text, FreeDigestSource.Llm, null, result.InputTokens, result.OutputTokens);
    }

    internal static int CompletionTokenBudget(string systemPrompt, string userMessage, int tokenCap)
    {
        var estimatedPromptTokens = (int)Math.Ceiling((systemPrompt.Length + userMessage.Length) / CharsPerToken);
        return Math.Clamp(tokenCap - estimatedPromptTokens, MinCompletionTokens, MaxCompletionTokens);
    }
}
