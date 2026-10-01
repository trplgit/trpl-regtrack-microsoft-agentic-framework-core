namespace Insights.Domain;

/// <summary>
/// One weekly insight, in exactly the shape the RegTrack /insights hub binds (ADR-0004, revised
/// 2026-09-24 to the frontend's own field list). This record IS the POST body's <c>report</c>
/// object. Nothing may be added to it without the frontend changing with it, and a field it does
/// not read does not belong here.
///
/// <para>Every number came from one monthly subject procedure's result set, captured in one
/// instant. The only text the model wrote is <see cref="Headline"/> and <see cref="Narrative"/>,
/// both validated against that procedure's closed set of numbers before this record was built;
/// <see cref="Title"/> and every other field are derived in code (CLAUDE.md non-negotiable 5).</para>
///
/// <para>How the run went - model or deterministic fallback, tokens spent, which procedure - is
/// deliberately NOT here. That is operational data: it travels on <c>InsightCardResult</c> and is
/// logged. The customer's payload carries only what the customer's page renders.</para>
///
/// <para>Dates are ISO strings, not <see cref="DateOnly"/>: this record travels through Durable
/// Task activity inputs, whose serializer predates DateOnly.</para>
/// </summary>
public sealed record InsightCard(
    string InsightId,
    string Tier,
    string Type,
    string Severity,
    string WeekOf,
    string Title,
    string Headline,
    string Narrative,
    InsightPrimaryMetric PrimaryMetric,
    IReadOnlyList<InsightSupportingMetric> SupportingMetrics);

/// <summary>
/// The one figure the card leads on.
///
/// <para><see cref="Target"/> is the ideal implied by <see cref="Direction"/> - zero for a count of
/// something that should not exist, 100 for a rate that should be complete. It is NEVER a forecast,
/// a commitment, or a value any procedure returned, and the distance between it and
/// <see cref="Current"/> is not a projection. That distinction is why ADR-0004 removed the earlier
/// <c>projection{}</c> block: a number the data layer did not compute must never look like one it
/// did.</para>
/// </summary>
public sealed record InsightPrimaryMetric(string Label, int Current, int Target, string Unit, string Direction);

/// <summary>A second or third figure that says something the headline figure did not.</summary>
public sealed record InsightSupportingMetric(string Label, int Value, string Unit);
