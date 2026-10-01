using Insights.Domain;
using Insights.Worker.Orchestration.Activities;

namespace Insights.Worker.Integration;

/// <summary>
/// ADR-0003 (2026-09-14): the ONLY place PostInsightJsonInput (our shape) becomes
/// AiReportWeeklyUpsertRequest (the external API's shape). Pure, no I/O, no clock - so the
/// translation is unit-testable without an HttpClient, and this file is the entire blast radius of
/// "the external contract changed".
///
/// <para>ADR-0004 (revised 2026-09-23): the input is an <see cref="InsightCard"/> and the card IS
/// the <c>report</c> object - the receiver stores it free-form and the /insights hub renders it.
/// The per-recipient <c>insight_id</c> is stamped here, because one card is composed per scope
/// group and fanned out to every recipient in it.</para>
/// </summary>
public static class AiReportWeeklyMapper
{
    /// <summary>The generator-version tag for the card-shaped report (ADR-0004). "-1" was the headline/focus shape.</summary>
    public const string ModelVersion = "reginsights-freedigest-2";

    /// <summary>
    /// ADR-0003 D2 (confirmed with the product owner, 2026-09-14): the Monday report sent each
    /// week covers the LAST SEVEN DAYS - the same backward-looking convention as the free-digest
    /// email - so period_start_date is the Monday that STARTS the week WeekEnding (a Sunday)
    /// CLOSES, not the Monday about to begin. WeekEnding - 6, never WeekEnding + 1.
    /// </summary>
    public static DateOnly PeriodStartDateFor(DateOnly weekEnding) => weekEnding.AddDays(-6);

    public static AiReportWeeklyUpsertRequest Map(PostInsightJsonInput input, DateOnly periodStartDate)
    {
        var card = input.Card with { InsightId = InsightCardRules.InsightId(input.CustomerId, input.UserId, periodStartDate) };

        return new AiReportWeeklyUpsertRequest(
            input.CustomerId,
            input.UserId,
            periodStartDate,
            ToWire(card),
            ModelVersion,
            $"freedigest-insight-{input.CustomerId}-{input.WeekEnding}");
    }

    internal static AiReportWeeklyInsight ToWire(InsightCard c) => new(
        c.InsightId, c.Tier, c.Type, c.Severity, c.WeekOf, c.Title, c.Headline, c.Narrative,
        new AiReportWeeklyPrimaryMetric(
            c.PrimaryMetric.Label, c.PrimaryMetric.Current, c.PrimaryMetric.Target,
            c.PrimaryMetric.Unit, c.PrimaryMetric.Direction),
        c.SupportingMetrics.Select(s => new AiReportWeeklySupportingMetric(s.Label, s.Value, s.Unit)).ToList());

}
