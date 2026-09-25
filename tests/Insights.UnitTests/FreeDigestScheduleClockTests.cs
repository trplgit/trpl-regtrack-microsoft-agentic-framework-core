using Insights.Worker;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// The clock-aligned wake (2026-09-25): phases start exactly on their configured hour, no single
/// sleep is longer than the check interval, and a worker started mid-phase catches up at once.
/// All times below are IST (the configured zone); the clock is fed UTC, as in production.
/// </summary>
public sealed class FreeDigestScheduleClockTests
{
    private static readonly TimeZoneInfo Ist = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

    private static FreeDigestSettings Settings() => new()
    {
        FromAddress = "noreply@example.com",
        FromName = "RegTrack Insights",
        UpgradeUrl = "https://example.com/upgrade",
        UnsubscribeBaseUrl = "https://example.com/unsubscribe",
        UnsubscribeSigningKey = "test-key",
        ScheduleTimeZone = Ist,
        GenerateDay = DayOfWeek.Sunday,
        GenerateHourLocal = 0,
        SendDay = DayOfWeek.Monday,
        SendHourLocal = 8,
        ScheduleCheckInterval = TimeSpan.FromMinutes(60),
    };

    // 2026-09-27 is a Sunday, 2026-09-28 a Monday.
    private static DateTime UtcFromIst(int day, int hour, int minute = 0, int second = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 9, day, hour, minute, second, DateTimeKind.Unspecified), Ist);

    private static TimeSpan Delay(DateTime utc) => FreeDigestScheduleClock.NextWakeDelay(utc, Settings());

    [Fact]
    public void MidWeek_SleepsOnlyOneInterval_NeverDaysAtATime()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), Delay(UtcFromIst(23, 14, 0)));   // Wednesday 14:00
    }

    [Fact]
    public void LateSaturday_WakesExactlyAtSundayMidnight()
    {
        Assert.Equal(TimeSpan.FromMinutes(23), Delay(UtcFromIst(26, 23, 37)));  // Saturday 23:37 -> Sunday 00:00
    }

    [Fact]
    public void MondayMorning_WakesExactlyAtEight()
    {
        Assert.Equal(TimeSpan.FromMinutes(25), Delay(UtcFromIst(28, 7, 35)));   // Monday 07:35 -> 08:00
    }

    /// <summary>The bug this replaces: a boot-relative timer turned 08:00 into 08:37. Now any odd minute still lands on the hour.</summary>
    [Fact]
    public void AWakeAFewMillisecondsEarly_SleepsTheRemainderAndStillHitsTheHour()
    {
        var justBefore = UtcFromIst(28, 7, 59, 59).AddMilliseconds(990);
        var delay = Delay(justBefore);

        Assert.Equal(TimeSpan.FromMilliseconds(10), delay);
        Assert.True(FreeDigestScheduleClock.IsSendDue(TimeZoneInfo.ConvertTimeFromUtc(justBefore + delay, Ist), Settings()));
    }

    [Fact]
    public void OnAPhaseDay_TheIntervalIsTheCatchUpRecheck()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), Delay(UtcFromIst(27, 5, 0)));    // Sunday 05:00 -> 06:00
        Assert.Equal(TimeSpan.FromMinutes(60), Delay(UtcFromIst(28, 10, 0)));   // Monday 10:00 -> 11:00
    }

    /// <summary>Exactly on a phase start, the NEXT start is a week away - the delay is the interval, not zero (no busy loop).</summary>
    [Fact]
    public void ExactlyOnAPhaseStart_TheDelayIsNeverZero()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), Delay(UtcFromIst(28, 8, 0)));
        Assert.Equal(TimeSpan.FromMinutes(60), Delay(UtcFromIst(27, 0, 0)));
    }

    [Fact]
    public void AShortInterval_IsStillHonouredAsTheCap()
    {
        var settings = Settings();
        var shortInterval = new FreeDigestSettings
        {
            FromAddress = settings.FromAddress, FromName = settings.FromName, UpgradeUrl = settings.UpgradeUrl,
            UnsubscribeBaseUrl = settings.UnsubscribeBaseUrl, UnsubscribeSigningKey = settings.UnsubscribeSigningKey,
            ScheduleTimeZone = Ist, SendHourLocal = 8, ScheduleCheckInterval = TimeSpan.FromMinutes(5),
        };

        Assert.Equal(TimeSpan.FromMinutes(5), FreeDigestScheduleClock.NextWakeDelay(UtcFromIst(28, 7, 0), shortInterval));
    }

    [Theory]
    [InlineData(27, 0, 0, true)]    // Sunday 00:00 - generation opens
    [InlineData(27, 23, 59, true)]  // still Sunday
    [InlineData(26, 23, 59, false)] // Saturday
    [InlineData(28, 0, 0, false)]   // Monday
    public void GenerateIsDueAllOfSundayFromMidnight(int day, int hour, int minute, bool due)
    {
        Assert.Equal(due, FreeDigestScheduleClock.IsGenerateDue(new DateTime(2026, 9, day, hour, minute, 0), Settings()));
    }

    [Theory]
    [InlineData(28, 7, 59, false)]  // Monday 07:59
    [InlineData(28, 8, 0, true)]    // Monday 08:00 - send opens
    [InlineData(28, 23, 0, true)]   // restart late Monday - catches up
    [InlineData(29, 8, 0, false)]   // Tuesday
    public void SendIsDueOnMondayFromEight(int day, int hour, int minute, bool due)
    {
        Assert.Equal(due, FreeDigestScheduleClock.IsSendDue(new DateTime(2026, 9, day, hour, minute, 0), Settings()));
    }
}
