using Insights.Agents;
using Insights.Domain;
using Insights.Presentation;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// The deterministic fallback (templates/digest_fallback.txt) is meant to read like the LLM's own
/// prose (prompts/06_freetier_digest.md), not a raw stats table - a recipient should not be able
/// to tell the LLM was skipped this week. Substitute has no conditional-on-VALUE logic (only
/// conditional-on-non-empty-string), so the singular/plural/zero clauses are computed in
/// FreeDigestEmailRenderer.TokensFor, not the template. These tests lock in that every token the
/// template references actually exists (a typo'd token renders as silent empty string, not an
/// error - see Substitute's own doc comment) and that the zero/singular/plural cases read right.
/// </summary>
public sealed class FreeDigestEmailRendererFallbackTests
{
    private static readonly string TemplateDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RegtrackInsights", "templates");

    private static readonly FreeDigestEmailRenderer Renderer = new(TemplateDirectory);

    private static FreeDigestAggregates Aggregates(
        int dueNext7 = 0, int criticalNext7 = 0, int dueNext30 = 0, int imprisonmentNext30 = 0,
        int licencesLapsing = 0, int completedLast7 = 0) =>
        new(
            CustomerId: 1300, GeneratedAt: DateTime.UtcNow, TotalActiveObligations: 100,
            DueNext7: dueNext7, CriticalDueNext7: criticalNext7, ImprisonmentDueNext7: 0, BranchesInScope: 1,
            DueNext30: dueNext30, ImprisonmentDueNext30: imprisonmentNext30, LicencesLapsingNext30: licencesLapsing,
            CriticalDueNext30: 0, CompletedLast7: completedLast7, DueNext14: 0,
            DistinctImprisonmentObligations: 0, BranchesWithUpcoming: 0);

    /// <summary>No stray "{{...}}" survives - that would mean a token in the template has no entry in TokensFor, and Substitute silently emitted empty string instead of erroring.</summary>
    private static void AssertNoUnresolvedTokens(string rendered) =>
        Assert.DoesNotMatch(@"\{\{.*?\}\}", rendered);

    [Fact]
    public async Task RenderFallbackBodyAsync_AllZero_ReadsAsProseNotATable()
    {
        var body = await Renderer.RenderFallbackBodyAsync(Aggregates(), recipientName: null, weekEnding: new DateTime(2026, 9, 6));

        AssertNoUnresolvedTokens(body);
        Assert.Contains("Good morning,", body);
        Assert.Contains("Here's your compliance snapshot for the week: **0 obligations** are due in the next seven days, with none rated critical.", body);
        Assert.Contains("the next 30 days carry **0 obligations** in total. Of those, **0** carry personal liability for the responsible officer, and no licences are due to lapse.", body);
        Assert.Contains("**0 completions** were recorded last week across the estate.", body);
        Assert.DoesNotContain("Due in the next 7 days:", body); // the old table-shaped wording must be gone
    }

    /// <summary>
    /// [REGRESSION] The opening clause used to say "the week ending {{WeekEnding}}" - which puts
    /// the literal date (day + year) into the body text. FreeDigestValidator's numeric-diff check
    /// (check 4) only allows numbers that trace to an aggregate or a window label (7/14/30), so an
    /// LLM body doing the same thing would be rejected outright and silently replaced by the
    /// fallback - EVERY digest, regardless of tenant data, because the date is never one of the 13
    /// aggregates. Confirmed live: 6/6 LLM calls for tenant 23 were discarded this way. The fix was
    /// to drop the date from the opening clause entirely (the email header above the body already
    /// states it) rather than try to special-case date numbers into the validator. This test proves
    /// the fallback - which is meant to read exactly like a validator-accepted LLM body - actually
    /// passes FreeDigestValidator, so this class of "the fallback template looks fine but would
    /// have failed the check the LLM path is held to" bug cannot recur silently.
    /// </summary>
    [Fact]
    public async Task RenderFallbackBodyAsync_PassesTheSameValidatorTheLlmBodyIsHeldTo()
    {
        var aggregates = Aggregates(dueNext7: 66, criticalNext7: 18, dueNext30: 644, imprisonmentNext30: 183, licencesLapsing: 15, completedLast7: 214);
        var body = await Renderer.RenderFallbackBodyAsync(aggregates, recipientName: null, weekEnding: new DateTime(2026, 9, 6));

        var result = FreeDigestValidator.Validate(body, aggregates);

        Assert.True(result.IsValid, string.Join("; ", result.FailedChecks));
    }

    [Fact]
    public async Task RenderFallbackBodyAsync_SingularCounts_UseSingularGrammar()
    {
        var body = await Renderer.RenderFallbackBodyAsync(
            Aggregates(dueNext7: 1, criticalNext7: 1, dueNext30: 1, imprisonmentNext30: 1, licencesLapsing: 1, completedLast7: 1),
            recipientName: "Asha", weekEnding: new DateTime(2026, 9, 6));

        AssertNoUnresolvedTokens(body);
        Assert.Contains("Good morning Asha,", body);
        Assert.Contains("**1 obligation** is due in the next seven days, 1 of them rated critical.", body);
        Assert.Contains("the next 30 days carry **1 obligation** in total. Of those, **1** carries personal liability for the responsible officer, and 1 licence is due to lapse.", body);
        Assert.Contains("**1 completion** was recorded last week across the estate.", body);
    }

    [Fact]
    public async Task RenderFallbackBodyAsync_PluralCounts_UsePluralGrammar()
    {
        var body = await Renderer.RenderFallbackBodyAsync(
            Aggregates(dueNext7: 66, criticalNext7: 18, dueNext30: 644, imprisonmentNext30: 183, licencesLapsing: 15, completedLast7: 214),
            recipientName: null, weekEnding: new DateTime(2026, 9, 6));

        AssertNoUnresolvedTokens(body);
        Assert.Contains("**66 obligations** are due in the next seven days, 18 of them rated critical.", body);
        Assert.Contains("the next 30 days carry **644 obligations** in total. Of those, **183** carry personal liability for the responsible officer, and 15 licences are due to lapse.", body);
        Assert.Contains("**214 completions** were recorded last week across the estate.", body);
    }

    [Fact]
    public async Task RenderFallbackBodyAsync_NeverUsesTheWordOverdue()
    {
        // Same absolute rule the LLM prompt is held to (06_freetier_digest.md rule 2) - "overdue" is paid-tier only.
        var body = await Renderer.RenderFallbackBodyAsync(
            Aggregates(dueNext7: 5, criticalNext7: 2, dueNext30: 10, imprisonmentNext30: 3, licencesLapsing: 1, completedLast7: 4),
            recipientName: null, weekEnding: new DateTime(2026, 9, 6));

        Assert.DoesNotContain("overdue", body, StringComparison.OrdinalIgnoreCase);
    }
}
