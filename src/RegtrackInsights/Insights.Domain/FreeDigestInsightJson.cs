namespace Insights.Domain;

/// <summary>
/// The single most material figure from a recipient's weekly aggregates, chosen
/// DETERMINISTICALLY - never by the LLM (CLAUDE.md non-negotiable 5: the narrative may only
/// assert what the data layer verified; comparatives and severity are computed, not phrased).
///
/// Priority order, most severe first: an imprisonment-bearing obligation due THIS week outranks
/// everything (personal liability, immediate); then a critical obligation due this week; then the
/// 30-day imprisonment/licence horizon; then plain volume due this week; then plain volume due in
/// the next 30 days (still forward-looking, even with nothing due THIS specific week); and only
/// when there is truly nothing ahead to point at does this fall back to last week's completions -
/// a backward-looking fact, deliberately the LAST resort, not the default.
/// </summary>
public sealed record InsightFocus(string SeverityBand, string Metric, int Value, int? Denominator)
{
    /// <summary>
    /// Denominator minus Value - "what's left once this is addressed". Computed HERE, not by the
    /// LLM: CLAUDE.md non-negotiable 5 says comparatives are computed, never phrased, so the
    /// prompt is handed this as a third given number rather than being asked to do the
    /// subtraction itself. Null whenever Denominator is null (a focus with no natural whole to
    /// subtract from - e.g. LicencesLapsingNext30, CompletedLast7).
    /// </summary>
    public int? Remainder => Denominator - Value;

    public static InsightFocus SelectFor(FreeDigestAggregates a)
    {
        if (a.ImprisonmentDueNext7 > 0)
            return new InsightFocus("High impact", nameof(a.ImprisonmentDueNext7), a.ImprisonmentDueNext7, a.DueNext7);

        if (a.CriticalDueNext7 > 0)
            return new InsightFocus("High impact", nameof(a.CriticalDueNext7), a.CriticalDueNext7, a.DueNext7);

        if (a.ImprisonmentDueNext30 > 0)
            return new InsightFocus("Medium impact", nameof(a.ImprisonmentDueNext30), a.ImprisonmentDueNext30, a.DueNext30);

        if (a.LicencesLapsingNext30 > 0)
            return new InsightFocus("Medium impact", nameof(a.LicencesLapsingNext30), a.LicencesLapsingNext30, null);

        if (a.DueNext7 > 0)
            return new InsightFocus("Low impact", nameof(a.DueNext7), a.DueNext7, a.TotalActiveObligations);

        // Nothing due THIS week, but the 30-day window is not empty - still a forward-looking
        // fact worth surfacing before falling back to a backward-looking one.
        if (a.DueNext30 > 0)
            return new InsightFocus("Low impact", nameof(a.DueNext30), a.DueNext30, a.TotalActiveObligations);

        return new InsightFocus("Low impact", nameof(a.CompletedLast7), a.CompletedLast7, null);
    }
}

/// <summary>
/// One weekly "current insight" headline for a free-tier recipient (ADR-0002, 2026-09-11) - a
/// short, data-verified narrative claim, distinct from the full email digest body. SeverityBand
/// comes from <see cref="InsightFocus"/>, computed before the LLM is ever called.
/// </summary>
public sealed record InsightNarrative(string SeverityBand, string Headline, string Explanation, string Source);
