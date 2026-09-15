using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using System.Globalization;

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

        var focusPresentation = FocusPresentationFor(focus);
        var report = new AiReportWeeklyReport(
            input.Narrative.Headline,
            input.Narrative.Explanation,
            input.Narrative.SeverityBand,
            input.Narrative.Source,
            new AiReportWeeklyFocus(
                focus.Metric,
                focus.Value,
                focus.Denominator,
                focusPresentation.Label,
                focusPresentation.DisplayText));

        return new AiReportWeeklyUpsertRequest(
            input.CustomerId,
            input.UserId,
            periodStartDate,
            report,
            ModelVersion,
            $"freedigest-insight-{input.CustomerId}-{input.WeekEnding}");
    }

    internal static (string Label, string DisplayText) FocusPresentationFor(InsightFocus focus)
    {
        var label = focus.Metric switch
        {
            nameof(FreeDigestAggregates.ImprisonmentDueNext7) => "Imprisonment-related items due in the next 7 days",
            nameof(FreeDigestAggregates.CriticalDueNext7) => "Critical items due in the next 7 days",
            nameof(FreeDigestAggregates.ImprisonmentDueNext30) => "Imprisonment-related items due in the next 30 days",
            nameof(FreeDigestAggregates.LicencesLapsingNext30) => "Licences lapsing in the next 30 days",
            nameof(FreeDigestAggregates.DueNext7) => "Items due in the next 7 days",
            nameof(FreeDigestAggregates.DueNext30) => "Items due in the next 30 days",
            nameof(FreeDigestAggregates.CompletedLast7) => "Items completed in the last 7 days",
            _ => throw new InvalidOperationException($"Unknown free digest focus metric '{focus.Metric}'. Refusing to build the API payload."),
        };

        var value = focus.Value.ToString("N0", CultureInfo.InvariantCulture);
        var displayText = focus.Denominator is { } denominator
            ? $"{value} of {denominator.ToString("N0", CultureInfo.InvariantCulture)}"
            : value;

        return (label, displayText);
    }
}
