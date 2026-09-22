using System.Globalization;

namespace Insights.Domain;

/// <summary>
/// The five monthly free-digest emails (spec docs/superpowers/specs/2026-09-18-monthly-free-tier-digest-design.md
/// Sec.2). Which one a Sunday gets is decided by <see cref="MonthlyDigestCalendar"/> from the date alone.
/// </summary>
public enum MonthlyDigestSlot
{
    Overview,
    Users,
    Location,
    Act,
    Licence,
}

/// <summary>
/// One Sunday's place in the monthly cycle. <see cref="CurrMonthStart"/> is always the 1st of the
/// Sunday's own month - sql/34 THROWs 51236 on anything else, and 51237 when @AsOf falls outside that
/// month, so this record is the single source of both.
/// </summary>
public sealed record MonthlyDigestEdition(
    MonthlyDigestSlot Slot, DateOnly Sunday, int SundayOfMonth, int SundaysInMonth, DateOnly CurrMonthStart)
{
    public DateOnly PrevMonthStart => CurrMonthStart.AddMonths(-1);

    public DateOnly CurrMonthEnd => CurrMonthStart.AddMonths(1).AddDays(-1);

    /// <summary>"October 2026 - Monthly overview". Used in the email masthead, preheader and subject.</summary>
    public string PeriodLabel =>
        $"{CurrMonthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture)} - {MonthlyDigestCalendar.Title(Slot)}";

    /// <summary>"October 2026 monthly overview" - reads after "for the" in the preheader and title.</summary>
    public string PeriodPhrase =>
        $"{CurrMonthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture)} {MonthlyDigestCalendar.Title(Slot).ToLowerInvariant()}";
}

/// <summary>
/// Calendar-pure slot selection (spec Sec.2): the email a Sunday carries is computed from the date
/// only - never from tenant history or what was sent last time - so it is reproducible on any replay.
/// </summary>
public static class MonthlyDigestCalendar
{
    public static MonthlyDigestEdition For(DateOnly sunday)
    {
        if (sunday.DayOfWeek != DayOfWeek.Sunday)
            throw new ArgumentException($"{sunday:yyyy-MM-dd} is a {sunday.DayOfWeek}, not a Sunday - the monthly digest is keyed on the Sunday that closes its week.", nameof(sunday));

        var monthStart = new DateOnly(sunday.Year, sunday.Month, 1);
        var sundayOfMonth = (sunday.Day - 1) / 7 + 1;

        var firstSunday = monthStart.AddDays(((int)DayOfWeek.Sunday - (int)monthStart.DayOfWeek + 7) % 7);
        var daysInMonth = DateTime.DaysInMonth(sunday.Year, sunday.Month);
        var sundaysInMonth = (daysInMonth - firstSunday.Day) / 7 + 1;

        var slot = sundayOfMonth switch
        {
            1 => MonthlyDigestSlot.Overview,
            2 => MonthlyDigestSlot.Users,
            3 => MonthlyDigestSlot.Location,
            4 => MonthlyDigestSlot.Act,
            5 => MonthlyDigestSlot.Licence,
            _ => throw new InvalidOperationException($"A month cannot have a Sunday number {sundayOfMonth}."),
        };

        return new MonthlyDigestEdition(slot, sunday, sundayOfMonth, sundaysInMonth, monthStart);
    }

    /// <summary>The edition that goes out the following Sunday - for the "Next week:" line.</summary>
    public static MonthlyDigestEdition Next(MonthlyDigestEdition edition) => For(edition.Sunday.AddDays(7));

    /// <summary>
    /// @AsOf for the SQL call, clamped into the edition's month. A Sunday-generated run is always
    /// inside its own month, but a retry that lands after midnight on the last day of the month would
    /// otherwise pass an @AsOf from the NEXT month and be refused with 51237.
    /// </summary>
    public static DateTime AsOfWithinMonth(DateTime localNow, MonthlyDigestEdition edition)
    {
        var start = edition.CurrMonthStart.ToDateTime(TimeOnly.MinValue);
        var end = edition.CurrMonthEnd.ToDateTime(new TimeOnly(23, 59, 59));
        var asOf = localNow < start ? start : localNow > end ? end : localNow;
        return DateTime.SpecifyKind(asOf, DateTimeKind.Unspecified);
    }

    public static string Title(MonthlyDigestSlot slot) => slot switch
    {
        MonthlyDigestSlot.Overview => "Monthly overview",
        MonthlyDigestSlot.Users => "People and ownership",
        MonthlyDigestSlot.Location => "Locations",
        MonthlyDigestSlot.Act => "Laws",
        MonthlyDigestSlot.Licence => "Licences",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null),
    };
}

/// <summary>One row of the <c>facts</c> result set - identical shape in every sql/36-41 slot proc.</summary>
public sealed record MonthlyFact(
    string FactKey,
    int FactValue,
    string DisplayLabel,
    string Section,
    int DisplayOrder,
    string WindowScope,
    string ImpactClass,
    int SeverityTier,
    bool AsAtRequired,
    bool IsHeadline);

/// <summary>One row of the <c>detector_policy</c> result set.</summary>
public sealed record MonthlyDetectorPolicy(string Detector, int Eligible, int Flagged, decimal? FlaggedPct, string? EmitMode, string? Note);

/// <summary>
/// One row of the <c>candidates</c> result set. <see cref="EntityLabel"/> NULL means the entity is not
/// nameable (names withheld, unnamed master row) - it is described, never named.
/// <see cref="DefaultSlot"/> 1/2 marks the two the email names by default.
/// </summary>
public sealed record MonthlyCandidate(
    int? DefaultSlot,
    string Detector,
    int Priority,
    int SeverityTier,
    int RankInDetector,
    string EntityKind,
    long? EntityId,
    string? EntityLabel,
    string? ContextKind,
    string? ContextLabel,
    string Metric,
    int? MetricPct,
    int? TenantPct,
    int? ItemCount,
    int? BaseCount,
    DateTime? EventDate,
    bool AsAtRequired,
    int ProblemCount,
    int PopulationCount,
    int ResidualCount);

/// <summary>One row of the <c>data_quality</c> result set - declared, never silent.</summary>
public sealed record MonthlyDataQuality(string Code, int ItemCount, string Detail);

/// <summary>
/// Everything one monthly slot proc returned for one scope group. <see cref="HeadlineSource"/> is
/// <c>fact</c> (a fact carries IsHeadline) or <c>candidate</c> (the DefaultSlot 1 candidate leads).
/// </summary>
public sealed record MonthlyDigestData(
    MonthlyDigestEdition Edition,
    DateTime AsOf,
    string HeadlineSource,
    IReadOnlyList<MonthlyFact> Facts,
    IReadOnlyList<MonthlyDetectorPolicy> DetectorPolicy,
    IReadOnlyList<MonthlyCandidate> Candidates,
    IReadOnlyList<MonthlyDataQuality> DataQuality);

/// <summary>
/// A monthly slot proc refused to return data (THROW 51230-51309: scope denied, reconciliation failed,
/// dictionary gap, detector contract). Fail closed - nothing is sent for that scope group this week.
/// </summary>
public sealed class FreeMonthlyDigestRefusedException(int sqlErrorNumber, string message, Exception inner)
    : Exception($"Free monthly digest refused by SQL (error {sqlErrorNumber}): {message}", inner)
{
    public int SqlErrorNumber { get; } = sqlErrorNumber;

    /// <summary>The error block sql/34-41 own (CLAUDE.md Sec.5b).</summary>
    public static bool IsMonthlyErrorNumber(int number) => number is >= 51230 and <= 51309;
}
