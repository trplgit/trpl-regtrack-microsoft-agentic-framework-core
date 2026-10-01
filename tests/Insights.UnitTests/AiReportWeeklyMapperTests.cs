using System.Text.Json;
using Insights.Agents;
using Insights.Domain;
using Insights.Worker.Integration;
using Insights.Worker.Orchestration.Activities;

namespace Insights.UnitTests;

/// <summary>
/// ADR-0004 (revised 2026-09-24): the card IS the <c>report</c> object the receiver stores, in the
/// frontend's own field list. The per-recipient <c>insight_id</c> is stamped here, because one card
/// is composed per scope group and fanned out to everyone in it.
/// </summary>
public sealed class AiReportWeeklyMapperTests
{
    private static InsightCard Card(MonthlyDigestData data)
    {
        var input = InsightCardInput.Build(data);
        var (headline, narrative) = InsightCardFallback.Build(input.Guardrails);
        return InsightCardBuilder.Build(input, 23, 357, data.Edition.Sunday,
            new InsightCardText(headline, narrative, "fallback", "test", true, 0, 0, string.Empty));
    }

    [Fact]
    public void Report_IsTheCard()
    {
        var data = MonthlyExamples.Act();
        var card = Card(data);

        var request = AiReportWeeklyMapper.Map(new PostInsightJsonInput(23, 357, "2026-10-25", card), new DateOnly(2026, 10, 19));

        Assert.Equal(card.Headline, request.Report.Headline);
        Assert.Equal(card.Narrative, request.Report.Narrative);
        Assert.Equal(card.Title, request.Report.Title);
        Assert.Equal("free", request.Report.Tier);
        Assert.Equal(16, request.Report.PrimaryMetric.Current);
        Assert.Equal(AiReportWeeklyMapper.ModelVersion, request.ModelVersion);
        Assert.Equal("freedigest-insight-23-2026-10-25", request.SourceReference);
        Assert.Equal(new DateOnly(2026, 10, 19), request.PeriodStartDate);
    }

    [Fact]
    public void InsightId_IsStampedPerRecipient()
    {
        var card = Card(MonthlyExamples.Users());

        var a = AiReportWeeklyMapper.Map(new PostInsightJsonInput(23, 111, "2026-10-11", card), new DateOnly(2026, 10, 5));
        var b = AiReportWeeklyMapper.Map(new PostInsightJsonInput(23, 222, "2026-10-11", card), new DateOnly(2026, 10, 5));

        Assert.Equal("ins_23_111_20261005", a.Report.InsightId);
        Assert.Equal("ins_23_222_20261005", b.Report.InsightId);
        Assert.Equal(a.Report.Headline, b.Report.Headline);
    }

    [Fact]
    public void Wire_CarriesExactlyTheFrontendsFields_AndNothingElse()
    {
        var card = Card(MonthlyExamples.Overview());
        var request = AiReportWeeklyMapper.Map(new PostInsightJsonInput(23, 357, "2026-10-04", card), new DateOnly(2026, 9, 28));

        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(
            new[] { "customer_id", "user_id", "period_start_date", "report", "model_version", "source_reference" },
            root.EnumerateObject().Select(p => p.Name));

        var report = root.GetProperty("report");
        Assert.Equal(
            new[]
            {
                "insight_id", "tier", "type", "severity", "week_of", "title", "headline", "narrative",
                "primary_metric", "supporting_metrics",
            },
            report.EnumerateObject().Select(p => p.Name));

        Assert.Equal(
            new[] { "label", "current", "target", "unit", "direction" },
            report.GetProperty("primary_metric").EnumerateObject().Select(p => p.Name));

        foreach (var metric in report.GetProperty("supporting_metrics").EnumerateArray())
            Assert.Equal(new[] { "label", "value", "unit" }, metric.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void PeriodStartDate_IsTheMondayThatStartsTheClosingWeek()
    {
        Assert.Equal(new DateOnly(2026, 9, 14), AiReportWeeklyMapper.PeriodStartDateFor(new DateOnly(2026, 9, 20)));
        Assert.Equal(DayOfWeek.Monday, AiReportWeeklyMapper.PeriodStartDateFor(new DateOnly(2026, 9, 20)).DayOfWeek);
    }
}
