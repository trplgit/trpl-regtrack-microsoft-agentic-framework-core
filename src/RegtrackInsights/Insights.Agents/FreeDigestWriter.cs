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

        var result = await client.CompleteAsync(systemPrompt, userMessage, tokenCap, cancellationToken);
        var totalTokens = result.InputTokens + result.OutputTokens;

        if (result.WasTruncated)
            return new FreeDigestEmail(result.Text, FreeDigestSource.Fallback, "response truncated at the token cap", result.InputTokens, result.OutputTokens);

        if (totalTokens > tokenCap)
            return new FreeDigestEmail(result.Text, FreeDigestSource.Fallback, $"token usage {totalTokens} exceeded cap {tokenCap}", result.InputTokens, result.OutputTokens);

        return new FreeDigestEmail(result.Text, FreeDigestSource.Llm, null, result.InputTokens, result.OutputTokens);
    }
}
