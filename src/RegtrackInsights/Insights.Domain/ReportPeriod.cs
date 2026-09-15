namespace Insights.Domain;

/// <summary>
/// The fixed set of period-picker options. The UI offers ONLY these - the user never types a
/// date. Each one resolves to a concrete [start, end) datetime pair (see <see cref="ReportPeriodResolver"/>)
/// that is passed as @WindowStart / @WindowEnd to usp_Insights_Dimension_TimelinessFY (sql/23) and
/// usp_Insights_Dimension_EvidenceIntegrity (sql/25).
/// </summary>
public enum ReportPeriodKind
{
    Last30Days,
    Last60Days,
    Last90Days,
    /// <summary>A quarter of the CURRENT financial year (April-start). Which quarter is given by <see cref="ReportPeriodChoice.FyQuarter"/>.</summary>
    FyQuarter,
}

/// <summary>
/// A single dropdown selection. <see cref="FyQuarter"/> (1..4) is required when
/// <see cref="Kind"/> is <see cref="ReportPeriodKind.FyQuarter"/> and ignored otherwise.
/// </summary>
public sealed record ReportPeriodChoice(ReportPeriodKind Kind, int? FyQuarter = null)
{
    public static readonly ReportPeriodChoice Last30Days = new(ReportPeriodKind.Last30Days);
    public static readonly ReportPeriodChoice Last60Days = new(ReportPeriodKind.Last60Days);
    public static readonly ReportPeriodChoice Last90Days = new(ReportPeriodKind.Last90Days);
    public static ReportPeriodChoice Quarter(int fyQuarter) => new(ReportPeriodKind.FyQuarter, fyQuarter);
}

/// <summary>
/// A resolved window. <see cref="StartInclusive"/> &lt;= row &lt; <see cref="EndExclusive"/> -
/// half-open, matching the SQL (`ScheduleOn &gt;= @WindowStart AND ScheduleOn &lt; @WindowEnd`).
/// <see cref="Capped"/> is true when the end was pulled back to "now" because the quarter has not
/// finished yet (a to-date view).
/// </summary>
public sealed record ResolvedReportPeriod(DateTime StartInclusive, DateTime EndExclusive, string Label, bool Capped);

/// <summary>
/// Turns a fixed dropdown pick into a concrete date window, relative to "now". Pure - no I/O, no
/// static clock. Financial year starts 1 April, matching sql/23's own boundary
/// (`MONTH(@AsOf) &gt;= 4`).
/// </summary>
public static class ReportPeriodResolver
{
    private const int FyStartMonth = 4; // 1 April

    /// <summary>
    /// The calendar year in which the CURRENT financial year began, for a given moment.
    /// e.g. any date in Jan-Mar 2027 -> 2026 (FY2026-27 started April 2026).
    /// </summary>
    public static int CurrentFyStartYear(DateTime asOf) =>
        asOf.Month >= FyStartMonth ? asOf.Year : asOf.Year - 1;

    /// <summary>
    /// Which FY quarters (1..4) the dropdown may OFFER right now: a quarter is selectable once it
    /// has started (its first day is on or before <paramref name="asOf"/>). Future quarters are
    /// returned by neither this method nor the UI - they render greyed out.
    /// </summary>
    public static IReadOnlyList<int> SelectableFyQuarters(DateTime asOf)
    {
        var result = new List<int>(4);
        for (var q = 1; q <= 4; q++)
        {
            var (start, _) = FyQuarterBounds(CurrentFyStartYear(asOf), q);
            if (start <= asOf) result.Add(q);
        }
        return result;
    }

    /// <summary>
    /// Resolve a pick to its window. Throws <see cref="ArgumentException"/> for a malformed choice
    /// (no quarter number, quarter out of 1..4, or a quarter that has not started yet - the UI must
    /// not offer those).
    /// </summary>
    public static ResolvedReportPeriod Resolve(ReportPeriodChoice choice, DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(choice);

        // "Now, exclusive" - end of today. A row dated any time today is < this, so today counts.
        var nowExclusive = asOf.Date.AddDays(1);

        switch (choice.Kind)
        {
            case ReportPeriodKind.Last30Days: return RollingDays(30, nowExclusive);
            case ReportPeriodKind.Last60Days: return RollingDays(60, nowExclusive);
            case ReportPeriodKind.Last90Days: return RollingDays(90, nowExclusive);

            case ReportPeriodKind.FyQuarter:
            {
                if (choice.FyQuarter is not (>= 1 and <= 4))
                    throw new ArgumentException($"FyQuarter must be 1..4, got {choice.FyQuarter?.ToString() ?? "null"}.", nameof(choice));

                var q = choice.FyQuarter.Value;
                var fyStartYear = CurrentFyStartYear(asOf);
                var (start, endExclusive) = FyQuarterBounds(fyStartYear, q);

                if (start > asOf)
                    throw new ArgumentException($"Q{q} of FY{Short(fyStartYear)}-{Short(fyStartYear + 1)} has not started yet - the picker must not offer it.", nameof(choice));

                var capped = nowExclusive < endExclusive;
                var end = capped ? nowExclusive : endExclusive;
                var label = $"Q{q} FY{Short(fyStartYear)}-{Short(fyStartYear + 1)}" + (capped ? " (to date)" : "");
                return new ResolvedReportPeriod(start, end, label, capped);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(choice), choice.Kind, "Unknown period kind.");
        }
    }

    private static ResolvedReportPeriod RollingDays(int days, DateTime endExclusive) =>
        new(endExclusive.AddDays(-days), endExclusive, $"Last {days} days", Capped: false);

    /// <summary>[start, endExclusive) for a quarter of the FY that began in <paramref name="fyStartYear"/>. Q1 = Apr-Jun, Q2 = Jul-Sep, Q3 = Oct-Dec, Q4 = Jan-Mar (next calendar year).</summary>
    private static (DateTime Start, DateTime EndExclusive) FyQuarterBounds(int fyStartYear, int quarter)
    {
        // Quarter 1 starts at FyStartMonth; each quarter is 3 months. Month 13-15 roll into the next year.
        var startMonthAbsolute = FyStartMonth + (quarter - 1) * 3; // 4, 7, 10, 13
        var start = MonthStart(fyStartYear, startMonthAbsolute);
        var endExclusive = MonthStart(fyStartYear, startMonthAbsolute + 3);
        return (start, endExclusive);
    }

    private static DateTime MonthStart(int baseYear, int monthAbsolute)
    {
        var year = baseYear + (monthAbsolute - 1) / 12;
        var month = (monthAbsolute - 1) % 12 + 1;
        return new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
    }

    private static string Short(int year) => (year % 100).ToString("D2");
}
