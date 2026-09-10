using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// The period picker offers a FIXED set of options - no free date entry. Each resolves to a
/// concrete [start, end) window passed to sql/23 (TimelinessFY) and sql/25 (EvidenceIntegrity).
/// FY starts 1 April, matching sql/23's own boundary.
/// </summary>
public sealed class ReportPeriodResolverTests
{
    // A fixed "now": 15 Nov 2026 -> inside FY2026-27 (started Apr 2026), inside Q3 (Oct-Dec).
    private static readonly DateTime Now = new(2026, 11, 15, 9, 30, 0);

    [Theory]
    [InlineData(ReportPeriodKind.Last30Days, 30)]
    [InlineData(ReportPeriodKind.Last60Days, 60)]
    [InlineData(ReportPeriodKind.Last90Days, 90)]
    public void Resolve_RollingDays_EndsAtEndOfToday_StartsExactlyNDaysBefore(ReportPeriodKind kind, int days)
    {
        var r = ReportPeriodResolver.Resolve(new ReportPeriodChoice(kind), Now);

        Assert.Equal(new DateTime(2026, 11, 16), r.EndExclusive);          // midnight after today - today counts
        Assert.Equal(new DateTime(2026, 11, 16).AddDays(-days), r.StartInclusive);
        Assert.Equal($"Last {days} days", r.Label);
        Assert.False(r.Capped);
    }

    [Fact]
    public void Resolve_Q1_Apr_To_Jul()
    {
        var r = ReportPeriodResolver.Resolve(ReportPeriodChoice.Quarter(1), Now);

        Assert.Equal(new DateTime(2026, 4, 1), r.StartInclusive);
        Assert.Equal(new DateTime(2026, 7, 1), r.EndExclusive);
        Assert.Equal("Q1 FY26-27", r.Label);
        Assert.False(r.Capped);
    }

    [Fact]
    public void Resolve_Q2_Jul_To_Oct()
    {
        var r = ReportPeriodResolver.Resolve(ReportPeriodChoice.Quarter(2), Now);

        Assert.Equal(new DateTime(2026, 7, 1), r.StartInclusive);
        Assert.Equal(new DateTime(2026, 10, 1), r.EndExclusive);
        Assert.False(r.Capped);
    }

    [Fact]
    public void Resolve_CurrentQuarter_Q3_IsCappedToEndOfToday_AndLabelledToDate()
    {
        var r = ReportPeriodResolver.Resolve(ReportPeriodChoice.Quarter(3), Now);

        Assert.Equal(new DateTime(2026, 10, 1), r.StartInclusive);
        Assert.Equal(new DateTime(2026, 11, 16), r.EndExclusive);   // pulled back to now, not 1 Jan
        Assert.True(r.Capped);
        Assert.Equal("Q3 FY26-27 (to date)", r.Label);
    }

    [Fact]
    public void Resolve_Q4_CrossesTheCalendarYear_Jan_To_Apr_NextYear()
    {
        // From a "now" late enough that Q4 has started: 20 Feb 2027 is still FY2026-27.
        var feb = new DateTime(2027, 2, 20);
        var r = ReportPeriodResolver.Resolve(ReportPeriodChoice.Quarter(4), feb);

        Assert.Equal(new DateTime(2027, 1, 1), r.StartInclusive);
        Assert.Equal(new DateTime(2027, 2, 21), r.EndExclusive);    // capped - Q4 still running
        Assert.Equal("Q4 FY26-27 (to date)", r.Label);
    }

    [Fact]
    public void Resolve_FutureQuarter_Throws_PickerMustNotOfferIt()
    {
        // In Nov 2026, Q4 (Jan-Mar 2027) has not started.
        var ex = Assert.Throws<ArgumentException>(() => ReportPeriodResolver.Resolve(ReportPeriodChoice.Quarter(4), Now));
        Assert.Contains("has not started", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(null)]
    public void Resolve_BadQuarterNumber_Throws(int? q)
    {
        Assert.Throws<ArgumentException>(() => ReportPeriodResolver.Resolve(new ReportPeriodChoice(ReportPeriodKind.FyQuarter, q), Now));
    }

    [Fact]
    public void SelectableFyQuarters_MidNovember_OffersQ1ToQ3_NotQ4()
    {
        Assert.Equal(new[] { 1, 2, 3 }, ReportPeriodResolver.SelectableFyQuarters(Now));
    }

    [Fact]
    public void SelectableFyQuarters_EarlyApril_OffersOnlyQ1()
    {
        Assert.Equal(new[] { 1 }, ReportPeriodResolver.SelectableFyQuarters(new DateTime(2026, 4, 3)));
    }

    [Fact]
    public void SelectableFyQuarters_February_AllFourStarted()
    {
        Assert.Equal(new[] { 1, 2, 3, 4 }, ReportPeriodResolver.SelectableFyQuarters(new DateTime(2027, 2, 1)));
    }

    [Fact]
    public void CurrentFyStartYear_JanuaryIsStillLastAprilsFy()
    {
        Assert.Equal(2026, ReportPeriodResolver.CurrentFyStartYear(new DateTime(2027, 1, 10)));
        Assert.Equal(2026, ReportPeriodResolver.CurrentFyStartYear(new DateTime(2026, 4, 1)));
        Assert.Equal(2025, ReportPeriodResolver.CurrentFyStartYear(new DateTime(2026, 3, 31)));
    }
}
