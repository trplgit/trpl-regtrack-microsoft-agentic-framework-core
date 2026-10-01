using Insights.Domain;
using Xunit;

namespace Insights.UnitTests;

public class ReportPeriodRequestParserTests
{
    [Theory]
    [InlineData("last_30_days", ReportPeriodKind.Last30Days, null)]
    [InlineData("last_60_days", ReportPeriodKind.Last60Days, null)]
    [InlineData("last_90_days", ReportPeriodKind.Last90Days, null)]
    [InlineData("q1", ReportPeriodKind.FyQuarter, 1)]
    [InlineData("q2", ReportPeriodKind.FyQuarter, 2)]
    [InlineData("q3", ReportPeriodKind.FyQuarter, 3)]
    [InlineData("q4", ReportPeriodKind.FyQuarter, 4)]
    public void TryParse_RecognisedKeyword_ReturnsTheMatchingChoice(string period, ReportPeriodKind expectedKind, int? expectedQuarter)
    {
        var choice = ReportPeriodRequestParser.TryParse(period);

        Assert.NotNull(choice);
        Assert.Equal(expectedKind, choice!.Kind);
        Assert.Equal(expectedQuarter, choice.FyQuarter);
    }

    [Theory]
    [InlineData("LAST_30_DAYS")]
    [InlineData("Last_30_Days")]
    [InlineData("Q1")]
    [InlineData("  q1  ")]
    public void TryParse_IsCaseInsensitiveAndTrims(string period)
    {
        Assert.NotNull(ReportPeriodRequestParser.TryParse(period));
    }

    /// <summary>
    /// The real, load-bearing case: every existing free-text period string already in use for the
    /// cooldown/run-id key (test-cooldown-busters, "FY2025-26", etc.) must resolve to null, not
    /// throw and not guess - CLAUDE.md's "fail closed, never guess" non-negotiable. A null result
    /// means no window is resolved, which preserves every existing caller's exact current
    /// behaviour (see FetchDimensionsActivity/RunEndpoints doc comments).
    /// </summary>
    [Theory]
    [InlineData("FY2025-26")]
    [InlineData("sp_getapplock-verify-20260924123456")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("last 30 days")] // spaces, not underscores - not a recognised keyword
    [InlineData("Q5")] // out of range
    public void TryParse_UnrecognisedOrEmpty_ReturnsNull(string? period)
    {
        Assert.Null(ReportPeriodRequestParser.TryParse(period));
    }
}
