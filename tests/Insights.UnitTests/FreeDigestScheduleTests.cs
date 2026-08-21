using Insights.Worker;

namespace Insights.UnitTests;

/// <summary>
/// The two pieces of scheduling arithmetic the weekly lane depends on. Both are pure functions,
/// and both are the kind of thing that looks obviously right and is quietly wrong in production.
/// </summary>
public sealed class FreeDigestScheduleTests
{
    /// <summary>
    /// Design doc 10.3 staggers tenants across the week so 600 digests do not all fire at once.
    /// The anchor MUST be stable across restarts - a tenant that moves day cannot be reasoned
    /// about, and combined with a week-keyed claim it could be mailed twice in one week.
    /// </summary>
    [Fact]
    public void AnchorDay_IsStableAndSpreadAcrossTheWeek()
    {
        var first = Enumerable.Range(1, 700).Select(FreeDigestScheduler.AnchorDayFor).ToList();
        var second = Enumerable.Range(1, 700).Select(FreeDigestScheduler.AnchorDayFor).ToList();

        Assert.Equal(first, second);
        Assert.Equal(7, first.Distinct().Count());

        // Even spread: 700 tenants over 7 days should be 100 each.
        foreach (var day in first.GroupBy(d => d))
            Assert.Equal(100, day.Count());
    }

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
            Assert.Equal(expected, FreeDigestService.WeekEndingFor(day));
        }
    }

    /// <summary>A Sunday closes its own week, not the next one - otherwise Sunday sends twice.</summary>
    [Fact]
    public void WeekEnding_OnASunday_IsThatSunday()
    {
        var sunday = new DateTime(2026, 8, 23);

        Assert.Equal(new DateOnly(2026, 8, 23), FreeDigestService.WeekEndingFor(sunday));
    }

    /// <summary>The next week resolves to a different key, or nothing would ever send again.</summary>
    [Fact]
    public void WeekEnding_RollsForwardTheFollowingMonday()
    {
        Assert.Equal(new DateOnly(2026, 8, 30), FreeDigestService.WeekEndingFor(new DateTime(2026, 8, 24)));
    }

    /// <summary>Time of day must not affect the key - a 06:00 tick and a 23:00 tick are the same week.</summary>
    [Fact]
    public void WeekEnding_IgnoresTimeOfDay()
    {
        var morning = new DateTime(2026, 8, 19, 6, 0, 0);
        var night = new DateTime(2026, 8, 19, 23, 59, 59);

        Assert.Equal(FreeDigestService.WeekEndingFor(morning), FreeDigestService.WeekEndingFor(night));
    }
}
