using Insights.Domain;
using Insights.Worker.Integration;
using Insights.Worker.Orchestration.Activities;

namespace Insights.UnitTests;

public sealed class AiReportWeeklyMapperTests
{
    private static FreeDigestAggregates Aggregates(
        int dueNext7 = 0,
        int criticalDueNext7 = 0,
        int imprisonmentDueNext7 = 0,
        int dueNext30 = 0,
        int imprisonmentDueNext30 = 0,
        int licencesLapsingNext30 = 0,
        int completedLast7 = 0,
        int totalActiveObligations = 1552) => new(
        23,
        new DateTime(2026, 9, 13),
        totalActiveObligations,
        dueNext7,
        criticalDueNext7,
        imprisonmentDueNext7,
        1,
        dueNext30,
        imprisonmentDueNext30,
        licencesLapsingNext30,
        0,
        completedLast7,
        0,
        0,
        0);

    private static AiReportWeeklyUpsertRequest Map(FreeDigestAggregates aggregates) =>
        AiReportWeeklyMapper.Map(
            new PostInsightJsonInput(
                23,
                357,
                "2026-09-13",
                aggregates,
                new InsightNarrative("Low impact", "Headline", "Explanation", "Llm")),
            new DateOnly(2026, 9, 7));

    [Fact]
    public void DueNext30Focus_ContainsVerifiedLabelAndNumbers()
    {
        var request = Map(Aggregates(dueNext30: 2));

        Assert.Equal("DueNext30", request.Report.Focus.Metric);
        Assert.Equal(2, request.Report.Focus.Value);
        Assert.Equal(1552, request.Report.Focus.Denominator);
        Assert.Equal("Items due in the next 30 days", request.Report.Focus.Label);
        Assert.Equal("2 of 1,552", request.Report.Focus.DisplayText);
    }

    [Fact]
    public void NullDenominator_ProducesValueOnlyDisplayText()
    {
        var request = Map(Aggregates(licencesLapsingNext30: 2107));

        Assert.Equal("LicencesLapsingNext30", request.Report.Focus.Metric);
        Assert.Equal(2107, request.Report.Focus.Value);
        Assert.Null(request.Report.Focus.Denominator);
        Assert.Equal("Licences lapsing in the next 30 days", request.Report.Focus.Label);
        Assert.Equal("2,107", request.Report.Focus.DisplayText);
    }

    [Fact]
    public void UnknownMetricCannotBeSent()
    {
        var focus = new InsightFocus("Low impact", "UnknownMetric", 1, 2);

        Assert.Throws<InvalidOperationException>(() =>
            AiReportWeeklyMapper.FocusPresentationFor(focus));
    }
}