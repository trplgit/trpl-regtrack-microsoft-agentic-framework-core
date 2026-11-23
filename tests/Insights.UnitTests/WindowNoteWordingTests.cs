using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// [ADDED 2026-11-03, FOUND LIVE] Demo tenant 1899 Risk report, last_30_days on 3 Nov: header said
/// "5 Oct 2026 - 3 Nov 2026" (right) but a data note said "through 4 Nov 2026" (wrong). The proc's
/// 'window' data_quality note prints @WindowEnd, which is EXCLUSIVE (the day after the last day).
/// Texts below are the real deployed proc wordings (demo, 2026-11-03).
/// </summary>
public sealed class WindowNoteWordingTests
{
    private static readonly DateTime EndExclusive = new(2026, 11, 4);

    private static string Rewrite(string detail, DateTime? end = null) =>
        Assert.Single(WindowNoteWording.Normalize([new DataQualityNote { Issue = "window", Detail = detail }], end ?? EndExclusive)).Detail;

    [Fact]
    public void Risk_style_between_note_states_the_real_last_day()
    {
        var d = Rewrite("Scoped to obligations with a scheduled occurrence between 2026-10-05 and 2026-11-04. " +
                        "An obligation with no occurrence in this window is excluded entirely.");
        Assert.Equal("Scoped to obligations with a scheduled occurrence between 2026-10-05 and 2026-11-03 (both days included). " +
                     "An obligation with no occurrence in this window is excluded entirely.", d);
    }

    [Fact]
    public void TimelinessFY_style_to_note_states_the_real_last_day()
    {
        var d = Rewrite("On-time rate computed over the caller-supplied window 2026-10-05 to 2026-11-04.");
        Assert.Equal("On-time rate computed over the caller-supplied window 2026-10-05 to 2026-11-03 (both days included).", d);
    }

    [Fact]
    public void Event_note_that_already_prints_the_last_day_is_not_shifted_again()
    {
        const string ev = "Counts every active event-based due date between 2026-10-05 and 2026-11-03 - the same rows as the Event Based tabs.";
        Assert.Equal(ev, Rewrite(ev));
    }

    [Fact]
    public void Other_issues_are_untouched()
    {
        var other = new DataQualityNote { Issue = "nature_untagged", Detail = "between 2026-10-05 and 2026-11-04" };
        Assert.Same(other, Assert.Single(WindowNoteWording.Normalize([other], EndExclusive)));
    }

    [Fact]
    public void No_window_end_known_means_no_change()
    {
        var note = new DataQualityNote { Issue = "window", Detail = "between 2026-10-05 and 2026-11-04" };
        Assert.Same(note, Assert.Single(WindowNoteWording.Normalize([note], null)));
    }

    [Fact]
    public void Single_day_window_reads_as_that_day()
    {
        Assert.Equal("between 2026-11-03 and 2026-11-03 (both days included)",
            Rewrite("between 2026-11-03 and 2026-11-04"));
    }
}
