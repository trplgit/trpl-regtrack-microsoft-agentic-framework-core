using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// The scheduling arithmetic the weekly lane depends on - a pure function, and the kind of thing
/// that looks obviously right and is quietly wrong in production.
/// </summary>
public sealed class FreeDigestScheduleTests
{
    /// <summary>
    /// The week-ending date is the third part of the claim key. If it changed mid-week, every day
    /// would look like a fresh week and the claim would stop preventing anything - so every day
    /// from Monday to Sunday must resolve to the SAME Sunday.
    /// </summary>
    [Fact]
    public void WeekEnding_IsTheSameSundayForEveryDayOfThatWeek()
    {
        // Mon 17 Aug 2026 through Sun 23 Aug 2026.
        var monday = new DateTime(2026, 8, 17);
        var expected = new DateOnly(2026, 8, 23);

        foreach (var offset in Enumerable.Range(0, 7))
        {
            var day = monday.AddDays(offset);
            Assert.Equal(expected, DigestWeek.EndingFor(day));
        }
    }

    /// <summary>A Sunday closes its own week, not the next one - otherwise Sunday sends twice.</summary>
    [Fact]
    public void WeekEnding_OnASunday_IsThatSunday()
    {
        var sunday = new DateTime(2026, 8, 23);

        Assert.Equal(new DateOnly(2026, 8, 23), DigestWeek.EndingFor(sunday));
    }

    /// <summary>The next week resolves to a different key, or nothing would ever send again.</summary>
    [Fact]
    public void WeekEnding_RollsForwardTheFollowingMonday()
    {
        Assert.Equal(new DateOnly(2026, 8, 30), DigestWeek.EndingFor(new DateTime(2026, 8, 24)));
    }

    /// <summary>Time of day must not affect the key - a 06:00 tick and a 23:00 tick are the same week.</summary>
    [Fact]
    public void WeekEnding_IgnoresTimeOfDay()
    {
        var morning = new DateTime(2026, 8, 19, 6, 0, 0);
        var night = new DateTime(2026, 8, 19, 23, 59, 59);

        Assert.Equal(DigestWeek.EndingFor(morning), DigestWeek.EndingFor(night));
    }
}
