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
    /// <summary>
    /// The instant an edition reports as at.
    ///
    /// <para>In production this is simply "now", because an edition is generated ON its own Sunday -
    /// the scheduler runs that day and no other. A PREVIEW is different: it renders five editions in
    /// one afternoon, and using "now" for all of them makes every email report the same instant. The
    /// September set all read "as at 21 Sep 2026" even though the Overview's edition is the 6th and
    /// Location's is the 20th, so they showed one position five ways instead of the month unfolding.</para>
    ///
    /// <para>So the edition's own Sunday wins whenever it has already passed. That is what production
    /// would have used on the day, which makes a preview of a past week honest rather than
    /// approximate. A Sunday still in the future clamps back to now - we cannot report a position
    /// that has not happened - and everything stays inside the edition's month either way.</para>
    /// </summary>
    public static DateTime AsOfWithinMonth(DateTime localNow, MonthlyDigestEdition edition)
    {
        var start = edition.CurrMonthStart.ToDateTime(TimeOnly.MinValue);
        var end = edition.CurrMonthEnd.ToDateTime(new TimeOnly(23, 59, 59));

        // The edition's own Sunday, at the end of that day - what the scheduler would have seen.
        var itsSunday = edition.Sunday.ToDateTime(new TimeOnly(23, 59, 59));
        var preferred = itsSunday <= localNow ? itsSunday : localNow;

        var asOf = preferred < start ? start : preferred > end ? end : preferred;
        return DateTime.SpecifyKind(asOf, DateTimeKind.Unspecified);
    }

    public static string Title(MonthlyDigestSlot slot) => slot switch
    {
        MonthlyDigestSlot.Overview => "Monthly overview",
        MonthlyDigestSlot.Users => "People and ownership",
        MonthlyDigestSlot.Location => "Locations",
        // "Acts", not "Laws": the reader's own registers name Acts, and the email body says "Act".
        MonthlyDigestSlot.Act => "Acts",
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
/// One row of the <c>examples</c> result set (grid #6, sql/36-41): a member that ILLUSTRATES an
/// aggregate-mode pattern fact. Not a finding. The emission policy (CLAUDE.md Sec.4) still reports a
/// pattern that covers more than a fifth of the members as ONE aggregate finding; an example only
/// lets the email say "18 of your 24 locations ..., including Khavda, Jabalpur and Surat".
///
/// <para>[DECIDED 2026-09-23, docs/superpowers/specs/2026-09-23-aggregate-mode-examples-design.md]
/// An example carries identification and scale - its own <see cref="ItemCount"/> of
/// <see cref="BaseCount"/> - and never a rate against the scope: the rate-versus-tenant comparison
/// is what makes a row an individual finding, so it is structurally absent here.</para>
/// </summary>
public sealed record MonthlyExample(
    string Detector,
    string PatternFactKey,
    int ExampleRank,
    string EntityKind,
    long? EntityId,
    string EntityLabel,
    string? ContextKind,
    string? ContextLabel,
    int? ItemCount,
    int? BaseCount,
    string UnitLabel);

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
    IReadOnlyList<MonthlyDataQuality> DataQuality)
{
    /// <summary>
    /// Grid #6, examples for aggregate-mode patterns. An init property rather than a positional
    /// parameter so that preview captures serialised before the grid existed deserialise to empty,
    /// and a proc not yet redeployed simply yields none.
    /// </summary>
    public IReadOnlyList<MonthlyExample> Examples { get; init; } = [];
}

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
