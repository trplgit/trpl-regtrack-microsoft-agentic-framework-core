namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-09-25] Turns the public API's free-text <c>period</c> field into a
/// <see cref="ReportPeriodChoice"/> - the real missing piece flagged since 2026-09-24: the field
/// existed, <see cref="ReportPeriodResolver"/> existed, but nothing ever connected them, so every
/// windowed dimension silently defaulted regardless of what a caller sent.
///
/// Deliberately a closed set of exact keywords, not a fuzzy date parser - CLAUDE.md's own
/// non-negotiable #2 ("fail closed, never guess"). An unrecognised string returns null, which
/// means NO window: TimelinessFY/EvidenceIntegrity fall back to their existing current-FY-to-date
/// default (unchanged behaviour), and Act/Event - which have no such fallback - fail that one
/// dimension loudly (FetchDimensionsActivity's own doc comment) rather than the caller getting a
/// silently different report than they thought they asked for.
///
/// <c>period</c> keeps its OTHER job (the cooldown/run-id key, GeneratedReport.Period storage)
/// unchanged either way - this parser only ADDS a window when it recognises the string, it never
/// rejects or alters the string itself.
/// </summary>
public static class ReportPeriodRequestParser
{
    /// <summary>The exact values <see cref="TryParse"/> recognises, for error messages.</summary>
    public const string RecognisedValues = "last_30_days, last_60_days, last_90_days, q1, q2, q3, q4";

    /// <summary>
    /// [ADDED 2026-09-27] Dimensions whose procs REQUIRE a real window and have no fallback - the
    /// same list FetchDimensionsActivity fails without one. RunEndpoints refuses a request for any
    /// of these (or the fixed holistic report, which fetches them all) when <see cref="TryParse"/>
    /// returns null, instead of queuing a run that can only fail. Case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> WindowRequiredDimensions = new HashSet<string>(
        ["Act", "Event", "Location", "Entity", "Risk", "Nature", "Departments", "Users", "Internal", "Licence"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Case-insensitive. Matches the real period-picker options (ReportPeriodKind's own doc
    /// comment): rolling day counts, or a quarter of the CURRENT financial year. Returns null for
    /// anything else, including free text like "FY2025-26" or a caller's own test-cooldown-buster
    /// string - those are valid <c>period</c> values for their OTHER purpose, just not ones this
    /// parser can turn into a window.
    /// </summary>
    public static ReportPeriodChoice? TryParse(string? period)
    {
        if (string.IsNullOrWhiteSpace(period))
            return null;

        return period.Trim().ToLowerInvariant() switch
        {
            "last_30_days" => ReportPeriodChoice.Last30Days,
            "last_60_days" => ReportPeriodChoice.Last60Days,
            "last_90_days" => ReportPeriodChoice.Last90Days,
            "q1" => ReportPeriodChoice.Quarter(1),
            "q2" => ReportPeriodChoice.Quarter(2),
            "q3" => ReportPeriodChoice.Quarter(3),
            "q4" => ReportPeriodChoice.Quarter(4),
            _ => null,
        };
    }
}
