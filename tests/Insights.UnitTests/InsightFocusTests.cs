using Insights.Domain;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// InsightFocus.SelectFor is the deterministic severity/metric pick the LLM is never allowed to
/// make (CLAUDE.md non-negotiable 5). These tests are the priority order in code form: an
/// imprisonment-bearing item due THIS week beats everything else, and so on down to "nothing due,
/// state last week's completions."
/// </summary>
public sealed class InsightFocusTests
{
    private static FreeDigestAggregates Aggregates(
        int totalActive = 0, int dueNext7 = 0, int criticalDueNext7 = 0, int imprisonmentDueNext7 = 0,
        int branchesInScope = 0, int dueNext30 = 0, int imprisonmentDueNext30 = 0, int licencesLapsingNext30 = 0,
        int criticalDueNext30 = 0, int completedLast7 = 0, int dueNext14 = 0, int distinctImprisonment = 0,
        int branchesWithUpcoming = 0) =>
        new(1, DateTime.UtcNow, totalActive, dueNext7, criticalDueNext7, imprisonmentDueNext7, branchesInScope,
            dueNext30, imprisonmentDueNext30, licencesLapsingNext30, criticalDueNext30, completedLast7,
            dueNext14, distinctImprisonment, branchesWithUpcoming);

    [Fact]
    public void ImprisonmentDueThisWeek_OutranksEverythingElse()
    {
        var focus = InsightFocus.SelectFor(Aggregates(imprisonmentDueNext7: 1, criticalDueNext7: 5, dueNext7: 24));

        Assert.Equal("High impact", focus.SeverityBand);
        Assert.Equal(nameof(FreeDigestAggregates.ImprisonmentDueNext7), focus.Metric);
        Assert.Equal(1, focus.Value);
        Assert.Equal(24, focus.Denominator);
    }

    [Fact]
    public void NoImprisonmentThisWeek_FallsBackToCriticalThisWeek()
    {
        var focus = InsightFocus.SelectFor(Aggregates(criticalDueNext7: 3, dueNext7: 24, imprisonmentDueNext30: 5));

        Assert.Equal("High impact", focus.SeverityBand);
        Assert.Equal(nameof(FreeDigestAggregates.CriticalDueNext7), focus.Metric);
        Assert.Equal(3, focus.Value);
    }

    [Fact]
    public void NothingThisWeek_FallsBackToThirtyDayImprisonmentHorizon()
    {
        var focus = InsightFocus.SelectFor(Aggregates(imprisonmentDueNext30: 5, dueNext30: 78, licencesLapsingNext30: 2));

        Assert.Equal("Medium impact", focus.SeverityBand);
        Assert.Equal(nameof(FreeDigestAggregates.ImprisonmentDueNext30), focus.Metric);
        Assert.Equal(5, focus.Value);
    }

    [Fact]
    public void NoLiabilityAnywhere_FallsBackToLapsingLicences()
    {
        var focus = InsightFocus.SelectFor(Aggregates(licencesLapsingNext30: 2, dueNext7: 24));

        Assert.Equal("Medium impact", focus.SeverityBand);
        Assert.Equal(nameof(FreeDigestAggregates.LicencesLapsingNext30), focus.Metric);
        Assert.Equal(2, focus.Value);
        Assert.Null(focus.Denominator);
    }

    [Fact]
    public void OnlyPlainVolumeDue_UsesDueNext7AgainstTheWholeEstate()
    {
        var focus = InsightFocus.SelectFor(Aggregates(dueNext7: 24, totalActive: 1893));

        Assert.Equal("Low impact", focus.SeverityBand);
        Assert.Equal(nameof(FreeDigestAggregates.DueNext7), focus.Metric);
        Assert.Equal(24, focus.Value);
        Assert.Equal(1893, focus.Denominator);
    }

    /// <summary>Nothing due THIS week must still surface the 30-day forward window before ever falling back to a backward-looking fact - the whole point of the priority order (see InsightFocus's own doc comment).</summary>
    [Fact]
    public void NothingDueThisWeek_ButSomethingIn30Days_UsesDueNext30BeforeFallingBackToCompletions()
    {
        var focus = InsightFocus.SelectFor(Aggregates(dueNext30: 40, totalActive: 1893, completedLast7: 12));

        Assert.Equal("Low impact", focus.SeverityBand);
        Assert.Equal(nameof(FreeDigestAggregates.DueNext30), focus.Metric);
        Assert.Equal(40, focus.Value);
        Assert.Equal(1893, focus.Denominator);
    }

    [Fact]
    public void NothingDueAtAll_FallsBackToLastWeeksCompletions()
    {
        var focus = InsightFocus.SelectFor(Aggregates(completedLast7: 12));

        Assert.Equal("Low impact", focus.SeverityBand);
        Assert.Equal(nameof(FreeDigestAggregates.CompletedLast7), focus.Metric);
        Assert.Equal(12, focus.Value);
    }

    /// <summary>The "leaves N remaining" before/after framing (prompt 07, InsightFallbackNarrative) depends on this being computed HERE, never phrased/computed by the LLM (CLAUDE.md non-negotiable 5).</summary>
    [Fact]
    public void Remainder_IsDenominatorMinusValue()
    {
        var focus = new InsightFocus("High impact", nameof(FreeDigestAggregates.ImprisonmentDueNext7), 5, 24);

        Assert.Equal(19, focus.Remainder);
    }

    [Fact]
    public void Remainder_IsNullWhenDenominatorIsNull()
    {
        var focus = new InsightFocus("Medium impact", nameof(FreeDigestAggregates.LicencesLapsingNext30), 2, null);

        Assert.Null(focus.Remainder);
    }
}
