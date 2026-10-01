using System.Text.Json;
using Insights.Agents;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// The <c>signals</c> block is the judgement the model is handed ready-made: labels, never numbers.
/// [2026-09-23] It used to read only the Overview's fact keys, so the four dimension emails got a
/// block of nulls and read as figure lists. Every slot must now produce its own signals, and every
/// value must be a label that contains no digit - a signal that leaked a number would widen the
/// closed set the validator checks against.
/// </summary>
public sealed class FreeMonthlySignalsTests
{
    private static JsonElement Signals(MonthlyDigestData data)
    {
        var prompt = FreeMonthlyDigestPrompt.Build(data);
        using var doc = JsonDocument.Parse(prompt.UserMessage);
        return doc.RootElement.GetProperty("signals").Clone();
    }

    private static string? Signal(JsonElement signals, string name) =>
        signals.TryGetProperty(name, out var value) ? value.GetString() : null;

    [Fact]
    public void Overview_ReadsLastMonthAndTheBacklog()
    {
        var signals = Signals(MonthlyExamples.Overview());

        Assert.Equal("on_time_almost_always", Signal(signals, "last_month_closing"));      // lm_on_time_pct 91
        Assert.Equal("little_still_open", Signal(signals, "last_month_left_open"));         // 24 of 812
        Assert.Equal("mixed_ages", Signal(signals, "backlog_age"));                         // 38 of 146
        Assert.Equal("a_small_share", Signal(signals, "liability_in_backlog"));             // 12 of 146
        Assert.Equal("some_open_work_has_nobody_assigned", Signal(signals, "ownership"));   // od_no_owner 4
        Assert.Equal("some_overdue_work_was_never_started", Signal(signals, "never_started"));

        // The Overview carries no concentration fact and no expired total, so neither is asserted.
        Assert.Null(Signal(signals, "backlog_concentration"));
        Assert.Null(Signal(signals, "expired_licences"));
    }

    [Fact]
    public void Users_ReadsItsOwnKeys_NotTheOverviews()
    {
        var signals = Signals(MonthlyExamples.Users());

        Assert.Equal("a_small_share", Signal(signals, "liability_in_backlog"));                         // t_od_liability 12 of t_od_total 146 = 8%
        Assert.Equal("most_of_it_sits_with_the_three_holding_the_most", Signal(signals, "backlog_concentration")); // 64%
        Assert.Equal("some_carry_overdue_work", Signal(signals, "overdue_spread"));                     // 17 of 42 people
        Assert.Equal("some_open_work_is_held_by_people_no_longer_active", Signal(signals, "deactivated_owners"));
        Assert.Equal("some_work_is_performed_and_approved_by_the_same_person", Signal(signals, "self_review"));

        // u_open_no_owner is 0: a zero is not a signal.
        Assert.Null(Signal(signals, "ownership"));
        // No t_od_over_90_days in this slot.
        Assert.Null(Signal(signals, "backlog_age"));
    }

    [Fact]
    public void Location_ReadsSpreadAndSinglePersonSites()
    {
        var signals = Signals(MonthlyExamples.Location());

        Assert.Equal("some_carry_overdue_work", Signal(signals, "overdue_spread"));                  // 15 of 22 sites
        Assert.Equal("some_left_last_month_open", Signal(signals, "last_month_slippage_spread"));    // 11 of 22
        Assert.Equal("a_large_part_sits_with_the_three_holding_the_most", Signal(signals, "backlog_concentration")); // 58%
        Assert.Equal("some_sites_have_all_open_work_on_one_person", Signal(signals, "single_person_sites"));
    }

    [Fact]
    public void Act_ReadsSpreadAgainstActsInScope()
    {
        var signals = Signals(MonthlyExamples.Act());

        Assert.Equal("some_carry_overdue_work", Signal(signals, "overdue_spread"));                  // 21 of 37 Acts
        Assert.Equal("some_left_last_month_open", Signal(signals, "last_month_slippage_spread"));    // 16 of 37
        Assert.Equal("a_large_part_sits_with_the_three_holding_the_most", Signal(signals, "backlog_concentration")); // 47%
    }

    /// <summary>
    /// [FOUND LIVE on tenant 1082, 2026-09-23] The month's real story: 47 of 194 August obligations
    /// were left open (24%), while 116 of the 162 due so far in September were already past due
    /// (72%). lm_on_time_pct read 99 because it counts only what completed, and the this-month
    /// denominator was never sent, so the model could see neither half of the contrast.
    /// </summary>
    [Fact]
    public void Overview_ReadsTheContrastBetweenLastMonthAndThisMonth()
    {
        var overview = MonthlyExamples.Overview();
        var facts = overview.Facts.Concat(
        [
            MonthlyExamples.Fact("lm_on_time", 139, asAt: true, window: "prev"),
            MonthlyExamples.Fact("tm_due_so_far", 162, window: "curr"),
        ]).Select(f => f.FactKey switch
        {
            "lm_due" => f with { FactValue = 194 },
            "lm_still_open" => f with { FactValue = 47 },
            "lm_on_time_pct" => f with { FactValue = 99 },
            "tm_open_past_due" => f with { FactValue = 116 },
            _ => f,
        }).ToList();

        var signals = Signals(overview with { Facts = facts });

        Assert.Equal("mixed", Signal(signals, "last_month_closing"));                              // 139 of 194 due, not 99% of completed
        Assert.Equal("a_meaningful_share_still_open", Signal(signals, "last_month_left_open"));   // 47 of 194 = 24%
        Assert.Equal("most_of_it_already_past_due", Signal(signals, "this_month_so_far"));        // 116 of 162 = 72%
        Assert.Equal("this_month_is_slipping_more_than_last_month_did", Signal(signals, "month_trend"));
    }

    [Fact]
    public void ThePeriodDenominators_AlwaysReachTheModel()
    {
        var overview = MonthlyExamples.Overview();
        var facts = overview.Facts.Concat(
        [
            MonthlyExamples.Fact("tm_due_so_far", 162, window: "curr"),   // tier 3 in the fixture helper; tier 4 volume in the proc
        ]).ToList();

        var sent = FreeMonthlyDigestPrompt.ForTheModel(facts, MonthlyDigestSlot.Overview).Select(f => f.FactKey).ToList();

        Assert.Contains("lm_due", sent);
        Assert.Contains("tm_due_so_far", sent);
        Assert.Contains("rm_due", sent);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 1082, 2026-09-23] A rate the proc computed on its whole population
    /// (category_overdue_skew: 32% vs 14%, no BaseCount) was stripped as if it were a small-base
    /// percentage, and the model was handed a named category with no figure at all.
    /// </summary>
    [Fact]
    public void ARateWithNoBaseCount_IsSentToTheModel()
    {
        var overview = MonthlyExamples.Overview();
        var candidates = overview.Candidates.Select(c => c.DefaultSlot == 2
            ? c with { Detector = "category_overdue_skew", EntityKind = "category", ItemCount = null, BaseCount = null, MetricPct = 32, TenantPct = 14 }
            : c).ToList();

        var prompt = FreeMonthlyDigestPrompt.Build(overview with { Candidates = candidates });

        Assert.Contains("\"MetricPct\":32", prompt.UserMessage);
        Assert.Contains("\"TenantPct\":14", prompt.UserMessage);
        Assert.Contains(32, prompt.AllowedPercentages);
        Assert.Contains(14, prompt.AllowedPercentages);
    }

    [Fact]
    public void Licence_ReadsTheRenewalSplit()
    {
        var signals = Signals(MonthlyExamples.Licence());

        Assert.Equal("some_have_no_renewal_in_progress", Signal(signals, "expired_licences"));   // 6 of 9
        Assert.Equal("some_have_nothing_filed", Signal(signals, "expiring_this_month"));         // 3 of 5
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("users")]
    [InlineData("location")]
    [InlineData("act")]
    [InlineData("licence")]
    public void EverySlotGetsAtLeastOneSignal_AndNoSignalCarriesADigit(string slot)
    {
        var signals = Signals(MonthlyExamples.ByName(slot));

        var values = signals.EnumerateObject().Select(p => p.Value.GetString()).ToList();

        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.DoesNotContain(v!, c => char.IsDigit(c)));
    }

    [Fact]
    public void AZeroNumeratorIsStillASignal_WhenTheWholeIsKnown()
    {
        // Every expired licence has a renewal in progress: a real state, computed from ALL facts
        // (not only the ones sent), so the model may be told it.
        var facts = new[] { MonthlyExamples.Fact("lic_total", 9), MonthlyExamples.Fact("lic_expired_total", 9), MonthlyExamples.Fact("lic_expired_unrenewed", 0) };
        var signals = Signals(MonthlyExamples.Licence() with { Facts = facts, HeadlineSource = "candidate" });

        Assert.Equal("most_are_being_renewed", Signal(signals, "expired_licences"));
    }
}
