using Insights.Agents;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// The validator decides whether an LLM-written digest ships or is replaced by the deterministic
/// template. Testing it against a live model is useless - the output varies per call, so a pass
/// proves nothing about the next run. These pin the behaviour deterministically.
///
/// Checks are numbered as in templates/free_digest_email.md "Validation before send".
/// </summary>
public sealed class FreeDigestValidatorTests
{
    /// <summary>The exact closing design doc Section 10.7 requires, as supplied by the prompt and the fallback template.</summary>
    private const string SanctionedClosing =
        "This digest shows what is coming. RegInsights Pro shows which locations, " +
        "which people, and which laws are driving it.";

    private static FreeDigestAggregates Aggregates(int dueNext7 = 24, int dueNext30 = 50, int completedLast7 = 12) =>
        new(CustomerId: 23,
            GeneratedAt: new DateTime(2026, 8, 20),
            TotalActiveObligations: 1893,
            DueNext7: dueNext7,
            CriticalDueNext7: 0,
            ImprisonmentDueNext7: 0,
            BranchesInScope: 9,
            DueNext30: dueNext30,
            ImprisonmentDueNext30: 0,
            LicencesLapsingNext30: 0,
            CriticalDueNext30: 0,
            CompletedLast7: completedLast7,
            DueNext14: 30,
            DistinctImprisonmentObligations: 0,
            BranchesWithUpcoming: 9);

    /// <summary>
    /// THE REGRESSION THIS FILE EXISTS FOR.
    ///
    /// The closing is mandated by 10.7 and contains the word "locations". The leak scan used to
    /// reject it, so the model was penalised for following its own prompt and every compliant
    /// body was discarded - while bodies that OMITTED the closing passed. Validation was
    /// selecting against the specification.
    /// </summary>
    [Fact]
    public void SanctionedClosing_IsNotTreatedAsALeak()
    {
        var body = $"Due in the next 7 days: 24. Over the next 30 days: 50.\n\n{SanctionedClosing}";

        var result = FreeDigestValidator.Validate(body, Aggregates());

        Assert.True(result.IsValid, string.Join("; ", result.FailedChecks));
    }

    /// <summary>The closing is exempt; the same word anywhere else is not.</summary>
    [Fact]
    public void LocationMentionedOutsideTheClosing_IsRejected()
    {
        var body = $"Your Pune location has 24 items due.\n\n{SanctionedClosing}";

        var result = FreeDigestValidator.Validate(body, Aggregates());

        Assert.False(result.IsValid);
        Assert.Contains(result.FailedChecks, f => f.Contains("location", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Check 2 - "overdue" as a level belongs to the paid tier (10.6).</summary>
    [Fact]
    public void OverdueWord_IsRejected()
    {
        var result = FreeDigestValidator.Validate($"You have 24 overdue items.\n{SanctionedClosing}", Aggregates());

        Assert.False(result.IsValid);
        Assert.Contains(result.FailedChecks, f => f.Contains("overdue", StringComparison.Ordinal));
    }

    /// <summary>
    /// Check 4, the strongest one. The input set is closed, so any figure that does not trace
    /// back to an aggregate was invented by the model.
    /// </summary>
    [Fact]
    public void NumberNotInTheAggregates_IsRejected()
    {
        var result = FreeDigestValidator.Validate($"You have 999 items due.\n{SanctionedClosing}", Aggregates());

        Assert.False(result.IsValid);
        Assert.Contains(result.FailedChecks, f => f.Contains("999", StringComparison.Ordinal));
    }

    /// <summary>
    /// Check 1 per spec is a "%" ADJACENT TO a completion/closure word - a ratio was invented.
    /// The old blanket ban on any "%" was stricter than the document it cites.
    /// </summary>
    [Fact]
    public void PercentageNextToACompletionWord_IsRejected()
    {
        var result = FreeDigestValidator.Validate($"Your teams completed 30% of last week's work.\n{SanctionedClosing}", Aggregates());

        Assert.False(result.IsValid);
        Assert.Contains(result.FailedChecks, f => f.Contains("ratio", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The ordinary verb "act" is not a named statute. Rejecting it discarded bodies carrying no
    /// Act name at all - the same class of false positive as the closing.
    /// </summary>
    [Fact]
    public void LowercaseVerbAct_IsNotTreatedAsANamedAct()
    {
        var body = $"There is time to act before the deadline. 24 items are due.\n{SanctionedClosing}";

        var result = FreeDigestValidator.Validate(body, Aggregates());

        Assert.True(result.IsValid, string.Join("; ", result.FailedChecks));
    }

    /// <summary>A capitalised Act reads as a named statute, which the digest has no data for.</summary>
    [Fact]
    public void NamedAct_IsRejected()
    {
        var body = $"24 items under the Factories Act are due.\n{SanctionedClosing}";

        var result = FreeDigestValidator.Validate(body, Aggregates());

        Assert.False(result.IsValid);
        Assert.Contains(result.FailedChecks, f => f.Contains("Act", StringComparison.Ordinal));
    }

    /// <summary>Check 5 - the cap is ~400 words (10.5 targets ~250).</summary>
    [Fact]
    public void OverlyLongBody_IsRejected()
    {
        var body = string.Join(' ', Enumerable.Repeat("word", 401));

        var result = FreeDigestValidator.Validate(body, Aggregates());

        Assert.False(result.IsValid);
        Assert.Contains(result.FailedChecks, f => f.Contains("exceeds", StringComparison.Ordinal));
    }
}
