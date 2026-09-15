using Insights.Domain;
using Insights.Worker.Orchestration.Activities;

namespace Insights.Worker.Integration;

/// <summary>
/// ADR-0003 (2026-09-14): the ONLY place PostInsightJsonInput (our shape) becomes
/// AiReportWeeklyUpsertRequest (the external API's shape). Pure, no I/O, no clock - so the
/// translation is unit-testable without an HttpClient, and this file is the entire blast radius of
/// "the external contract changed" - PostInsightJsonActivity, ComposeInsightJsonActivity and every
/// domain type upstream stay untouched.
/// </summary>
public static class AiReportWeeklyMapper
{
    /// <summary>
    /// A stable generator-version tag, bumped only when the shape/meaning of
    /// <see cref="AiReportWeeklyReport"/> changes - NOT per-run provenance (that is
    /// <see cref="InsightNarrative.Source"/>, carried inside the report itself).
    /// </summary>
    public const string ModelVersion = "reginsights-freedigest-1";

    /// <summary>
    /// ADR-0003 D2 (confirmed with the product owner, 2026-09-14): the Monday report sent each
    /// week covers the LAST SEVEN DAYS - the same backward-looking convention as the free-digest
    /// email - so period_start_date is the Monday that STARTS the week WeekEnding (a Sunday)
    /// CLOSES, not the Monday about to begin. WeekEnding - 6, never WeekEnding + 1.
    /// </summary>
    public static DateOnly PeriodStartDateFor(DateOnly weekEnding) => weekEnding.AddDays(-6);

    public static AiReportWeeklyUpsertRequest Map(PostInsightJsonInput input, DateOnly periodStartDate)
    {
        // Recomputed, not re-plumbed from ComposeInsightJsonActivity: InsightFocus.SelectFor is a
        // pure, deterministic function of the aggregates already on the input (CLAUDE.md
        // non-negotiable 5) - recomputing it here keeps this mapper self-contained without adding
        // a field to PostInsightJsonInput or touching the orchestrator/compose activity.
        var focus = InsightFocus.SelectFor(input.Aggregates);

        var report = new AiReportWeeklyReport(
            input.Narrative.Headline,
            input.Narrative.Explanation,
            input.Narrative.SeverityBand,
            input.Narrative.Source,
            new AiReportWeeklyFocus(focus.Metric, focus.Value, focus.Denominator));

        return new AiReportWeeklyUpsertRequest(
            input.CustomerId,
            input.UserId,
            periodStartDate,
            report,
            ModelVersion,
            $"freedigest-insight-{input.CustomerId}-{input.WeekEnding}");
    }
}
