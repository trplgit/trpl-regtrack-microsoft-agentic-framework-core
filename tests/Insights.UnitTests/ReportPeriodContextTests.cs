using Insights.Agents;

namespace Insights.UnitTests;

public sealed class ReportPeriodContextTests
{
    [Fact]
    public void LastNDays_ShowsTheNameAndTheRealRange_EndDateInclusive()
    {
        // The procedures' window end is exclusive (the day after the last day).
        var label = ReportPeriodContext.Describe("last_30_days", new DateTime(2026, 8, 29), new DateTime(2026, 9, 28));

        Assert.Equal("Last 30 days · 29 Aug 2026 – 27 Sep 2026", label);
    }

    [Fact]
    public void Quarter_ShowsTheIndianFinancialYear()
    {
        var label = ReportPeriodContext.Describe("q4", new DateTime(2027, 1, 1), new DateTime(2027, 4, 1));

        Assert.Equal("Q4 FY2026-27 · 1 Jan 2027 – 31 Mar 2027", label);
    }

    [Fact]
    public void NoWindow_IsNull_SoTheReportSaysAsOfToday()
    {
        Assert.Null(ReportPeriodContext.Describe(null, null, null));
        Assert.Null(ReportPeriodContext.Describe("last_30_days", null, null));
    }

    [Theory]
    [InlineData("BacklogAging")]
    public void AsOfTodayDimension_GetsNoPeriod_EvenWhenTheRequestCarriedAWindow(string dimension)
    {
        Assert.Null(ReportPeriodContext.DescribeFor(dimension, "q2", new DateTime(2026, 7, 1), new DateTime(2026, 10, 1)));
    }

    [Theory]
    [InlineData("Act")]
    [InlineData("Users")]
    [InlineData("Licence")] // [2026-09-29] follows the period now (sql/v2/24)
    [InlineData(null)]
    public void WindowedDimensionOrWholeReport_KeepsThePeriod(string? dimension)
    {
        Assert.Equal("Q2 FY2026-27 · 1 Jul 2026 – 30 Sep 2026",
            ReportPeriodContext.DescribeFor(dimension, "q2", new DateTime(2026, 7, 1), new DateTime(2026, 10, 1)));
    }

    [Fact]
    public void NarrativeRunContext_CarriesTodayAndTheRealRange()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            AnalystNarrativeAgentRunContext("Act", new DateTime(2026, 8, 29), new DateTime(2026, 9, 28)));

        Assert.Contains($"\"run_date\":\"{DateTime.UtcNow:yyyy-MM-dd}\"", json);
        Assert.Contains("29 Aug 2026", json);
        Assert.Contains("27 Sep 2026", json);
    }

    private static object AnalystNarrativeAgentRunContext(string dim, DateTime ws, DateTime we) =>
        Insights.Agents.MafAnalystNarrativeAgent.RunContext(dim, ws, we);

    [Fact]
    public void UnknownPeriodName_StillShowsTheRealRange()
    {
        Assert.Equal("1 Sep 2026 – 30 Sep 2026", ReportPeriodContext.Describe("custom", new DateTime(2026, 9, 1), new DateTime(2026, 10, 1)));
    }
}
