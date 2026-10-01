namespace Insights.Worker;

/// <summary>
/// When the weekly lane wakes, and whether a phase is due - pure, so the arithmetic is unit-tested.
///
/// -- WHY CLOCK-ALIGNED, NOT A FIXED TIMER (2026-09-25) -----------------------------------------
/// The lane used to tick on a PeriodicTimer counted from whenever the worker booted: started at
/// 10:37, it checked at xx:37, so an "8am" send really began at 08:37 - anywhere up to one full
/// interval late, depending on deploy time. Now each wake sleeps until the EARLIER of the next
/// phase start (GenerateDay GenerateHourLocal:00, SendDay SendHourLocal:00) or one check interval,
/// then re-reads the clock. So:
///   * each phase starts exactly on the hour it is configured for;
///   * no single sleep is ever longer than the interval - a multi-day sleep would be one timer
///     that never re-checks the clock, and a paused VM or clock change could silently miss Sunday;
///   * on a phase day, the interval-spaced wakes are the catch-up re-checks (a restart mid-run, or
///     a tenant that failed to enqueue, is picked up on the next one).
/// </summary>
public static class FreeDigestScheduleClock
{
    public static bool IsGenerateDue(DateTime localNow, FreeDigestSettings settings) =>
        localNow.DayOfWeek == settings.GenerateDay && localNow.Hour >= settings.GenerateHourLocal;

    public static bool IsSendDue(DateTime localNow, FreeDigestSettings settings) =>
        localNow.DayOfWeek == settings.SendDay && localNow.Hour >= settings.SendHourLocal;

    /// <summary>How long to sleep from <paramref name="utcNow"/> before the next check. Always &gt; 0 and &lt;= the check interval.</summary>
    public static TimeSpan NextWakeDelay(DateTime utcNow, FreeDigestSettings settings)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), settings.ScheduleTimeZone);

        var nextGenerate = NextOccurrence(localNow, settings.GenerateDay, settings.GenerateHourLocal);
        var nextSend = NextOccurrence(localNow, settings.SendDay, settings.SendHourLocal);
        var untilNextPhase = (nextGenerate < nextSend ? nextGenerate : nextSend) - localNow;

        return untilNextPhase < settings.ScheduleCheckInterval ? untilNextPhase : settings.ScheduleCheckInterval;
    }

    /// <summary>The first <paramref name="day"/> at <paramref name="hour"/>:00 strictly after <paramref name="localNow"/>.</summary>
    internal static DateTime NextOccurrence(DateTime localNow, DayOfWeek day, int hour)
    {
        var daysAhead = ((int)day - (int)localNow.DayOfWeek + 7) % 7;
        var candidate = localNow.Date.AddDays(daysAhead).AddHours(hour);

        return candidate > localNow ? candidate : candidate.AddDays(7);
    }
}
