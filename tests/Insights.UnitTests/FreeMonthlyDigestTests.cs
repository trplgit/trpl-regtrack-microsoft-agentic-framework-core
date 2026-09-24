using System.Text.Json;
using System.Text.RegularExpressions;
using Insights.Agents;
using Insights.Domain;
using Insights.Worker;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Insights.UnitTests;

public sealed class MonthlyDigestCalendarTests
{
    [Theory]
    // October 2026 has 4 Sundays (4, 11, 18, 25); November 2026 has 5 (1 .. 29).
    [InlineData("2026-10-04", MonthlyDigestSlot.Overview, 1, 4)]
    [InlineData("2026-10-11", MonthlyDigestSlot.Users, 2, 4)]
    [InlineData("2026-10-18", MonthlyDigestSlot.Location, 3, 4)]
    [InlineData("2026-10-25", MonthlyDigestSlot.Act, 4, 4)]
    [InlineData("2026-11-01", MonthlyDigestSlot.Overview, 1, 5)]
    [InlineData("2026-11-29", MonthlyDigestSlot.Licence, 5, 5)]
    public void For_PicksTheSlotFromTheDateAlone(string sunday, MonthlyDigestSlot slot, int sundayOfMonth, int sundaysInMonth)
    {
        var edition = MonthlyDigestCalendar.For(DateOnly.Parse(sunday));

        Assert.Equal(slot, edition.Slot);
        Assert.Equal(sundayOfMonth, edition.SundayOfMonth);
        Assert.Equal(sundaysInMonth, edition.SundaysInMonth);
        Assert.Equal(1, edition.CurrMonthStart.Day);
        Assert.Equal(edition.Sunday.Month, edition.CurrMonthStart.Month);
    }

    [Fact]
    public void For_RejectsANonSunday() =>
        Assert.Throws<ArgumentException>(() => MonthlyDigestCalendar.For(new DateOnly(2026, 10, 5)));

    [Fact]
    public void Next_AfterTheLastSundayOfAMonth_IsTheNextMonthsOverview()
    {
        var next = MonthlyDigestCalendar.Next(MonthlyDigestCalendar.For(new DateOnly(2026, 10, 25)));

        Assert.Equal(MonthlyDigestSlot.Overview, next.Slot);
        Assert.Equal(new DateOnly(2026, 11, 1), next.CurrMonthStart);
    }

    /// <summary>
    /// sql/34 THROWs 51237 when @AsOf is outside @CurrMonthStart's month, so a run that slipped past
    /// midnight must still report inside November.
    ///
    /// <para>[UPDATED 2026-09-22] It now reports as at the edition's OWN Sunday rather than the
    /// month's last moment - that is the position the scheduler would have seen on the day, and it
    /// is what makes a preview of five editions show five different weeks instead of one instant
    /// repeated. The guarantee this test exists for is unchanged: the result is inside the month.</para>
    /// </summary>
    [Fact]
    public void AsOfWithinMonth_ClampsARetryThatCrossedIntoTheNextMonth()
    {
        var edition = MonthlyDigestCalendar.For(new DateOnly(2026, 11, 29));

        var asOf = MonthlyDigestCalendar.AsOfWithinMonth(new DateTime(2026, 12, 1, 2, 0, 0), edition);

        Assert.Equal(new DateTime(2026, 11, 29, 23, 59, 59), asOf);
        Assert.Equal(11, asOf.Month);
    }

    /// <summary>
    /// Each edition reports as at its own Sunday, so a preview of a whole month shows the position
    /// moving week by week rather than one instant rendered five ways.
    /// </summary>
    [Fact]
    public void AsOfWithinMonth_UsesEachEditionsOwnSunday()
    {
        var wellAfterTheMonth = new DateTime(2026, 9, 22, 10, 0, 0);

        var overview = MonthlyDigestCalendar.AsOfWithinMonth(wellAfterTheMonth, MonthlyDigestCalendar.For(new DateOnly(2026, 8, 2)));
        var licence = MonthlyDigestCalendar.AsOfWithinMonth(wellAfterTheMonth, MonthlyDigestCalendar.For(new DateOnly(2026, 8, 30)));

        Assert.Equal(new DateTime(2026, 8, 2, 23, 59, 59), overview);
        Assert.Equal(new DateTime(2026, 8, 30, 23, 59, 59), licence);
    }

    [Fact]
    public void AsOfWithinMonth_LeavesAnInMonthTimeAlone()
    {
        var edition = MonthlyDigestCalendar.For(new DateOnly(2026, 10, 4));
        var now = new DateTime(2026, 10, 4, 6, 30, 0);

        Assert.Equal(now, MonthlyDigestCalendar.AsOfWithinMonth(now, edition));
    }

    [Fact]
    public void PeriodLabels_NameTheMonthAndTopic()
    {
        var edition = MonthlyDigestCalendar.For(new DateOnly(2026, 10, 11));

        Assert.Equal("October 2026 - People and ownership", edition.PeriodLabel);
        Assert.Equal("October 2026 people and ownership", edition.PeriodPhrase);
    }
}

public sealed class FreeMonthlyDigestPromptTests
{
    [Fact]
    public void Build_NeverShowsTheModelARealName_AndBindsItAfterwards()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Licence());

        Assert.DoesNotContain("Trade Licence", prompt.UserMessage);
        Assert.DoesNotContain("Pune Plant", prompt.UserMessage);
        Assert.Contains("{{NAME_1}}", prompt.UserMessage);
        Assert.Equal("Trade Licence", prompt.Bindings["{{NAME_1}}"]);
        Assert.Equal("Pune Plant", prompt.Bindings["{{NAME_1_AT}}"]);
        Assert.Equal("30 Nov 2026", prompt.Bindings["{{DATE_1}}"]);
        Assert.Equal("November", prompt.Bindings["{{CURR_MONTH}}"]);
        Assert.Equal("October", prompt.Bindings["{{PREV_MONTH}}"]);
        Assert.Contains("\"slot\":\"licence\"", prompt.UserMessage);
    }

    [Fact]
    public void Build_SendsExistenceFactsEvenAtZero()
    {
        // 06e and 06b each branch on these to write a "nothing in scope" email, so unlike every
        // other count they have to survive ForTheModel at zero (shared Rule 11's own exception).
        var data = MonthlyExamples.Overview() with
        {
            Facts = [.. MonthlyExamples.Overview().Facts, MonthlyExamples.Fact("lic_total", 0), MonthlyExamples.Fact("u_open_items", 0)],
        };

        using var json = JsonDocument.Parse(FreeMonthlyDigestPrompt.Build(data).UserMessage);
        var keys = json.RootElement.GetProperty("facts").EnumerateArray()
            .Select(f => f.GetProperty("FactKey").GetString()).ToList();

        Assert.Contains("lic_total", keys);
        Assert.Contains("u_open_items", keys);
    }

    [Fact]
    public void ForTheModel_KeepsTheSectionBaseBehindAnOfThoseFact()
    {
        // "of those are still open" counts a share of lm_due, not of the line above it. Keeping
        // the previous line instead is how a draft reached "29 of the 41" - a denominator the
        // model built by subtraction because the real one was never sent.
        var kept = FreeMonthlyDigestPrompt.ForTheModel(MonthlyExamples.Overview().Facts, MonthlyDigestSlot.Overview)
            .Select(f => f.FactKey).ToList();

        Assert.Contains("lm_still_open", kept);
        Assert.Contains("lm_due", kept);
    }

    [Fact]
    public void Build_DropsImmaterialZeroFactsAndTheirValues()
    {
        // A dropped fact's value must NOT stay in the allowed set: the validator would then pass a
        // draft citing a figure the model was never shown.
        var data = MonthlyExamples.Overview() with
        {
            Facts = [.. MonthlyExamples.Overview().Facts, MonthlyExamples.Fact("tm_open_liability", 0) with { SeverityTier = 9 }],
        };

        var prompt = FreeMonthlyDigestPrompt.Build(data);
        using var json = JsonDocument.Parse(prompt.UserMessage);
        var keys = json.RootElement.GetProperty("facts").EnumerateArray()
            .Select(f => f.GetProperty("FactKey").GetString()).ToList();

        Assert.DoesNotContain("tm_open_liability", keys);
    }

    [Fact]
    public void Build_AnUnlabelledCandidateGetsNoNamePlaceholder()
    {
        var data = MonthlyExamples.Users() with
        {
            Candidates = [MonthlyExamples.Users().Candidates[0] with { EntityLabel = null }, MonthlyExamples.Users().Candidates[1]],
            HeadlineSource = "fact",
            Facts = [.. MonthlyExamples.Users().Facts.Select(f => f with { IsHeadline = f.FactKey == "u_open_items_inactive_owner" })],
        };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.False(prompt.Bindings.ContainsKey("{{NAME_1}}"));
        Assert.True(prompt.Bindings.ContainsKey("{{NAME_2}}"));
    }

    [Fact]
    public void Build_ThrowsWhenACandidateHeadlineHasNoSlotOneCandidate()
    {
        var data = MonthlyExamples.Users() with { Candidates = [] };

        Assert.Throws<InvalidOperationException>(() => FreeMonthlyDigestPrompt.Build(data));
    }

    /// <summary>
    /// [DECIDED 2026-09-23] Two names per email read as a list of counts with a name or two
    /// attached - the reader's own words were "data slapping". The procs already return up to five
    /// candidates per detector; the email now names up to four of them, slots 1-2 as the proc
    /// marked them and slots 3-4 from the next-ranked candidates that are a DIFFERENT thing.
    /// </summary>
    [Fact]
    public void Build_NamesUpToFourFindings_FromTheProcsRankedCandidates()
    {
        var location = MonthlyExamples.Location();
        var data = location with
        {
            Candidates =
            [
                location.Candidates[0],
                location.Candidates[1],
                MonthlyExamples.Unslotted("last_month_slippage", "location", 2001, "Jabalpur Depot", rank: 2, priority: 2, item: 4, baseCount: 6),
                MonthlyExamples.Unslotted("single_point_of_failure", "location", 2002, "Khavda Plant", rank: 2, priority: 3, item: 30),
                MonthlyExamples.Unslotted("ghost_location", "location", 2003, "Surat Office", rank: 1, priority: 7),
            ],
        };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal(4, prompt.NamedFindings.Count);
        Assert.Equal("Bhiwandi Warehouse", prompt.Bindings["{{NAME_1}}"]);
        Assert.Equal("Nashik Depot", prompt.Bindings["{{NAME_2}}"]);
        Assert.Equal("Jabalpur Depot", prompt.Bindings["{{NAME_3}}"]);
        Assert.Equal("Khavda Plant", prompt.Bindings["{{NAME_4}}"]);
        Assert.False(prompt.Bindings.ContainsKey("{{NAME_5}}"));
        Assert.DoesNotContain("Surat Office", prompt.UserMessage);
        Assert.Contains("{{NAME_4}}", prompt.UserMessage);
        Assert.Contains("\"Slot\":4", prompt.UserMessage);
    }

    /// <summary>A site the proc already put in slot 1 or 2 is never named again under a third slot, whatever detector flagged it.</summary>
    [Fact]
    public void Build_NeverNamesTheSameEntityTwice()
    {
        var location = MonthlyExamples.Location();
        var bhiwandiAgain = MonthlyExamples.Unslotted("single_point_of_failure", "location", location.Candidates[0].EntityId!.Value, "Bhiwandi Warehouse", rank: 2, priority: 3, item: 12);
        var data = location with { Candidates = [location.Candidates[0], location.Candidates[1], bhiwandiAgain] };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal(2, prompt.NamedFindings.Count);
        Assert.False(prompt.Bindings.ContainsKey("{{NAME_3}}"));
    }

    /// <summary>An extra candidate with no label (names withheld) cannot take a slot - it would be a placeholder with nothing to bind.</summary>
    [Fact]
    public void Build_SkipsAnUnlabelledExtraCandidate()
    {
        var location = MonthlyExamples.Location();
        var data = location with
        {
            Candidates =
            [
                location.Candidates[0],
                location.Candidates[1],
                MonthlyExamples.Unslotted("last_month_slippage", "location", 2001, null, rank: 2, priority: 2, item: 4, baseCount: 6),
                MonthlyExamples.Unslotted("single_point_of_failure", "location", 2002, "Khavda Plant", rank: 2, priority: 3, item: 30),
            ],
        };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal(3, prompt.NamedFindings.Count);
        Assert.Equal("Khavda Plant", prompt.Bindings["{{NAME_3}}"]);
    }

    /// <summary>
    /// [DECIDED 2026-09-23] An aggregate-mode pattern ("18 of your 24 locations ...") may carry up
    /// to three EXAMPLE members from grid #6, so the sentence can end "including Khavda, Jabalpur
    /// and Surat". Examples bind through {{EG_n}}, never {{NAME_n}}: they are illustrations of the
    /// one aggregate finding, not findings of their own.
    /// </summary>
    [Fact]
    public void Build_AttachesExamplesToTheAggregateFactTheyIllustrate()
    {
        var location = MonthlyExamples.Location();
        var data = location with
        {
            Facts =
            [
                .. location.Facts,
                new("pat_chronic_backlog", 18, "locations have an unusually high share of overdue work open for more than 90 days", "patterns", 720, "stock", "operational_continuity", 2, false, false),
                new("pat_chronic_backlog_of", 24, "locations with overdue work were compared", "patterns", 721, "ctx", "volume", 5, false, false),
            ],
            Examples =
            [
                MonthlyExamples.Example("chronic_backlog", "pat_chronic_backlog", 1, 3001, "Khavda Plant", item: 210, baseCount: 300),
                MonthlyExamples.Example("chronic_backlog", "pat_chronic_backlog", 2, 3002, "Jabalpur Depot", item: 90, baseCount: 120),
                MonthlyExamples.Example("chronic_backlog", "pat_chronic_backlog", 3, 3003, "Surat Office", item: 40, baseCount: 55),
                // A pattern the model was not sent has no fact to hang an example on.
                MonthlyExamples.Example("liability_share", "pat_liability_share", 1, 3004, "Nagpur Works", item: 12, baseCount: 20),
            ],
        };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal(3, prompt.Examples.Count);
        Assert.Equal("Khavda Plant", prompt.Bindings["{{EG_1}}"]);
        Assert.Equal("Jabalpur Depot", prompt.Bindings["{{EG_2}}"]);
        Assert.Equal("Surat Office", prompt.Bindings["{{EG_3}}"]);
        Assert.False(prompt.Bindings.ContainsKey("{{EG_4}}"));
        Assert.DoesNotContain("Khavda Plant", prompt.UserMessage);
        Assert.DoesNotContain("Nagpur Works", prompt.UserMessage);
        Assert.Contains("\"examples\":[", prompt.UserMessage);
        Assert.Contains("\"PatternFactKey\":\"pat_chronic_backlog\"", prompt.UserMessage);
        Assert.Contains("\"Counts\":\"overdue obligations more than 90 days late\"", prompt.UserMessage);
        Assert.Contains(210, prompt.AllowedNumbers);
        Assert.Contains(300, prompt.AllowedNumbers);
        // Examples never carry a rate, so the percentage set is untouched by them.
        Assert.DoesNotContain(70, prompt.AllowedPercentages);
    }

    /// <summary>Six per email, rank 1 of every pattern before rank 2 of any - breadth first, then numbered in reading order.</summary>
    [Fact]
    public void Build_CapsExamplesAtSix_RoundRobinAcrossPatterns()
    {
        var location = MonthlyExamples.Location();
        var data = location with
        {
            Facts =
            [
                .. location.Facts,
                new("pat_liability_share", 7, "locations have an unusually high share ...", "patterns", 710, "stock", "personal_liability", 1, false, false),
                new("pat_liability_share_of", 20, "locations with overdue work were compared", "patterns", 711, "ctx", "volume", 5, false, false),
                new("pat_chronic_backlog", 18, "locations have an unusually high share ...", "patterns", 720, "stock", "operational_continuity", 2, false, false),
                new("pat_chronic_backlog_of", 24, "locations with overdue work were compared", "patterns", 721, "ctx", "volume", 5, false, false),
                new("pat_overdue_concentration", 6, "locations each hold a disproportionate share", "patterns", 730, "stock", "operational_continuity", 2, false, false),
                new("pat_overdue_concentration_of", 20, "locations with overdue work were compared", "patterns", 731, "ctx", "volume", 5, false, false),
            ],
            Examples =
            [
                .. Enumerable.Range(1, 3).Select(r => MonthlyExamples.Example("liability_share", "pat_liability_share", r, 100 + r, $"Liab {r}", item: 10 - r)),
                .. Enumerable.Range(1, 3).Select(r => MonthlyExamples.Example("chronic_backlog", "pat_chronic_backlog", r, 200 + r, $"Chron {r}", item: 10 - r)),
                .. Enumerable.Range(1, 3).Select(r => MonthlyExamples.Example("overdue_concentration", "pat_overdue_concentration", r, 300 + r, $"Conc {r}", item: 10 - r)),
            ],
        };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal(6, prompt.Examples.Count);
        Assert.Equal(new[] { "Liab 1", "Liab 2", "Chron 1", "Chron 2", "Conc 1", "Conc 2" },
            prompt.Examples.Select(e => e.Example.EntityLabel).ToArray());
        Assert.Equal(Enumerable.Range(1, 6), prompt.Examples.Select(e => e.Number));
    }

    /// <summary>
    /// [FOUND LIVE on UAT tenant 5, 2026-09-23] Two licence rows with the same type, site and
    /// expiry date are one licence to the reader; naming both produced "Transport at Rudra Customer
    /// on 9 Sep 2026, Transport at Rudra Customer on 9 Sep 2026".
    /// </summary>
    [Fact]
    public void Build_NeverNamesTwoRowsTheReaderCannotTellApart()
    {
        var licence = MonthlyExamples.Licence();
        var twin = licence.Candidates[0] with { DefaultSlot = null, EntityId = 9999, RankInDetector = 2 };
        var data = licence with { Candidates = [licence.Candidates[0], licence.Candidates[1], twin] };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal(2, prompt.NamedFindings.Count);
        Assert.False(prompt.Bindings.ContainsKey("{{NAME_3}}"));
    }

    /// <summary>Examples are listed in must_use beside the names, because a requirement stated in the data is the one the model follows.</summary>
    [Fact]
    public void Build_ListsExamplesInMustUse()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.LocationWithAggregateExamples());

        Assert.Contains("\"must_use\":[\"{{NAME_1}}\",\"{{NAME_2}}\",\"{{EG_1}}\",\"{{EG_2}}\",\"{{EG_3}}\"]", prompt.UserMessage);
    }

    /// <summary>
    /// [OWNER, PROD tenant 1008, 2026-09-23] "your MVAST site, with 1 of 1" - a site holding one
    /// licence is a member with nothing to compare. A base of one is dropped, for examples and for
    /// findings alike, and a dropped slot-1 finding promotes the next rather than breaking the email.
    /// </summary>
    [Fact]
    public void Build_NeverOffersAOneOfOneMember()
    {
        var licence = MonthlyExamples.Licence();
        var data = licence with
        {
            Facts =
            [
                .. licence.Facts,
                new("pat_expired_unrenewed_location", 29, "locations have an unusually high share of licences expired with no renewal", "patterns", 700, "stock", "licence_continuity", 1, false, false),
                new("pat_expired_unrenewed_location_of", 65, "locations holding licences were compared", "patterns", 701, "ctx", "volume", 5, false, false),
            ],
            Examples =
            [
                MonthlyExamples.Example("expired_unrenewed_location", "pat_expired_unrenewed_location", 1, 5001, "Manesar", item: 8, baseCount: 12),
                MonthlyExamples.Example("expired_unrenewed_location", "pat_expired_unrenewed_location", 2, 5002, "MVAST", item: 1, baseCount: 1),
                MonthlyExamples.Example("expired_unrenewed_location", "pat_expired_unrenewed_location", 3, 5003, "MIL", item: 1, baseCount: 1),
            ],
        };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Single(prompt.Examples);
        Assert.Equal("Manesar", prompt.Bindings["{{EG_1}}"]);

        // A slot-1 FINDING on a base of one is dropped too, and the second finding takes slot 1.
        var users = MonthlyExamples.Users();
        var thin = users with
        {
            Candidates = [users.Candidates[0] with { ItemCount = 1, BaseCount = 1 }, users.Candidates[1]],
        };
        var promoted = FreeMonthlyDigestPrompt.Build(thin);
        Assert.Single(promoted.NamedFindings);
        Assert.Equal("Meera Nair", promoted.Bindings["{{NAME_1}}"]);
        Assert.Equal("overdue_concentration", promoted.NamedFindings[0].Candidate.Detector);
    }

    /// <summary>A site that already holds a {{NAME_n}} slot is one thing with one token - never also an example.</summary>
    [Fact]
    public void Build_DropsAnExampleThatIsAlreadyANamedFinding()
    {
        var location = MonthlyExamples.Location();
        var data = location with
        {
            Facts =
            [
                .. location.Facts,
                new("pat_chronic_backlog", 18, "locations ...", "patterns", 720, "stock", "operational_continuity", 2, false, false),
                new("pat_chronic_backlog_of", 24, "locations with overdue work were compared", "patterns", 721, "ctx", "volume", 5, false, false),
            ],
            Examples =
            [
                MonthlyExamples.Example("chronic_backlog", "pat_chronic_backlog", 1, location.Candidates[0].EntityId!.Value, "Bhiwandi Warehouse", item: 210, baseCount: 300),
                MonthlyExamples.Example("chronic_backlog", "pat_chronic_backlog", 2, 3002, "Jabalpur Depot", item: 90, baseCount: 120),
            ],
        };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Single(prompt.Examples);
        Assert.Equal("Jabalpur Depot", prompt.Bindings["{{EG_1}}"]);
    }

    /// <summary>The relative-size rule still applies to every extra name: a 1-of-1 beside a 579 is noise, not a fourth paragraph.</summary>
    [Fact]
    public void Build_DropsAnExtraFindingThatIsTrivialBesideTheFirst()
    {
        var users = MonthlyExamples.Users();
        var data = users with
        {
            Candidates =
            [
                users.Candidates[0] with { ItemCount = 579 },
                users.Candidates[1] with { ItemCount = 51 },
                MonthlyExamples.Unslotted("self_review", "person", 3001, "Anil Shah", rank: 1, priority: 5, item: 1, baseCount: 1),
            ],
        };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal(2, prompt.NamedFindings.Count);
        Assert.DoesNotContain("{{NAME_3}}", prompt.UserMessage);
    }
}

/// <summary>
/// The email is about last month and this month: the all-time overdue / expired backlog may appear,
/// but never as the lead - whatever the tenant's numbers are.
/// </summary>
public sealed class PeriodHeadlineTests
{
    private static MonthlyFact F(string key, int value, string window, int tier = 2, string impact = "operational_continuity", bool headline = false, int order = 100) =>
        new(key, value, key, "s", order, window, impact, tier, false, headline);

    private static MonthlyDigestData Overview(params MonthlyFact[] facts) =>
        MonthlyExamples.Overview() with { HeadlineSource = "fact", Facts = facts, Candidates = [] };

    [Theory]
    [InlineData("od_total", "stock", true)]
    [InlineData("od_liability", "stock", true)]
    [InlineData("t_od_over_90_days", "stock", true)]
    [InlineData("lic_expired_unrenewed", "stock", true)]
    [InlineData("loc_with_liability_overdue", "stock", true)]
    [InlineData("u_top3_overdue_share_pct", "stock", true)]
    [InlineData("pat_liability_share", "stock", true)]
    [InlineData("u_open_items_inactive_owner", "stock", false)]   // current state of work, not backlog
    [InlineData("u_open_no_owner", "stock", false)]
    [InlineData("loc_single_performer", "stock", false)]
    [InlineData("tm_open_past_due", "curr", false)]               // this month's slippage is the period
    [InlineData("lm_open_liability", "prev", false)]
    public void IsBacklogFact_ClassifiesByKeyAndWindow(string key, string window, bool expected) =>
        Assert.Equal(expected, FreeMonthlyDigestPrompt.IsBacklogFact(F(key, 1, window)));

    [Fact]
    public void ABacklogHeadline_IsReplacedByTheMostSevereNonZeroPeriodFact()
    {
        var data = Overview(
            F("od_liability", 2230, "stock", tier: 1, impact: "personal_liability", headline: true, order: 355),
            F("lm_open_liability", 0, "prev", tier: 1, impact: "personal_liability", order: 150),
            F("lm_still_open", 24, "prev", tier: 2, order: 140),
            F("rm_liability", 3, "curr", tier: 1, impact: "personal_liability", order: 410),
            F("rm_due", 268, "curr", tier: 4, impact: "volume", order: 400));

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal("rm_liability", prompt.Data.Facts.Single(f => f.IsHeadline).FactKey);
        Assert.Equal("3", prompt.HeadlineMarker);
    }

    [Fact]
    public void AQuietWindow_LeadsWithThePeriodVolume_NotTheBacklog()
    {
        var data = Overview(
            F("od_total", 22070, "stock", headline: true, order: 300),
            F("lm_due", 0, "prev", tier: 4, impact: "volume", order: 100),
            F("rm_due", 1, "curr", tier: 4, impact: "volume", order: 400));

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal("rm_due", prompt.Data.Facts.Single(f => f.IsHeadline).FactKey);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 5, 2026-09-21] The one exception to "the backlog never leads". That
    /// tenant's Overview opened on a single lapsed licence (tier 2) while 50 overdue items carrying
    /// personal criminal liability (tier 1) went unmentioned in the lead - the reader's first
    /// sentence was the least important true thing about their estate. Liability exposure takes the
    /// lead when the period holds nothing at that tier; ordinary backlog volume still never does,
    /// which is what <see cref="AQuietWindow_LeadsWithThePeriodVolume_NotTheBacklog"/> guards.
    /// </summary>
    [Fact]
    public void AQuietPeriod_YieldsTheLeadToBacklogLiability_ButNotToBacklogVolume()
    {
        var data = Overview(
            F("lic_lapsed_last_month", 1, "prev", tier: 2, impact: "licence_continuity", headline: true, order: 100),
            F("od_total", 304, "stock", tier: 2, order: 300),
            F("od_liability", 50, "stock", tier: 1, impact: "personal_liability", order: 310));

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal("od_liability", prompt.Data.Facts.Single(f => f.IsHeadline).FactKey);
    }

    [Fact]
    public void ACurrentStateHeadline_IsKept()
    {
        var data = MonthlyExamples.Users() with
        {
            HeadlineSource = "fact",
            Candidates = [],
            Facts = [F("u_open_items_inactive_owner", 38, "stock", tier: 1, headline: true), F("t_od_total", 146, "stock", tier: 1)],
        };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal("u_open_items_inactive_owner", prompt.Data.Facts.Single(f => f.IsHeadline).FactKey);
    }

    [Fact]
    public void ABacklogFindingLoses_TheLead()
    {
        var users = MonthlyExamples.Users();
        var data = users with
        {
            HeadlineSource = "candidate",
            Candidates = [users.Candidates[1] with { DefaultSlot = 1 }],   // overdue_concentration
        };

        var prompt = FreeMonthlyDigestPrompt.Build(data);

        Assert.Equal("fact", prompt.Data.HeadlineSource);
        Assert.False(FreeMonthlyDigestPrompt.IsBacklogFact(prompt.Data.Facts.Single(f => f.IsHeadline)));
        Assert.Contains("\"Backlog\":true", prompt.UserMessage);
    }
}

public sealed class FreeMonthlyDigestValidatorTests
{
    public static TheoryData<string> SlotPrompts => new()
    {
        "06a_freetier_monthly_overview.md",
        "06b_freetier_monthly_users.md",
        "06c_freetier_monthly_location.md",
        "06d_freetier_monthly_act.md",
        "06e_freetier_monthly_licence.md",
    };

    /// <summary>
    /// [MEASURED 2026-09-20] No slot prompt may carry a worked example or a "write it as" sentence
    /// template. Both were in every prompt and both caused real defects: a draft lifted "37 laws"
    /// straight out of 06d's example input, and a draft took the deactivated-owner sentence out of
    /// 06b's template table and attached it to a merely-late person, asserting someone had left the
    /// company on a tenant whose inactive-owner count was 0. A ready-made sentence invites picking
    /// the wrong one, and a worked example's figures get copied. The prompts say what each detector
    /// FOUND and let the model write it. The next person to "improve" a prompt will reach for an
    /// example, so this test stands in the way.
    /// </summary>
    [Theory]
    [MemberData(nameof(SlotPrompts))]
    public void NoSlotPromptCarriesATemplateOrAWorkedExample(string promptFile)
    {
        var text = File.ReadAllText(Path.Combine(MonthlyExamples.PromptDirectory(), promptFile));

        // "| Write it as |" is the template table's column header - not prose like "write it as
        // past, present, future", which is direction rather than a sentence to copy.
        Assert.DoesNotContain("| Write it as", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Worked example", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\nOutput:\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live defect: the deactivated-owner consequence written about a merely-late person, on a
    /// tenant whose inactive-owner count was 0. Every figure was real, so the closed-set check
    /// passed - the fabrication was in what the sentence claimed.
    /// </summary>
    [Fact]
    public void RejectsAConsequenceWhoseEvidenceIsAbsent()
    {
        var users = MonthlyExamples.Users();
        var data = users with
        {
            // Only slippage, and nobody inactive: nothing licenses "can no longer act".
            Candidates = [users.Candidates[0] with { Detector = "last_month_slippage" }],
            Facts = [.. users.Facts.Select(f => f.FactKey.Contains("inactive_owner", StringComparison.Ordinal) ? f with { FactValue = 0 } : f)],
            HeadlineSource = "candidate",
        };
        var prompt = FreeMonthlyDigestPrompt.Build(data);

        var body = "Good morning,\n\nOf the items {{NAME_1}} had due last month, all are still open. "
                   + "The person they are assigned to can no longer act on them in RegTrack. "
                   + "That is the position as at {{AS_AT}}, and it has not moved since the start of the month.";

        var result = FreeMonthlyDigestValidator.Validate(body, prompt);

        Assert.Contains(result.FailedChecks, f => f.Contains("can no longer act") && f.Contains("deactivated owner"));
    }

    /// <summary>The same sentence is fine when the input actually contains a deactivated owner.</summary>
    [Fact]
    public void AllowsAConsequenceItsEvidenceSupports()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Users());

        var body = "Good morning,\n\n{{NAME_1}}, who is no longer an active user, still holds 38 open items. "
                   + "The person they are assigned to can no longer act on them in RegTrack.";

        Assert.DoesNotContain(
            FreeMonthlyDigestValidator.Validate(body, prompt).FailedChecks,
            f => f.Contains("can no longer act"));
    }

    /// <summary>
    /// The live defect: "192 of the 1041 overdue items across your scope" - 1041 is the finding's
    /// BaseCount, which counts that one person. Only a FactValue describes the whole scope.
    /// </summary>
    [Fact]
    public void RejectsAnEntitysCountPresentedAsTheWholeScope()
    {
        var users = MonthlyExamples.Users();
        var data = users with
        {
            Candidates = [users.Candidates[0] with { Detector = "liability_share", ItemCount = 192, BaseCount = 1041, MetricPct = 18, TenantPct = 9 }],
            HeadlineSource = "candidate",
        };
        var prompt = FreeMonthlyDigestPrompt.Build(data);

        var body = "Good morning,\n\n{{NAME_1}} holds 192 of the 1041 overdue items across your scope, "
                   + "and that share has not changed since the start of this month as at {{AS_AT}}.";

        var result = FreeMonthlyDigestValidator.Validate(body, prompt);

        Assert.Contains(result.FailedChecks, f => f.Contains("1041") && f.Contains("one entity"));
    }

    /// <summary>A scope-wide percentage is legitimate - TenantPct is measured across the scope.</summary>
    [Fact]
    public void AllowsAScopeWidePercentage()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Users());

        var body = "Good morning,\n\n{{NAME_1}} holds 51 of the 146 overdue items, 34% against 9% across your scope, "
                   + "and that is the position recorded as at {{AS_AT}} for this month.";

        Assert.DoesNotContain(
            FreeMonthlyDigestValidator.Validate(body, prompt).FailedChecks,
            f => f.Contains("one entity"));
    }

    [Fact]
    public void RejectsAnInventedNumber()
    {
        var result = Validate("Good morning,\n\n**5 items** carry personal liability. Another 13 items are open across your scope, which is the real figure here today.");

        Assert.Contains(result.FailedChecks, f => f.Contains("13"));
    }

    [Fact]
    public void RejectsAPercentageTheDataDidNotSupply()
    {
        // 24 is a real fact value (lm_still_open), but not a percentage.
        var result = Validate("Good morning,\n\n**5 items** carry personal liability, and 24% of last month is still open across your scope as of the run date.");

        Assert.Contains(result.FailedChecks, f => f.Contains("24%"));
    }

    [Fact]
    public void RejectsAPlaceholderItWasNotGiven()
    {
        var result = Validate("Good morning,\n\n**5 items** carry personal liability at {{NAME_3}}, all of them still open across your scope as at {{AS_AT}} today.");

        Assert.Contains(result.FailedChecks, f => f.Contains("{{NAME_3}}"));
    }

    /// <summary>
    /// [2026-09-23 aggregate-examples design] An example is not a finding. An email that names three
    /// examples and none of the findings it was given is still a page of counts pointing at nothing
    /// specific, and still fails the naming gate - which is the reason examples bind through {{EG_n}}
    /// rather than {{NAME_n}}.
    /// </summary>
    [Fact]
    public void ExamplesDoNotSatisfyTheNamingGate()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.LocationWithAggregateExamples());

        var result = FreeMonthlyDigestValidator.Validate(
            "Good morning,\n\nAcross your organisation, 18 of the 24 locations with overdue work hold obligations that have been overdue for more than 90 days, including {{EG_1}} and {{EG_2}}, and none of that work has been closed as at {{AS_AT}}.",
            prompt);

        Assert.Contains(result.FailedChecks, f => f.Contains("names none of the"));
        Assert.DoesNotContain(result.FailedChecks, f => f.Contains("{{EG_1}}"));
        Assert.Contains(result.Advisories, a => a.Contains("uses 2 of 3 examples") && a.Contains("{{EG_3}}"));
    }

    /// <summary>[FOUND LIVE on UAT tenant 5, 2026-09-23] "thirty other Acts ... are not named here" rejected a true email; the tens are now converted like the units.</summary>
    [Fact]
    public void ConvertsASpelledTenToDigits()
    {
        var repaired = FreeMonthlyDraftRepair.Apply("Good morning,\n\nOf the 350 obligations in the standing backlog, 50 fall under {{NAME_1}}, and thirty other Acts each hold a large part of it.");

        Assert.Contains("30 other Acts", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// [FOUND LIVE on PROD tenant 1008, 2026-09-23] "between the 1st of September and today" - the
    /// model quoted the label, "1st" was read as the number 1, and the this-month paragraph was
    /// deleted. An ordinal is a date, not a quantity.
    /// </summary>
    [Fact]
    public void AnOrdinalIsNotAFigure()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Overview());

        var review = FreeMonthlyDigestValidator.Validate(
            "Good morning,\n\nOf the 812 obligations that fell due between the 1st of {{PREV_MONTH}} and its end, 24 remain open as at {{AS_AT}} and are the work still outstanding from that month.",
            prompt);

        Assert.DoesNotContain(review.FailedChecks, f => f.Contains("the number 1"));
        Assert.Empty(FreeMonthlyDigestValidator.SentenceProblems("Of the 812 obligations that fell due between the 1st of {{PREV_MONTH}} and its end, 24 remain open.", prompt));
    }

    /// <summary>
    /// [FOUND LIVE on PROD tenant 1008, 2026-09-23] When the sentence carrying the figure is deleted,
    /// its follower - "This is broadly the same position as last month" - shipped alone, a verdict
    /// about nothing. A figure-less remainder of a paragraph something was removed from goes too.
    /// </summary>
    [Fact]
    public void DropsCommentaryWhoseFigureWasRemoved()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Overview());

        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nOf the 812 obligations that fell due in {{PREV_MONTH}}, 24 remain open.\n\n"
            + "Of the 4,444 obligations that fell due so far this month, 999 are past due. This is broadly the same position as last month rather than a change in direction.",
            prompt);

        Assert.DoesNotContain("broadly the same position", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("24 remain open", repaired.Body, StringComparison.Ordinal);
        Assert.Contains(repaired.Removed, r => r.StartsWith("orphaned commentary", StringComparison.Ordinal));
    }

    /// <summary>[FOUND LIVE on PROD tenant 1008, 2026-09-23] Trimming a repeated "as at" kept the date's comma with the date; the clause after it lost its comma.</summary>
    [Fact]
    public void KeepsTheClauseCommaWhenTrimmingARepeatedAsAt()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nOf the 1,915 obligations that fell due in {{PREV_MONTH}}, 590 remain open as at {{AS_AT}}.\n\n"
            + "The standing backlog is 17,041 obligations as at {{AS_AT}}, including 14,049 overdue for more than 90 days.");

        Assert.Contains("17,041 obligations, including 14,049", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>[FOUND LIVE on PROD tenant 1008, 2026-09-23] Two verdict sentences with no figure, beside figure sentences, restated what the figures already said.</summary>
    [Fact]
    public void RemovesAVerdictSentenceBesideAFigure_ButKeepsTheResidualAndConsequences()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nThe standing backlog is 17,041 obligations, including 14,049 overdue for more than 90 days. This is longstanding outstanding work, rather than only a recent monthly position.\n\n"
            + "At your {{NAME_1}} site, 14 of its 179 overdue obligations carry personal criminal liability. No other site is in the same situation.\n\n"
            + "There are 90 licences currently expired with no renewal in progress. Until a licence is renewed, there is no valid licence on record for that activity.");

        Assert.DoesNotContain("longstanding outstanding work", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("No other site is in the same situation.", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("no valid licence on record", repaired.Body, StringComparison.Ordinal);

        // A month token is context, not a figure: this verdict still goes.
        var withMonth = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nOf the 1,915 obligations that fell due in {{PREV_MONTH}}, 590 remain open. This is the work {{PREV_MONTH}} left outstanding, including exposure that rests with named individuals rather than only the company.");
        Assert.DoesNotContain("rests with named individuals", withMonth.Body, StringComparison.Ordinal);
    }

    /// <summary>In a sentence that opens with a number word, a small bare digit becomes a word too: "Three of the 8" reads as a misprint.</summary>
    [Fact]
    public void SpellsASmallDigitInASentenceThatOpensWithANumberWord()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nAcross your organisation, 27 of the 62 locations are above the average. Three of the 8 compliance categories are above it too, with 619 of 1,556 obligations overdue in the largest, and 12% is the rate.");

        Assert.Contains("Three of the eight compliance categories", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("619 of 1,556", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("12% is the rate", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("27 of the 62 locations", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>[OWNER, PROD tenant 1008, 2026-09-23] Beyond two full mentions, a paragraph's first mention becomes "personal liability" rather than repeating the full phrase.</summary>
    [Fact]
    public void ShortensTheLiabilityPhraseAcrossParagraphsBeyondTheAllowance()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n590 remain open, and 180 carry personal criminal liability for the responsible officer.\n\n"
            + "367 are past due, including 109 that carry personal criminal liability.\n\n"
            + "17,041 are overdue, of which 5,080 carry personal criminal liability.\n\n"
            + "1,049 fall due, including 360 that carry personal criminal liability.");

        Assert.Equal(2, Regex.Matches(repaired.Body, "personal criminal liability").Count);
        Assert.Equal(2, Regex.Matches(repaired.Body, @"carry personal liability\b").Count);
    }

    /// <summary>[OWNER, PROD tenant 1008, 2026-09-23] The officer tail is stated once; every later mention is "personal criminal liability" alone.</summary>
    [Fact]
    public void StatesTheOfficerTailOnce()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nOf the 1,915 obligations that fell due in {{PREV_MONTH}}, 590 remain open and 180 carry personal criminal liability for the responsible officer.\n\n"
            + "Of the 1,005 obligations due so far in {{CURR_MONTH}}, 367 are past due, including 109 carrying personal criminal liability for the responsible officer.\n\n"
            + "Before {{CURR_MONTH}} ends, 1,049 obligations fall due, including 360 carrying personal criminal liability for the responsible officers.");

        Assert.Equal(1, Regex.Matches(repaired.Body, "for the responsible officer").Count);
        Assert.Equal(2, Regex.Matches(repaired.Body, "personal criminal liability").Count);
        Assert.Contains("360 carrying personal liability", repaired.Body, StringComparison.Ordinal);

        // The normalizer bolds the phrase before the repair sees it; the tail still goes and the bold stays.
        var bolded = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n**180** carry **personal criminal liability** for the responsible officer.\n\n"
            + "**109** carry **personal criminal liability** for the responsible officer.");
        Assert.Equal(1, Regex.Matches(bolded.Body, "for the responsible officer").Count);
        Assert.Contains("**109** carry **personal criminal liability**.", bolded.Body, StringComparison.Ordinal);
    }

    /// <summary>[FOUND LIVE on PROD tenant 1008, 2026-09-23] "overall," is a recap only when it opens the sentence; mid-sentence it is the comparison's own wording.</summary>
    [Fact]
    public void RemovesARecapOnlyWhenItOpensTheSentence()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nAcross your organisation, 27 of the 62 locations have a higher overdue rate than the organisation overall, including your {{EG_1}} site. Overall, the position is serious.");

        Assert.Contains("than the organisation overall, including your {{EG_1}} site.", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Overall, the position", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>A figure-less paragraph the model wrote whole (a quiet month) is left as written.</summary>
    [Fact]
    public void KeepsAFigurelessParagraphNothingWasRemovedFrom()
    {
        var repaired = FreeMonthlyDraftRepair.Apply("Good morning,\n\nNo licences are tracked in RegTrack for them. Any recorded there will appear in this email when they come up for renewal.");

        Assert.Contains("No licences are tracked", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>[FOUND LIVE on PROD tenant 1008, 2026-09-23] A sentence never opens with a digit: the opening word stays spelled, and the validator checks it by value.</summary>
    [Fact]
    public void KeepsASentenceOpeningNumberWord_AndTheValidatorChecksItsValue()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Location());   // loc_single_performer = 3 is in the closed set
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nAt your {{NAME_1}} site, 13 of the 48 obligations that fell due in {{PREV_MONTH}} remain open. Three locations depend on one person for all their open work, and 4 of them hold most of it.",
            prompt);

        Assert.Contains("Three locations depend", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(FreeMonthlyDigestValidator.Validate(repaired.Body, prompt).FailedChecks, f => f.Contains("Three"));

        // "Three of the four locations" keeps BOTH words - "Three of the 4" is worse than either - and checks both by value.
        var mixed = FreeMonthlyDraftRepair.Apply("Good morning,\n\nAt your {{NAME_1}} site, 13 of the 48 obligations that fell due in {{PREV_MONTH}} remain open. Three of the fifteen locations with overdue work depend on one person for all of it.", prompt);
        Assert.Contains("Three of the fifteen locations", mixed.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(FreeMonthlyDigestValidator.Validate(mixed.Body, prompt).FailedChecks, f => f.Contains("spells the number"));

        var rejected = FreeMonthlyDigestValidator.Validate("Good morning,\n\nAt your {{NAME_1}} site, 13 of the 48 obligations that fell due in {{PREV_MONTH}} remain open. Seventeen locations depend on one person for all their open work today.", prompt);
        Assert.Contains(rejected.FailedChecks, f => f.Contains("Seventeen"));
    }

    /// <summary>
    /// [FOUND LIVE on PROD tenant 1008, 2026-09-23] The model wrote the not_assessable count (12
    /// licences with no end date) exactly as told; it was in no fact, so the typo-corrector rewrote
    /// it to 62. A number handed to the model as data is in the closed set, and a two-digit figure
    /// is never "corrected".
    /// </summary>
    [Fact]
    public void NeverRewritesAShortFigure_AndAllowsTheNotAssessableCount()
    {
        var overview = MonthlyExamples.Overview() with
        {
            DataQuality = [new MonthlyDataQuality("licences_without_end_date", 12, "Licences with no EndDate cannot be assessed for lapse.")],
        };
        var prompt = FreeMonthlyDigestPrompt.Build(overview);

        Assert.Contains(12, prompt.AllowedNumbers);

        // 62 is not in this fixture's data; 12 is one digit away and IS. A short figure is never rewritten.
        var repaired = FreeMonthlyDraftRepair.Apply("Good morning,\n\nOf the 812 obligations that fell due in {{PREV_MONTH}}, 24 remain open. These figures exclude 62 licences with no end date.", prompt);
        Assert.DoesNotContain("12 licences", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(repaired.Removed, r => r.Contains("corrected mistyped figure"));
    }

    [Fact]
    public void AdvisesOnARepeatedNamePlaceholder()
    {
        var result = Validate("Good morning,\n\n**5 items** carry personal liability at {{NAME_2}}. Those items at {{NAME_2}} are all still open across your scope today.");

        Assert.Contains(result.Advisories, f => f.Contains("{{NAME_2}}"));
    }

    [Fact]
    public void RejectsANameTheModelWroteItself()
    {
        var result = Validate("Good morning,\n\n**5 items** carry personal liability, most of them at the Pune branch, all still open across your scope today as it stands.");

        Assert.Contains(result.FailedChecks, f => f.Contains("'Pune'"));
    }

    [Fact]
    public void RejectsAMonthNameInsteadOfTheToken()
    {
        var result = Validate("Good morning,\n\n**5 items** from last month carry personal liability, and all of them fell due in September across your scope.");

        Assert.Contains(result.FailedChecks, f => f.Contains("'September'"));
    }

    [Fact]
    public void RejectsCausalLanguage()
    {
        var result = Validate("Good morning,\n\n**5 items** carry personal liability because the owners are not acting on them across your scope as at {{AS_AT}}.");

        Assert.Contains(result.FailedChecks, f => f.Contains("because"));
    }

    [Fact]
    public void AdvisesWhenTheLeadIsNotTheHeadline()
    {
        var result = Validate("Good morning,\n\n**146 items** are overdue across your scope today.\n\n5 items from last month carry personal liability and are still open as at {{AS_AT}}.");

        Assert.Contains(result.Advisories, f => f.Contains("headline"));
    }

    [Fact]
    public void AdvisesWhenTheLeadBoldsARealButWrongFigure()
    {
        // The first UAT preview: 24 is a real fact, the headline (5) is in the paragraph, but the
        // bolded claim is the wrong number.
        var result = Validate("Good morning,\n\n**24 items that carry personal liability are still open**, and 5 of them fell due last month across your scope as at {{AS_AT}}.");

        Assert.Contains(result.Advisories, f => f.Contains("not the headline figure"));
    }

    [Fact]
    /// <summary>
    /// [UPDATED 2026-09-23] Three spans per paragraph are now the shape - the lead figure and up
    /// to two meaning phrases, all added in code. Four is a shout, and that is what this guards.
    /// </summary>
    public void RejectsFourBoldSpansInOneParagraph()
    {
        var result = Validate("Good morning,\n\n**5 items** carry **personal criminal liability**, and **146 items** are **overdue** across your scope today as at {{AS_AT}}.");

        Assert.Contains(result.FailedChecks, f => f.Contains("bolds more than"));
    }

    /// <summary>A figure and its impact together is the intended shape, and must NOT fail.</summary>
    [Fact]
    public void AllowsAFigureAndItsImpactBoldedTogether()
    {
        var result = Validate("Good morning,\n\n**5 items** carry **personal criminal liability** as at {{AS_AT}} across your scope today.");

        Assert.DoesNotContain(result.FailedChecks, f => f.Contains("bolds more than"));
    }

    private static FreeMonthlyReview Validate(string body) =>
        FreeMonthlyDigestValidator.Validate(body, FreeMonthlyDigestPrompt.Build(MonthlyExamples.Overview()));
}

/// <summary>
/// Repair removes sentences that comment on the figures. [MEASURED 2026-09-21] 18 of 19 rejection
/// reasons across ten attempts were this kind of thing, and rejecting on them sent the reader the
/// deterministic fallback instead of a true email.
/// </summary>
public sealed class FreeMonthlyParagraphOrderTests
{
    /// <summary>
    /// [OWNER, 2026-09-24, PROD tenant 1008] The Location email introduced the backlog in
    /// paragraph 2 and came back to it in paragraph 5. Same period, adjacent paragraphs; the
    /// lead stays first; the model's order inside a period is kept.
    /// </summary>
    [Fact]
    public void GroupsParagraphsByPeriod_KeepingTheLeadAndTheOrderWithinAPeriod()
    {
        const string lead = "Across your organisation, 50 locations have overdue obligations carrying personal criminal liability.";
        const string backlog1 = "The standing backlog is 17,308 overdue obligations, of which 14,083 have been overdue for more than 90 days.";
        const string past = "As at {{AS_AT}}, 51 locations still have obligations open from {{PREV_MONTH}}, including {{NAME_1}} with 72 of its 77.";
        const string present = "Of the 1,005 obligations that fell due between the 1st of {{CURR_MONTH}} and today, 367 are already past their due date.";
        const string backlog2 = "Of the 17,308 overdue obligations in the backlog, 2,140 sit at your {{NAME_2}} site.";
        const string future = "Before {{CURR_MONTH}} ends, 1,049 obligations fall due, including 360 carrying personal liability.";

        var arranged = FreeMonthlyParagraphOrder.Arrange($"Good morning,\n\n{lead}\n\n{backlog1}\n\n{past}\n\n{future}\n\n{present}\n\n{backlog2}");

        Assert.Equal(new[] { "Good morning,", lead, past, present, backlog1, backlog2, future }, arranged.Split("\n\n"));
    }

    /// <summary>A paragraph naming no period explains the one before it and travels with it.</summary>
    [Fact]
    public void AParagraphWithoutAPeriodStaysWithThePreviousOne()
    {
        const string lead = "Of the 1,915 obligations that fell due in {{PREV_MONTH}}, 590 remain open.";
        const string stock = "The standing backlog is 17,041 obligations.";
        const string explains = "Until a licence is renewed, there is no valid licence on record for that activity.";
        const string past = "Of the people with obligations due in {{PREV_MONTH}}, 21 still have work open.";

        var arranged = FreeMonthlyParagraphOrder.Arrange($"Good morning,\n\n{lead}\n\n{stock}\n\n{explains}\n\n{past}");

        Assert.Equal(new[] { "Good morning,", lead, past, stock, explains }, arranged.Split("\n\n"));
    }

    [Fact]
    public void LeavesAShortEmailAlone()
    {
        const string body = "Good morning,\n\nNo licences are tracked in RegTrack for you.\n\nAny recorded there will appear here when they come up for renewal.";
        Assert.Equal(body, FreeMonthlyParagraphOrder.Arrange(body));
    }
}

public sealed class FreeMonthlyDraftRepairTests
{
    /// <summary>[FOUND LIVE on PROD tenant 1008, 2026-09-24] "At your {{NAME_1}}, all 39 obligations remain open" - and NAME_1 was an Act.</summary>
    [Fact]
    public void SaysUnderNotAtYourForAnAct()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Act());
        var act = prompt.NamedFindings.First(n => string.Equals(n.Candidate.EntityKind, "act", StringComparison.OrdinalIgnoreCase)).NamePlaceholder!;

        var repaired = FreeMonthlyDraftRepair.Apply($"Good morning,\n\nAt your {act}, the obligations that fell due in {{{{PREV_MONTH}}}} remain open.", prompt);

        Assert.Contains($"Under {act}", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("At your", repaired.Body, StringComparison.Ordinal);

        // A site keeps "at your": the preposition changes only for a placeholder that names an Act.
        var site = FreeMonthlyDraftRepair.Apply("Good morning,\n\nAt your {{NAME_1}} site, the obligations that fell due in {{PREV_MONTH}} remain open.");
        Assert.Contains("At your {{NAME_1}} site", site.Body, StringComparison.Ordinal);
    }

    /// <summary>[FOUND LIVE on PROD tenant 1008, 2026-09-24] "one of 18 people sharing this concentration, with 17 others".</summary>
    [Fact]
    public void SaysTheResidualOnce()
    {
        var repaired = FreeMonthlyDraftRepair.Apply("Good morning,\n\n{{NAME_1}} holds 2,034 of the 17,155 overdue obligations and is one of 18 people who each hold a large part of the backlog, with 17 others in the same position.");

        Assert.Contains("one of 18 people who each hold a large part of the backlog.", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("17 others", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>[FOUND LIVE on PROD tenant 1008, 2026-09-24] "Across your organisation,, 125 licences" reached the final body.</summary>
    [Fact]
    public void CollapsesADoubledComma()
    {
        var repaired = FreeMonthlyDraftRepair.Apply("Good morning,\n\nAcross your organisation,, 125 licences are currently expired, and 90 have no renewal in progress.");

        Assert.Contains("Across your organisation, 125 licences", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(",,", repaired.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovesTheCommentingSentenceAndKeepsTheFacts()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n"
            + "5 items from last month carry personal criminal liability. This indicates a broader process issue. "
            + "24 of the 812 items that fell due are still open.\n\n"
            + "Please review these findings and take action.");

        Assert.DoesNotContain("This indicates", repaired.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Please review", repaired.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5 items from last month carry personal criminal liability.", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("24 of the 812 items that fell due are still open.", repaired.Body, StringComparison.Ordinal);
        Assert.Equal(2, repaired.Removed.Count);
    }

    /// <summary>
    /// [2026-09-23 aggregate-examples design, Sec.3.5] An example is stated once, inside its
    /// finding's sentence. A second sentence about it is the example becoming a finding in prose.
    /// </summary>
    [Fact]
    public void RemovesASecondSentenceAboutAnExample()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n"
            + "Across your organisation, 18 of the 24 locations with overdue work hold obligations overdue for more than 90 days, including {{EG_1}} and {{EG_2}}. "
            + "{{EG_1}} holds 210 of its 300 overdue obligations beyond that point. "
            + "At your {{NAME_1}} site, 13 of the 48 obligations that fell due last month remain open.");

        Assert.Contains("including {{EG_1}} and {{EG_2}}.", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("holds 210 of its 300", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("{{NAME_1}}", repaired.Body, StringComparison.Ordinal);
        Assert.Contains(repaired.Removed, r => r.Contains("{{EG_1}}"));
    }

    [Fact]
    public void KeepsTheGreetingAndDropsAnEmptiedParagraph()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n5 items are open.\n\nThis indicates a wider problem.");

        Assert.StartsWith("Good morning,", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("5 items are open.", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("wider problem", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// [FOUND LIVE 2026-09-21] The comparison survives; only the model's own inference goes. Cutting
    /// the whole sentence deleted a computed comparison - the most useful line in that email, and
    /// the only place its finding was named. Explanation grounded in the data must reach the reader.
    /// </summary>
    [Fact]
    public void KeepsTheComparisonAndCutsOnlyTheInference()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n"
            + "This represents 49% of overdue items at {{NAME_1}}, which is significantly higher "
            + "than the average of 9% across your organisation, indicating a local issue.");

        Assert.Contains("49% of overdue items at {{NAME_1}}", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("9% across your organisation", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("indicating", repaired.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("significantly", repaired.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The procs' own labels say "disproportionate" and "unusually high" - the data's words stay.</summary>
    [Fact]
    public void KeepsWordingThatComesFromTheProcsOwnLabels()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n2 people each hold a disproportionate share of all overdue work, "
            + "and 3 locations have an unusually high share of expired licences.");

        Assert.Contains("disproportionate share", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("unusually high share", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A spelled figure is converted to digits, not deleted: the closed-set check only sees digits,
    /// so "Nine locations" would otherwise bypass the guarantee that every figure came from SQL.
    /// Converting makes it checkable - and it still rejects afterwards if the value was invented.
    /// </summary>
    [Fact]
    public void ConvertsASpelledFigureToDigitsSoItCanBeChecked()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nAcross your organisation, nine locations hold at least one expired licence with no renewal filed.");

        Assert.Contains("9 locations", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("nine", repaired.Body, StringComparison.OrdinalIgnoreCase);

        // And the converted figure is then subject to the closed set like any other number.
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Overview());
        var review = FreeMonthlyDigestValidator.Validate(
            FreeMonthlyDraftRepair.Apply("Good morning,\n\nAcross your organisation, seventeen locations hold an expired licence with no renewal filed at all today.").Body,
            prompt);

        Assert.Contains(review.FailedChecks, f => f.Contains("17"));

        // [2026-09-23] A sentence-opening number word stays a word (a sentence never opens with a
        // digit) and is checked by its value instead - see KeepsASentenceOpeningNumberWord.
    }

    /// <summary>
    /// [DIAGNOSED 2026-09-21] Fabrication here is transcription, not reasoning: the model wrote
    /// 2,709 where the fact says 3709. Deleting that sentence would throw away the email's most
    /// substantial line over one character, so a single-digit slip with exactly one candidate of
    /// the same length is corrected instead.
    /// </summary>
    [Fact]
    public void CorrectsASingleDigitSlipInsteadOfDeletingTheSentence()
    {
        var overview = MonthlyExamples.Overview();
        var data = overview with { Facts = [.. overview.Facts, MonthlyExamples.Fact("od_over_90_days", 3709) with { SeverityTier = 2, WindowScope = "stock" }] };
        var prompt = FreeMonthlyDigestPrompt.Build(data);

        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nAs at {{AS_AT}}, 2709 items have been overdue for more than 90 days across your organisation.", prompt);

        Assert.Contains("3709 items", repaired.Body, StringComparison.Ordinal);
        Assert.Contains(repaired.Removed, r => r.Contains("corrected mistyped figure"));
    }

    /// <summary>
    /// [FOUND LIVE on tenant 5, 2026-09-21] The model paraphrased "more than 90 days" as "over
    /// three months". No fact carries 3, so the truth check was right - but deleting the whole
    /// sentence left the paragraph as the bare stub "304 obligations are overdue today." The
    /// untruth was confined to a trailing clause, so the clause goes and the grounded half stays.
    /// </summary>
    [Fact]
    public void TrimsAnUnsupportedTailInsteadOfDeletingTheGroundedSentence()
    {
        var overview = MonthlyExamples.Overview();
        var data = overview with { Facts = [.. overview.Facts, MonthlyExamples.Fact("od_over_90_days", 302) with { WindowScope = "stock" }] };
        var prompt = FreeMonthlyDigestPrompt.Build(data);

        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nOf those, 302 have been overdue for more than 90 days, so nearly all of the backlog has been carried for over three months.",
            prompt);

        Assert.Contains("302 have been overdue for more than 90 days", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("three months", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sentence whose OPENING claim is untrue cannot be rescued by trimming - trimming would
    /// leave the fabrication and cut the context. It is still deleted outright.
    /// </summary>
    [Fact]
    public void DeletesRatherThanTrimsWhenTheFirstClauseIsTheFabrication()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Overview());

        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n91746 items are overdue, and that is the position today.", prompt);

        Assert.DoesNotContain("91746", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>

    /// <summary>
    /// [FOUND LIVE on tenant 1082, 2026-09-22] The Overview opened two paragraphs with the same
    /// date. The rule said "once", which the model read as once per paragraph. The qualifier now
    /// appears once per EMAIL; the figures and sentences carrying it are untouched.
    /// </summary>
    [Fact]
    public void StatesTheAsAtDateOnceInTheWholeEmail()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nAs at {{AS_AT}}, 12 obligations carry personal criminal liability.\n\n"
            + "As at {{AS_AT}}, 47 of the 194 obligations that fell due remain open.");

        Assert.Equal(1, Regex.Matches(repaired.Body, @"\{\{AS_AT\}\}").Count);
        Assert.Contains("12 obligations carry personal criminal liability.", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("47 of the 194 obligations that fell due remain open.", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// [THE RECURRING DEFECT] "Items" names nothing a reader recognises, and it has now been fixed
    /// twice by hand - once for the <c>od_*</c> labels, then again when the Act email said "189
    /// items" from its own <c>law_*</c> labels. Fixing a word where it was noticed is how it keeps
    /// coming back, so this checks every label of every slot instead.
    /// </summary>
    [Theory]
    [InlineData(MonthlyDigestSlot.Overview)]
    [InlineData(MonthlyDigestSlot.Users)]
    [InlineData(MonthlyDigestSlot.Location)]
    [InlineData(MonthlyDigestSlot.Act)]
    [InlineData(MonthlyDigestSlot.Licence)]
    public void NoLabelSentToTheModelSaysItem(MonthlyDigestSlot slot)
    {
        var data = MonthlyExamples.ByName(slot.ToString().ToLowerInvariant());
        if (slot == MonthlyDigestSlot.Location)
            data = MonthlyExamples.LocationWithAggregateExamples();   // so an example's unit label is covered too
        var prompt = FreeMonthlyDigestPrompt.Build(data);

        // The LABELS only - "ItemCount" is a field name the prompts describe, not prose the model copies.
        // [2026-09-23] An example's "Counts" label is prose the model copies as well, so it is held to the same rule.
        var labels = Regex.Matches(prompt.UserMessage, @"""(DisplayLabel|Counts)"":""(?<text>[^""]*)""")
            .Select(m => m.Groups["text"].Value)
            .ToList();

        Assert.NotEmpty(labels);
        Assert.DoesNotContain(labels, l => Regex.IsMatch(l, @"\bitems?\b", RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// [FOUND LIVE on tenant 1082, 2026-09-22] "Sexual Harassment of Women at Workplace
    /// (Prevention, Prohibition &amp; Redressal) Act, 2013 &amp; ... Rules 2013" reached a customer
    /// email as "Sexual Harassment of Women at Workplace (Prevention, Prohibition" - cut at the
    /// "&amp;" INSIDE its own title, leaving an unclosed bracket. A joiner between two statutes only
    /// occurs where the brackets are balanced.
    /// </summary>
    [Fact]
    public void KeepsAStatuteNameWhoseOwnTitleContainsAJoiner()
    {
        const string name = "Sexual Harassment of Women at Workplace (Prevention, Prohibition & Redressal) Act, 2013 "
                            + "& Sexual Harassment of Women at Workplace (Prevention, Prohibition & Redressal) Rules 2013";

        var clean = FreeMonthlyDigestPrompt.CleanLabel(name);

        Assert.Equal("Sexual Harassment of Women at Workplace (Prevention, Prohibition & Redressal) Act, 2013", clean);
        Assert.Equal(clean.Count(c => c == '('), clean.Count(c => c == ')'));
    }

    /// <summary>The ordinary case still shortens - one statute plus its Rules keeps only the statute.</summary>
    [Fact]
    public void StillShortensAStatuteJoinedToItsRules()
    {
        var clean = FreeMonthlyDigestPrompt.CleanLabel("Minimum Wages Act, 1948 and Minimum Wages Gujarat Rules, 1961");

        Assert.Equal("Minimum Wages Act, 1948", clean);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 1082, 2026-09-22] The stammer guard stripped the scope from a
    /// comparison, leaving "35%, compared with 21%." - 21% of what? A phrase completing a
    /// comparison is the second half of the claim, never a refrain.
    /// </summary>
    [Fact]
    public void KeepsTheScopeThatCompletesAComparison()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n5 sites hold overdue work across your organisation.\n\n"
            + "412 overdue obligations sit at one site across your organisation.\n\n"
            + "That site runs at 35%, compared with 21% across your organisation.");

        Assert.Contains("compared with 21% across your organisation", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 1082, 2026-09-22] "24 Acts have overdue obligations carrying personal
    /// criminal liability... A further 22 Acts are overdue across an unusually large share of their
    /// locations." Those 22 are the same Acts counted a second way, so the reader was told there
    /// were 46. The connective asserts an addition nobody computed; removing it cannot make a true
    /// sentence false.
    /// </summary>
    [Theory]
    [InlineData("A further 22 Acts are overdue at many sites.", "22 Acts are overdue at many sites.")]
    [InlineData("Another 144 obligations have no reviewer.", "144 obligations have no reviewer.")]
    [InlineData("In addition, 5 locations cannot be assessed.", "5 locations cannot be assessed.")]
    public void RemovesAConnectiveThatAddsTwoCountsTogether(string written, string expected)
    {
        var repaired = FreeMonthlyDraftRepair.Apply($"Good morning,\n\n{written}");

        Assert.Contains(expected, repaired.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a further", repaired.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("another", repaired.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 1082, 2026-09-22] "17 Acts still have obligations open from August,
    /// out of 9 Acts with last-month work." Both figures were real, from different populations, and
    /// joining them produced a fraction that cannot exist. Every value passed the closed-set check,
    /// so only a check on the RELATIONSHIP catches it.
    /// </summary>
    [Fact]
    public void RejectsAPartLargerThanItsWhole()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Overview());

        var problems = FreeMonthlyDigestValidator.SentenceProblems("17 Acts remain open, out of 9 Acts compared.", prompt);

        Assert.Contains(problems, p => p.Contains("a part cannot be larger"));
    }

    /// <summary>An ordinary "N of M" where the part fits inside the whole is untouched.</summary>
    [Fact]
    public void AcceptsAPartThatFitsInsideItsWhole()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Overview());

        var problems = FreeMonthlyDigestValidator.SentenceProblems("5 of the 24 locations are affected.", prompt);

        Assert.DoesNotContain(problems, p => p.Contains("a part cannot be larger"));
    }

    /// <summary>
    /// [FOUND LIVE on tenant 1082, 2026-09-22] The inference trim cut ", showing an unusually large
    /// share still open" and left "...with 4 of the 11 people who had work due." - a clause whose
    /// verb had been removed. A participle carrying a "with ..." clause is never trimmed.
    /// </summary>
    [Fact]
    public void NeverTrimsAParticipleThatIsAClausesOnlyVerb()
    {
        const string sentence = "47 of last month's obligations remain open, with 4 of the 11 people "
                                + "who had work due showing an unusually large share still open.";

        var repaired = FreeMonthlyDraftRepair.Apply($"Good morning,\n\n{sentence}");

        Assert.Contains("showing an unusually large share still open", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 1082, 2026-09-22] "personal criminal liability" appeared four times in
    /// one 120-word Overview. It is the most serious thing the email says, and repeating it turns
    /// it into wallpaper. Shortened on the third mention, never deleted - the claim is real.
    /// </summary>
    /// <para>[CHANGED 2026-09-23] ...within a paragraph that has already spelled it out. The live
    /// result of the email-wide rule was a last paragraph reading "13 obligations carry that
    /// liability" with no earlier mention in that paragraph for "that" to point at. A paragraph's
    /// first mention is now always the full phrase; only a repeat inside the same paragraph is
    /// shortened, once the email-wide count has been reached.</para>
    [Fact]
    public void ShortensAHeavyPhraseAfterTwoMentions_OnlyAfterAFullMentionInTheSameParagraph()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n12 obligations carry personal criminal liability.\n\n"
            + "587 overdue obligations carry personal criminal liability.\n\n"
            + "13 obligations due this month carry personal criminal liability. Of those, 5 at one site carry personal criminal liability.");

        // [2026-09-23] Two full mentions, then the standalone short form: a third paragraph's first
        // mention becomes "personal liability", and the repeat after it follows suit.
        Assert.Equal(2, Regex.Matches(repaired.Body, "personal criminal liability", RegexOptions.IgnoreCase).Count);
        Assert.Contains("13 obligations due this month carry personal liability", repaired.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5 at one site carry personal liability", repaired.Body, StringComparison.OrdinalIgnoreCase);

        foreach (var figure in new[] { "12", "587", "13", "5" })
            Assert.Contains(figure, repaired.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverOpensAParagraphWithTheShortForm()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n12 obligations carry personal criminal liability.\n\n"
            + "587 overdue obligations carry personal criminal liability.\n\n"
            + "13 obligations due this month carry personal criminal liability.");

        Assert.DoesNotContain("that liability", repaired.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Regex.Matches(repaired.Body, "personal criminal liability", RegexOptions.IgnoreCase).Count);
        Assert.Contains("13 obligations due this month carry personal liability", repaired.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The proc's comparison wording is shortened the same way, and stays grammatical.</summary>
    [Fact]
    public void ShortensTheRepeatedComparisonWordingGrammatically()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n4 people hold an unusually large share of the work.\n\n"
            + "6 sites hold an unusually large share of the work.\n\n"
            + "3 Acts hold an unusually large share of the work.");

        Assert.Contains("a larger share than most", repaired.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("an larger", repaired.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// [COST TWO EMAILS, 2026-09-22] Banning "in that position" deleted the residual line - the one
    /// sentence carrying {{NAME_1}} - so the draft named none of its findings, failed validation,
    /// and shipped the deterministic fallback instead. A phrase that appears in a sentence the
    /// prompts REQUIRE can never go on the delete list.
    /// </summary>
    [Fact]
    public void KeepsTheResidualLineThatNamesTheFinding()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n{{NAME_1}} expired without a renewal filed; it is 1 of 3 licences in that position.");

        Assert.Contains("{{NAME_1}}", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("1 of 3 licences", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>"1 of 1 licences" says nothing - with or without a determiner.</summary>
    [Theory]
    [InlineData("1 of 1 licences has expired.", "the only licences has expired.")]
    [InlineData("2 of the 2 licences expire.", "both licences expire.")]
    [InlineData("3 of its 3 sites are affected.", "all 3 sites are affected.")]
    public void RewritesAPartThatEqualsItsWhole(string written, string expected)
    {
        var repaired = FreeMonthlyDraftRepair.Apply($"Good morning,\n\n{written}");

        Assert.Contains(expected, repaired.Body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A number that is not a near-miss is invented, and its sentence goes - nothing false ships.</summary>
    [Fact]
    public void DeletesTheSentenceWhenANumberIsTrulyInvented()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Overview());

        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n5 items carry personal criminal liability. A further 91746 items sit somewhere else entirely.", prompt);

        Assert.DoesNotContain("91746", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("5 items carry personal criminal liability.", repaired.Body, StringComparison.Ordinal);
        Assert.Contains(repaired.Removed, r => r.Contains("UNSUPPORTED"));
    }

    /// <summary>
    /// [FOUND LIVE on tenant 5, 2026-09-21] Each of these reached a real email. "Overall," was
    /// never matched at all: \b cannot match before a comma, so the phrase test silently never
    /// fired. A stray leading comma survived a clause cut. "than typically seen" asserts a norm
    /// nothing computed.
    /// </summary>
    [Theory]
    [InlineData("Overall, the standing position shows areas requiring attention.")]
    [InlineData("This is a higher share than typically seen across the sector.")]
    [InlineData("This could impact operational continuity if not addressed.")]
    [InlineData("The issues may not be evenly distributed across your sites.")]
    public void RemovesSentencesThatStateNoFact(string sentence)
    {
        var repaired = FreeMonthlyDraftRepair.Apply($"Good morning,\n\n47 licences are expired with no renewal in progress. {sentence}");

        Assert.Contains("47 licences are expired", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(sentence, repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 5] A law's master-data name can be two statutes joined: the Act email
    /// carried a 140-character name twice in a 200-word email. Keeping the first statute leaves a
    /// real, searchable name rather than a truncation ending mid-word.
    /// </summary>
    [Fact]
    public void ShortensALawNameThatIsTwoStatutesJoined()
    {
        var clean = FreeMonthlyDigestPrompt.CleanLabel(
            "Maharashtra Shops and Establishments (Regulation of Employment and Conditions of Service) Act, 2017 "
            + "and Maharashtra Shops and Establishments (Regulation of Employment and Conditions of Service) Rules, 2018");

        Assert.Equal("Maharashtra Shops and Establishments (Regulation of Employment and Conditions of Service) Act, 2017", clean);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 1355, 2026-09-21] Master data holds some names entirely lower case
    /// ("amruta nangal"), and they were reaching a management inbox that way.
    /// </summary>
    [Theory]
    [InlineData("amruta nangal", "Amruta Nangal")]
    [InlineData("adi sangli", "Adi Sangli")]
    [InlineData("vidhya gaikwad", "Vidhya Gaikwad")]
    // Anything already carrying a capital is a deliberate spelling and is never touched - blind
    // title-casing would give "Api Unit-2" and capitalise the "of"/"and" inside a statute's name.
    [InlineData("API Unit-2, Atchutapuram", "API Unit-2, Atchutapuram")]
    [InlineData("BITA Consulting Assam", "BITA Consulting Assam")]
    [InlineData("Maharashtra Shops and Establishments (Regulation of Employment) Act, 2017",
                "Maharashtra Shops and Establishments (Regulation of Employment) Act, 2017")]
    public void CapitalisesALowerCaseName_ButNeverRecasesOne(string stored, string expected) =>
        Assert.Equal(expected, FreeMonthlyDigestPrompt.CleanLabel(stored));

    /// <summary>
    /// [FOUND LIVE on UAT tenant 1285, 2026-09-23] The join sits at character 19 and the old guard
    /// required more than 20, so the Act reached the email as its full two-statute name.
    /// </summary>
    [Fact]
    public void CutsAShortStatuteNameAtItsAmpersandJoin()
    {
        var clean = FreeMonthlyDigestPrompt.CleanLabel("Companies Act, 2013 & Companies (Management and Administration) Rules, 2014");

        Assert.Equal("Companies Act, 2013", clean);
    }

    /// <summary>A joiner inside a short title is part of the title, not a join.</summary>
    [Fact]
    public void KeepsAnAmpersandInsideAShortTitle()
    {
        Assert.Equal("Health & Safety Act, 1974", FreeMonthlyDigestPrompt.CleanLabel("Health & Safety Act, 1974"));
    }

    /// <summary>A single statute, however long, is left exactly as the master data has it.</summary>
    [Fact]
    public void LeavesASingleStatuteNameAlone()
    {
        const string name = "Employees State Insurance Act, 1948";

        Assert.Equal(name, FreeMonthlyDigestPrompt.CleanLabel(name));
    }

    /// <summary>
    /// [FOUND LIVE 2026-09-21] "This is a standing position, not last month's slip" appeared in 8
    /// of 10 emails, and the prosecution consequence twice inside ONE email. A senior reader
    /// notices a repeated line immediately, and it is padding wearing the clothes of a finding.
    /// </summary>
    [Fact]
    public void RemovesASentenceThatRepeatsAnEarlierPoint()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n3709 of 3830 items have been overdue for more than 90 days.\n\n"
            + "302 of 304 items have been overdue for more than 90 days.");

        Assert.Contains("3709 of 3830", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("302 of 304", repaired.Body, StringComparison.Ordinal);
        Assert.Contains(repaired.Removed, r => r.Contains("repeats an earlier sentence"));
    }

    /// <summary>An approved consequence is a point, not a refrain: once per email.</summary>
    [Fact]
    public void AllowsAnApprovedConsequenceOnceAndCutsTheSecond()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n47 licences are expired. Until a licence is renewed, there is no valid licence on record for that activity.\n\n"
            + "2 more expire before month end. Until a licence is renewed, there is no valid licence on record for that activity.");

        Assert.Equal(1, Regex.Matches(repaired.Body, "no valid licence on record", RegexOptions.IgnoreCase).Count);
        Assert.Contains("2 more expire before month end.", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// [FOUND LIVE 2026-09-21] A DIFFERENT consequence is still allowed in the same email. Counting
    /// all consequences against one allowance deleted a licence's "no valid licence on record"
    /// because a liability sentence had already used it up - two real points, one of them lost.
    /// </summary>
    [Fact]
    public void KeepsASecondConsequenceWhenItIsADifferentOne()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n6 obligations rest on one person. If that person is unavailable, no one else is assigned to that work in RegTrack.\n\n"
            + "47 licences are expired. Until a licence is renewed, there is no valid licence on record for that activity.");

        Assert.Contains("if that person is unavailable", repaired.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no valid licence on record", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 23, 2026-09-21] "1,017 open items have no one assigned to do them. No
    /// one is assigned to these in RegTrack." - the second sentence restates the first and carries
    /// no figure. The single-point-of-failure line reads similarly but says something different
    /// (nobody ELSE is assigned to THAT work) and must survive.
    /// </summary>
    [Fact]
    public void RemovesAConsequenceThatOnlyRestatesItsFact_ButKeepsTheSimilarSpofLine()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\n1017 open items have no one assigned to do them. No one is assigned to these in RegTrack.\n\n"
            + "11 open items at one site sit with a single person. If that person is unavailable, no one else is assigned to that work in RegTrack.");

        Assert.Contains("1017 open items have no one assigned to do them.", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("No one is assigned to these in RegTrack.", repaired.Body, StringComparison.Ordinal);
        Assert.Contains("no one else is assigned to that work in RegTrack.", repaired.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotLeaveAStrandedCommaAfterACut()
    {
        var repaired = FreeMonthlyDraftRepair.Apply(
            "Good morning,\n\nWhich means little here, Adinath holds 147 of these overdue items.");

        Assert.DoesNotContain(", Adinath", repaired.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", repaired.Body, StringComparison.Ordinal);
    }

    /// <summary>Repair only ever deletes or corrects a figure - it must never add a new claim.</summary>
    [Fact]
    public void NeverAddsText()
    {
        const string body = "Good morning,\n\n5 items are open as at {{AS_AT}}. Because of this, 24 remain.";

        var repaired = FreeMonthlyDraftRepair.Apply(body);

        foreach (var word in repaired.Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(word, body, StringComparison.Ordinal);
    }
}

/// <summary>
/// Over-bolding is cosmetic. The first real preview (tenant 1285) had 13 of 15 drafts rejected for
/// it alone, with every figure correct - so it is normalised, not rejected.
/// </summary>
public sealed class FreeMonthlyDraftNormalizerTests
{
    /// <summary>
    /// [ROOT CAUSE FIX, 2026-09-21] The model's own markers are discarded and the code emphasises
    /// each paragraph's leading figure with its unit. Every presentation defect this session came
    /// from asking the model to format: told not to bold bare numbers it bolded whole sentences,
    /// then told to keep emphasis short it invented "3 liable open items" to fill the span.
    /// </summary>
    [Fact]
    public void EmphasisesTheLeadingFigureOfEachParagraph_AndUnwrapsTheModelsBareFigures()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\n5 items are open and **12 items** are overdue.\n\n**268 items** fall due, of which 19 items carry liability.");

        // The lead figure of each paragraph is emphasised; a bare figure the model marked is
        // unwrapped (the code marks figures itself); the state word ("overdue", "fall due") is
        // emphasised too since 2026-09-23.
        Assert.Contains("**5 items** are open and 12 items are", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("**12 items**", normalized, StringComparison.Ordinal);
        Assert.Contains("**268 items**", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("**19 items**", normalized, StringComparison.Ordinal);
    }

    /// <summary>
    /// [OWNER, 2026-09-24] The model marks the insight and the code keeps it when it is short,
    /// says something, and holds no placeholder. The code's own phrase list then only fills what
    /// the model left, so a paragraph never carries more emphasis than before.
    /// </summary>
    [Fact]
    public void KeepsAShortInsightSpanTheModelMarked_AndAddsNoPhraseOfItsOwnBesideIt()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\nOf the 1,005 obligations that fell due so far, 367 are **already past their due date and still open**, and 109 of those carry personal criminal liability.");

        Assert.Contains("**1,005 obligations**", normalized, StringComparison.Ordinal);
        Assert.Contains("**already past their due date and still open**", normalized, StringComparison.Ordinal);
        // Budget is two impact spans: the model's one plus one from the code's list.
        Assert.Contains("**personal criminal liability**", normalized, StringComparison.Ordinal);
        Assert.Equal(3, normalized.Split("**").Length / 2);
    }

    [Fact]
    public void KeepsAtMostTwoModelSpans_AndOneBesideAName()
    {
        var plain = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\n17,041 obligations are overdue, **most of them for more than 90 days**, **half of them liability-bearing**, and **three never started**.");
        var named = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\nOf the 62 sites, 27 run **above your average overdue rate**, including your {{EG_1}} site, which **holds the largest part**.");

        Assert.Contains("**most of them for more than 90 days**", plain, StringComparison.Ordinal);
        Assert.Contains("**half of them liability-bearing**", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("**three never started**", plain, StringComparison.Ordinal);

        Assert.Contains("**above your average overdue rate**", named, StringComparison.Ordinal);
        Assert.DoesNotContain("**holds the largest part**", named, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("At your **{{NAME_1}} site**, 14 of its 179 obligations are overdue.", "**{{NAME_1}} site**")]     // a placeholder: names bind bold later
    [InlineData("As at today, **5 people hold most of the overdue work. Three of them** left.", "**5 people hold")]  // runs past a full stop
    [InlineData("The backlog is **overdue**.", "**overdue**.")]                                                        // one word is not an insight span
    public void UnwrapsAModelSpanThatIsNotAnInsight(string paragraph, string notExpected) =>
        Assert.DoesNotContain(notExpected, FreeMonthlyDraftNormalizer.Normalize("Good morning,\n\n" + paragraph), StringComparison.Ordinal);

    /// <summary>The model may put the lead figure inside its span; the code does not wrap it twice.</summary>
    [Fact]
    public void DoesNotNestTheLeadFigureInsideAModelSpan()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\nAs at {{AS_AT}}, **590 obligations from last month remain open**, and 180 of those carry personal criminal liability.");

        Assert.Contains("**590 obligations from last month remain open**", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("****", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("**590 obligations**", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void DropsAStrayUnclosedMarker()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize("Good morning,\n\n5 items are open, and **12 items are overdue.");

        Assert.Equal("Good morning,\n\n**5 items** are open, and 12 items are **overdue**.", normalized);
    }

    /// <summary>"of N" belongs to the figure - "41 of 46 items" is one idea, not two.</summary>
    [Theory]
    [InlineData("As at today, 41 of 46 items fell due last month.", "**41 of 46 items**")]
    [InlineData("Amruta Nangal holds 4,655 overdue items across the scope.", "**4,655 overdue items**")]
    [InlineData("Client Specific obligations are overdue at 98% of all cases here.", "**98% of all cases**")]
    public void EmphasisesTheFigureWithItsUnit(string paragraph, string expected) =>
        Assert.Contains(expected, FreeMonthlyDraftNormalizer.Normalize($"Good morning,\n\n{paragraph}"), StringComparison.Ordinal);

    /// <summary>
    /// [THE RECURRING DEFECT] "One point per paragraph" is what the prompt could never hold - it
    /// was dropped in a rewrite unnoticed, and once restored still produced a Location paragraph
    /// carrying four subjects. Splitting is lossless: only the blank lines move.
    /// </summary>
    [Fact]
    public void SplitsAWallOfTextThatMakesSeveralPoints()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\n2 sites have overdue obligations carrying personal criminal liability. "
            + "Across your organisation, 50 overdue obligations carry that exposure. "
            + "At one site, 39 of 143 overdue obligations carry personal criminal liability. "
            + "12 locations hold work that has been overdue for more than 90 days. "
            + "5 locations have no obligations configured at all and cannot be assessed today. "
            + "Ownership is missing from 144 of them.");

        var paragraphs = normalized.Split("\n\n");

        Assert.True(paragraphs.Length > 2, "a long paragraph carrying several subjects should be broken up");
        // A sentence with no figure explains the point before it, so it never starts a paragraph.
        Assert.EndsWith("Ownership is missing from **144** of them.", paragraphs[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// [MEASURED on tenant 1082, 2026-09-22] The splitter was reshaping work the model got RIGHT:
    /// a 5-paragraph Users draft left as 9, which turned one point about one person into two
    /// paragraphs that read as the same thing said twice. A short paragraph is now left alone
    /// however many figures it carries - this is a guard against a wall of text, not a reformatter.
    /// </summary>
    [Fact]
    public void LeavesAShortParagraphAloneEvenWhenItCarriesSeveralFigures()
    {
        const string paragraph = "Hanif Sumra holds 579 of the 2,852 overdue obligations, 20% of the total. "
                                 + "Hanif Sumra is one of 5 people in this position.";

        var normalized = FreeMonthlyDraftNormalizer.Normalize($"Good morning,\n\n{paragraph}");

        Assert.Equal(2, normalized.Split("\n\n").Length);   // greeting + the paragraph, unbroken
    }

    /// <summary>
    /// [FOUND LIVE on PROD tenant 1008, 2026-09-23] The lead paragraph emphasised its denominator
    /// (1,915) while the headline figure (180) sat plain further along. In the first paragraph the
    /// headline figure is emphasised wherever it is; a later paragraph still takes its first figure.
    /// </summary>
    [Fact]
    public void EmphasisesTheHeadlineFigureInTheLeadParagraph()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\nOf the 1,915 obligations that fell due in August, 590 remained open, including 180 that carry personal criminal liability.\n\nThe standing backlog is 17,041 overdue obligations, of which 180 carry personal criminal liability.",
            headlineFigure: "180");

        Assert.Contains("including **180** that carry **personal criminal liability**", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("**1,915", normalized, StringComparison.Ordinal);
        Assert.Contains("**17,041 overdue obligations**", normalized, StringComparison.Ordinal);
    }

    /// <summary>[OWNER, 2026-09-23] The figure, then up to two meaning phrases: the reader scanning the bold alone gets the insight.</summary>
    [Fact]
    public void EmphasisesUpToTwoMeaningPhrasesBesideTheFigure()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\nOf the 1,005 obligations that have fallen due so far in September, 367 are already past their due date and remain open, including 109 that carry personal criminal liability.");

        Assert.Contains("**1,005 obligations**", normalized, StringComparison.Ordinal);
        Assert.Contains("**personal criminal liability**", normalized, StringComparison.Ordinal);
        Assert.Contains("**already past their due date**", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("**remain open**", normalized, StringComparison.Ordinal);   // the third phrase is not taken
    }

    /// <summary>[OWNER, 2026-09-23] Phrases yield to names: three or more names in a paragraph leave no phrase bold; one or two leave one.</summary>
    [Fact]
    public void PhrasesYieldToNamesSoAParagraphIsNeverMostlyBold()
    {
        var threeNames = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\nOf the 62 locations holding obligations that carry personal criminal liability, 27 have a higher overdue rate than your organisation, including {{EG_1}}, {{EG_2}} and {{EG_3}}.");
        Assert.Contains("**62 locations**", threeNames, StringComparison.Ordinal);
        Assert.Contains("**personal criminal liability**", threeNames, StringComparison.Ordinal);
        Assert.DoesNotContain("**higher overdue rate", threeNames, StringComparison.Ordinal);

        var oneName = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\nAt your {{NAME_1}} site, 14 of its 179 overdue obligations carry personal criminal liability and are overdue for more than 90 days.");
        Assert.Contains("**14 of its 179 overdue obligations**", oneName, StringComparison.Ordinal);
        Assert.Contains("**personal criminal liability**", oneName, StringComparison.Ordinal);
        Assert.DoesNotContain("**overdue for more than 90 days**", oneName, StringComparison.Ordinal);
    }

    /// <summary>[FOUND LIVE on PROD tenant 1008, 2026-09-23] "286 of those 590 open obligations" needs five steps to reach its unit.</summary>
    [Fact]
    public void ReachesAUnitFiveWordsOut()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize("Good morning,\n\nThe month closed with 286 of those 590 open obligations rated critical.");

        Assert.Contains("**286 of those 590 open obligations**", normalized, StringComparison.Ordinal);
    }

    /// <summary>A figure with one supporting figure is a single point and stays whole.</summary>
    [Fact]
    public void LeavesAPointWithItsSupportingFigureAlone()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\nYour sites have 304 overdue obligations today. 302 of them have been overdue for more than 90 days. One item has never been started.");

        Assert.Equal(2, normalized.Split("\n\n").Length);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 5] An age band is never the point of a paragraph - every one of these
    /// emails mentions the 90-day threshold - so emphasis skips it and takes the real figure.
    /// </summary>
    [Fact]
    public void NeverEmphasisesAnAgeBand()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\nMost overdue work has been carried for more than 90 days, covering 302 of 304 items.");

        Assert.DoesNotContain("**90 days**", normalized, StringComparison.Ordinal);
        Assert.Contains("**302 of 304 items**", normalized, StringComparison.Ordinal);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 5, 2026-09-21] "**1 licence expired during** August" - the walk took
    /// every word the stop-list did not know, so it ran past the unit into the verb. The span now
    /// ends at the thing the figure counts.
    /// </summary>
    [Fact]
    public void EndsTheEmphasisAtTheUnitAndNotInTheVerb()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\n1 licence expired during August. Today, 47 licences are expired with no renewal in progress.");

        Assert.Contains("**1 licence**", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("**1 licence expired during**", normalized, StringComparison.Ordinal);
    }

    /// <summary>The unit can sit several words after the figure, and everything up to it belongs.</summary>
    [Theory]
    [InlineData("41 of 46 items fell due last month.", "**41 of 46 items**")]
    [InlineData("5,178 overdue obligations remain open.", "**5,178 overdue obligations**")]
    [InlineData("304 obligations are overdue today.", "**304 obligations**")]
    public void EmphasisesTheFigureWithTheUnitItCounts(string paragraph, string expected) =>
        Assert.Contains(expected, FreeMonthlyDraftNormalizer.Normalize("Good morning,\n\n" + paragraph), StringComparison.Ordinal);

    /// <summary>A paragraph with no figure has nothing to stress, and gets no markers.</summary>
    [Fact]
    public void LeavesAParagraphWithNoFigureUnemphasised() =>
        Assert.DoesNotContain("**", FreeMonthlyDraftNormalizer.Normalize("Good morning,\n\nOwnership of this work is not recorded anywhere."));

    [Fact]
    public void ADraftThatOnlyOverBolds_NowPasses()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Overview());
        var draft = "Good morning,\n\n**5 items** from last month carry **personal criminal liability** and **24 items** are **still open** as at {{AS_AT}} across your scope.\n\n"
                    + "Between today and the end of {{CURR_MONTH}}, **268 items** fall due, and 4 of the overdue items have no one assigned.\n\n"
                    // Both findings are named: an email given findings has to use them, so a draft
                    // testing emphasis still has to satisfy that check.
                    + "{{NAME_1}} at {{NAME_1_AT}} expires on {{DATE_1}} with no renewal filed. {{NAME_2}} holds **6** of them.";

        Assert.False(FreeMonthlyDigestValidator.Validate(draft, prompt).IsValid);
        Assert.True(FreeMonthlyDigestValidator.Validate(FreeMonthlyDraftNormalizer.Normalize(draft), prompt).IsValid);
    }

    /// <summary>
    /// [FOUND LIVE on tenant 1355, 2026-09-21] The model emphasised whole clauses - a shouted
    /// sentence emphasises nothing. A span longer than eight words is unwrapped (2026-09-24: the
    /// short, meaningful ones are kept), and the code marks the figure and its unit.
    /// </summary>
    [Fact]
    public void ReplacesAnEmphasisThatSwallowedTheSentence()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\nAs at today, **5 people have an unusually high share of overdue work carrying personal criminal liability**.");

        Assert.Contains("**5 people**", normalized, StringComparison.Ordinal);
        Assert.Contains("have an unusually high share of", normalized, StringComparison.Ordinal);

        // [2026-09-22] The exposure is now emphasised too - the kind of problem, not only its size.
        Assert.Contains("**personal criminal liability**", normalized, StringComparison.Ordinal);
    }

    /// <summary>A short emphasis - the figure and its unit - is exactly right and is left alone.</summary>
    [Fact]
    public void LeavesAFigureAndItsUnitEmphasised()
    {
        var normalized = FreeMonthlyDraftNormalizer.Normalize(
            "Good morning,\n\nAmruta Nangal holds **4,655 overdue items** across the scope.");

        Assert.Contains("**4,655 overdue items**", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalisingNeverRescuesAnInventedNumber()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Overview());
        var draft = "Good morning,\n\n**5 items** from last month carry personal criminal liability, and **777 items** are still open across your scope as at {{AS_AT}}.";

        var result = FreeMonthlyDigestValidator.Validate(FreeMonthlyDraftNormalizer.Normalize(draft), prompt);

        Assert.Contains(result.FailedChecks, f => f.Contains("777"));
    }
}

public sealed class FreeMonthlyPlaceholderBinderTests
{
    [Fact]
    public void Bind_ReplacesEveryPlaceholder()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Licence());

        var bound = FreeMonthlyPlaceholderBinder.Bind("{{NAME_1}} at {{NAME_1_AT}} expires on {{DATE_1}}, as at {{AS_AT}}.", prompt.Bindings);

        // [2026-09-22] A NAME is emphasised at binding - it is what the reader scans for. A DATE
        // is not: it is context for the figure, not a thing to look up.
        Assert.Equal("**Trade Licence** at **Pune Plant** expires on 30 Nov 2026, as at 29 Nov 2026.", bound);
    }

    /// <summary>
    /// [OWNER, 2026-09-23] Every proper noun is bold - findings and the examples inside a pattern
    /// sentence alike - because the names are where a scanning reader finds the insight. (The
    /// design had bound examples plain; the owner overrode that.) Dates stay plain.
    /// </summary>
    [Fact]
    public void Bind_BoldsExampleNamesToo()
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{NAME_1}}"] = "Khavda Plant",
            ["{{EG_1}}"] = "Jabalpur Depot",
            ["{{EG_2}}"] = "Surat Office",
            ["{{EG_2_AT}}"] = "Gujarat",
            ["{{DATE_1}}"] = "3 Sep 2026",
        };

        var bound = FreeMonthlyPlaceholderBinder.Bind("At {{NAME_1}} on {{DATE_1}}.\n\nAnd 18 sites including {{EG_1}} with 210 of its 300, and {{EG_2}} at {{EG_2_AT}}.", bindings);

        // Both examples bold: the second follows the first's own figure. Its site is bold with it.
        Assert.Equal("At **Khavda Plant** on 3 Sep 2026.\n\nAnd 18 sites including **Jabalpur Depot** with 210 of its 300, and **Surat Office** at **Gujarat**.", bound);
    }

    /// <summary>
    /// [OWNER, 2026-09-23] Every name is bold except inside a run: "including A, B and C" bolds A
    /// only; "A with 792, B with 358 and C with 347" bolds all three, each having its own figure.
    /// </summary>
    [Fact]
    public void Bind_BoldsEveryNameExceptInsideARunOfNames()
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{NAME_1}}"] = "Khavda Plant",
            ["{{EG_1}}"] = "Jabalpur Depot",
            ["{{EG_2}}"] = "Surat Office",
            ["{{EG_3}}"] = "Nagpur Works",
        };

        var run = FreeMonthlyPlaceholderBinder.Bind(
            "At {{NAME_1}}, 14 of its 179.\n\n18 sites are affected, including your {{EG_1}}, {{EG_2}} and {{EG_3}} sites.", bindings);
        Assert.Equal("At **Khavda Plant**, 14 of its 179.\n\n18 sites are affected, including your **Jabalpur Depot**, Surat Office and Nagpur Works sites.", run);

        var spaced = FreeMonthlyPlaceholderBinder.Bind(
            "18 sites are affected, including {{EG_1}} with 792 such overdue obligations, {{EG_2}} with 358 overdue obligations, and {{EG_3}} with 347 overdue obligations.", bindings);
        Assert.Equal("18 sites are affected, including **Jabalpur Depot** with 792 such overdue obligations, **Surat Office** with 358 overdue obligations, and **Nagpur Works** with 347 overdue obligations.", spaced);

        // Never more than three bold names in one paragraph, however well spaced.
        var four = FreeMonthlyPlaceholderBinder.Bind(
            "At {{NAME_1}} with 14 of its 179 obligations, then {{EG_1}} with 792 overdue obligations, {{EG_2}} with 358 overdue obligations, and {{EG_3}} with 347 overdue obligations.", bindings);
        Assert.Equal(3, Regex.Matches(four, @"\*\*[^*]+\*\*").Count);
        Assert.Contains("and Nagpur Works with 347", four, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_FailsClosedOnAnUnknownPlaceholder()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.Licence());

        Assert.Throws<InvalidOperationException>(() => FreeMonthlyPlaceholderBinder.Bind("{{NAME_9}} expires.", prompt.Bindings));
    }

    [Fact]
    public void CleanLabel_StripsBodySyntaxFromMasterData() =>
        Assert.Equal("Plant 2 East", FreeMonthlyDigestPrompt.CleanLabel("  Plant **2**\n{East}  "));
}

public sealed class FreeMonthlyFallbackBodyTests
{
    [Theory]
    [InlineData("overview")]
    [InlineData("users")]
    [InlineData("location")]
    [InlineData("act")]
    [InlineData("licence")]
    public void Fallback_UsesOnlyFactNumbers_AndNamesNothing(string example)
    {
        var data = MonthlyExamples.ByName(example);

        var body = FreeMonthlyFallbackBody.Build(data);

        Assert.StartsWith("Good morning,", body);
        Assert.DoesNotContain("{{", body);
        foreach (var candidate in data.Candidates)
            Assert.DoesNotContain(candidate.EntityLabel!, body);
        Assert.Contains("Next Monday", body);
    }

    [Fact]
    public void Fallback_BoldsTheHeadlineFact()
    {
        var body = FreeMonthlyFallbackBody.Build(MonthlyExamples.Overview());

        Assert.Contains("**5** still-open items from last month", body);
    }
}

public sealed class FreeMonthlyClosingTests
{
    [Fact]
    public void LastSundayOfAMonth_PointsToNextMonthsOverview() =>
        Assert.StartsWith("Next Monday you will get the November overview",
            FreeMonthlyClosing.For(MonthlyDigestCalendar.For(new DateOnly(2026, 10, 25))));

    [Fact]
    public void FourthSundayOfAFiveSundayMonth_PointsToLicences() =>
        Assert.StartsWith("Next Monday's email is about your licences",
            FreeMonthlyClosing.For(MonthlyDigestCalendar.For(new DateOnly(2026, 11, 22))));
}

public sealed class FreeMonthlySettingsTests
{
    private static Dictionary<string, string?> FullConfig() => new()
    {
        ["Budget:FreeMonthlyTokenCap:Overview"] = "12000",
        ["Budget:FreeMonthlyTokenCap:Users"] = "9000",
        ["Budget:FreeMonthlyTokenCap:Location"] = "9000",
        ["Budget:FreeMonthlyTokenCap:Act"] = "9000",
        ["Budget:FreeMonthlyTokenCap:Licence"] = "9000",
    };

    private static FreeMonthlySettings Build(Dictionary<string, string?> values) =>
        FreeMonthlySettings.Build(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            MonthlyExamples.PromptDirectory());

    [Fact]
    public void Build_ReadsEveryTokenCapFromConfiguration()
    {
        var values = FullConfig();
        values["Budget:FreeMonthlyTokenCap:Act"] = "20000";

        var settings = Build(values);

        Assert.Equal(20000, settings.TokenCapFor(MonthlyDigestSlot.Act));
    }

    /// <summary>
    /// [2026-09-23] A cap left over from before the 4-name email would fall back on every call.
    /// Configuration may raise the cap; it cannot starve the email below what its prompts need.
    /// </summary>
    [Fact]
    public void Build_RaisesAStaleTokenCapToTheFloor()
    {
        var values = FullConfig();
        values["Budget:FreeMonthlyTokenCap:Act"] = "8000";
        values["Budget:FreeMonthlyTokenCap:Overview"] = "12000";

        var settings = Build(values);

        Assert.Equal(FreeMonthlySettings.MinimumTokenCapFor(MonthlyDigestSlot.Act), settings.TokenCapFor(MonthlyDigestSlot.Act));
        Assert.Equal(16000, settings.TokenCapFor(MonthlyDigestSlot.Overview));
    }

    /// <summary>
    /// [MOVED OUT OF CONFIG 2026-09-22] AllowPersonNames and MaxDraftAttempts were required
    /// appsettings keys that every environment had to carry and that stopped the worker if absent.
    /// Neither varies by environment, so both now live in code - and a deployment that never
    /// mentions them still gets the intended behaviour.
    /// </summary>
    [Fact]
    public void Build_NeedsNoMonthlySectionInConfiguration()
    {
        var values = FullConfig();
        foreach (var key in values.Keys.Where(k => k.StartsWith("FreeDigest:Monthly:", StringComparison.Ordinal)).ToList())
            values.Remove(key);

        var settings = Build(values);

        Assert.True(settings.AllowPersonNames);
        Assert.Equal(1, settings.MaxDraftAttempts);
    }

    /// <summary>No defaults in code: every missing key stops startup, and the message names the key.</summary>
    [Theory]
    [InlineData("Budget:FreeMonthlyTokenCap:Overview")]
    public void Build_MissingKey_StopsStartupNamingTheKey(string key)
    {
        var values = FullConfig();
        values.Remove(key);

        var ex = Assert.Throws<InvalidOperationException>(() => Build(values));

        Assert.Contains(key, ex.Message);
    }

    /// <summary>
    /// A missing prompt file must stop the worker at startup, not surface on the first Sunday as a
    /// FileNotFoundException inside an activity, per scope group.
    ///
    /// <para>[REWRITTEN 2026-09-22] This used to set a prompt version of "v99" to point at a file
    /// that does not exist. Prompt versioning is gone - the filenames are fixed and the prompts
    /// ship as Content - so the missing-file case is now created the only way it can happen in
    /// production: the prompts are absent from the published output. That is a REAL deployment
    /// risk (the six .md files must be in the commit for the glob to copy them), so the check
    /// matters more without versioning than it did with it.</para>
    /// </summary>
    [Fact]
    public void Build_APromptFileThatIsNotPublished_StopsStartup()
    {
        var emptyDirectory = Path.Combine(Path.GetTempPath(), $"prompts-absent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDirectory);

        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(FullConfig()).Build();

            var ex = Assert.Throws<InvalidOperationException>(
                () => FreeMonthlySettings.Build(configuration, emptyDirectory));

            Assert.Contains("06_freetier_monthly_shared_rules.md", ex.Message);
        }
        finally
        {
            Directory.Delete(emptyDirectory, recursive: true);
        }
    }
}

public sealed class ComposeDigestActivityTests
{
    private static readonly FreeDigestSettings Settings = new()
    {
        FromAddress = "noreply@example.invalid",
        FromName = "RegTrack Insights",
        UpgradeUrl = "https://placeholder.invalid/upgrade",
        UnsubscribeBaseUrl = "https://placeholder.invalid/unsubscribe",
        UnsubscribeSigningKey = "test-signing-key",
    };

    /// <summary>
    /// The fail-closed path (CLAUDE.md Sec.10): a SQL refusal must come back as Source = Refused with
    /// an empty body - never the fallback, never an exception - because FreeDigestGenerateOrchestrator
    /// keys "persist nothing, release the slot" on exactly that value.
    /// </summary>
    [Fact]
    public async Task SqlRefusal_ReturnsRefused_WithNoBody_AndNoLlmCall()
    {
        var repository = new Mock<Insights.Data.IFreeMonthlyDigestRepository>();
        repository
            .Setup(r => r.GetSlotAsync(It.IsAny<MonthlyDigestEdition>(), 1490, 38, It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FreeMonthlyDigestRefusedException(51239, "RiskType Critical not seeded", new InvalidOperationException()));

        var llm = new Mock<IClaudeClient>(MockBehavior.Strict);
        var prompts = new Mock<IPromptLoader>(MockBehavior.Strict);

        var activity = new ComposeDigestActivity(
            new FreeMonthlyDigestComposer(
                repository.Object, new FreeMonthlyDigestWriter(llm.Object, prompts.Object), MonthlyExamples.Settings, Settings,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FreeMonthlyDigestComposer>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ComposeDigestActivity>.Instance);

        var output = await activity.RunAsync(new ComposeDigestInput(1490, 38, "2026-10-04", null));

        Assert.Equal(ComposeDigestActivity.RefusedSource, output.Source);
        Assert.Equal(string.Empty, output.Body);
        Assert.Equal(0, output.InputTokens);
    }

    /// <summary>
    /// A rejected draft is redrafted, not thrown away. Without this, one banned phrase costs the
    /// reader the whole written email - which is what pushed us to delete rules that should have
    /// stayed. The second call must carry the validator's failure text, so the model knows what
    /// to fix, and the accepted result must bill BOTH attempts.
    /// </summary>
    [Fact]
    public async Task ARejectedDraftIsRedraftedWithItsFailures_AndBothAttemptsAreBilled()
    {
        var data = MonthlyExamples.Overview();
        var prompt = FreeMonthlyDigestPrompt.Build(data);

        var repository = new Mock<Insights.Data.IFreeMonthlyDigestRepository>();
        repository
            .Setup(r => r.GetSlotAsync(It.IsAny<MonthlyDigestEdition>(), 1490, 38, It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(data);

        const string bad = "Good morning,\n\nThis is alarming: **777 items** are open.";
        var good = "Good morning,\n\n**5 items** from last month carry personal criminal liability as at {{AS_AT}}.\n\n"
                   + "{{NAME_1}} at {{NAME_1_AT}} expires on {{DATE_1}} with no renewal filed. {{NAME_2}} holds 6 of them.";

        var messages = new List<string>();
        var llm = new Mock<IClaudeClient>();
        llm
            .Setup(c => c.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string user, int _, CancellationToken _) =>
            {
                messages.Add(user);
                return new ClaudeCompletionResult(messages.Count == 1 ? bad : good, 100, 10, false);
            });

        var prompts = new Mock<IPromptLoader>();
        prompts
            .Setup(p => p.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("rules");

        var composer = new FreeMonthlyDigestComposer(
            repository.Object, new FreeMonthlyDigestWriter(llm.Object, prompts.Object), MonthlyExamples.Settings, Settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FreeMonthlyDigestComposer>.Instance);

        var output = await composer.ComposeAsync(1490, 38, MonthlyDigestCalendar.For(new DateOnly(2026, 10, 4)), "2026-10-04T06:00:00");

        Assert.Equal("Llm", output.Source);
        Assert.Equal(2, messages.Count);
        Assert.Contains("777", messages[1]);              // the rejected draft went back
        Assert.Contains("alarming", messages[1]);          // and so did the failure that named it
        Assert.Equal(200, output.InputTokens);             // both attempts billed
        Assert.Equal(20, output.OutputTokens);
    }

    /// <summary>Attempts are bounded: a model that never passes must not bill forever.</summary>
    [Fact]
    public async Task AModelThatNeverPasses_StopsAtMaxAttempts_AndFallsBack()
    {
        var data = MonthlyExamples.Overview();
        var repository = new Mock<Insights.Data.IFreeMonthlyDigestRepository>();
        repository
            .Setup(r => r.GetSlotAsync(It.IsAny<MonthlyDigestEdition>(), 1490, 38, It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(data);

        var calls = 0;
        var llm = new Mock<IClaudeClient>();
        llm
            .Setup(c => c.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                return new ClaudeCompletionResult("Good morning,\n\n**777 items** are open.", 100, 10, false);
            });

        var prompts = new Mock<IPromptLoader>();
        prompts.Setup(p => p.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("rules");

        var composer = new FreeMonthlyDigestComposer(
            repository.Object, new FreeMonthlyDigestWriter(llm.Object, prompts.Object), MonthlyExamples.Settings, Settings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FreeMonthlyDigestComposer>.Instance);

        var output = await composer.ComposeAsync(1490, 38, MonthlyDigestCalendar.For(new DateOnly(2026, 10, 4)), "2026-10-04T06:00:00");

        Assert.Equal("Fallback", output.Source);
        Assert.Equal(MonthlyExamples.Settings.MaxDraftAttempts, calls);
    }

    [Theory]
    [InlineData("2026-10-04", MonthlyDigestSlot.Overview)]
    [InlineData("2026-10-11", MonthlyDigestSlot.Users)]
    [InlineData("2026-11-29", MonthlyDigestSlot.Licence)]
    public async Task TheWeeksSundayPicksTheSlotProcThatIsCalled(string weekEnding, MonthlyDigestSlot expected)
    {
        MonthlyDigestSlot? called = null;
        var repository = new Mock<Insights.Data.IFreeMonthlyDigestRepository>();
        repository
            .Setup(r => r.GetSlotAsync(It.IsAny<MonthlyDigestEdition>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<MonthlyDigestEdition, int, int, DateTime, bool, CancellationToken>((e, _, _, _, _, _) => called = e.Slot)
            .ThrowsAsync(new FreeMonthlyDigestRefusedException(51230, "stop here", new InvalidOperationException()));

        var activity = new ComposeDigestActivity(
            new FreeMonthlyDigestComposer(
                repository.Object, new FreeMonthlyDigestWriter(Mock.Of<IClaudeClient>(), Mock.Of<IPromptLoader>()), MonthlyExamples.Settings, Settings,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FreeMonthlyDigestComposer>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ComposeDigestActivity>.Instance);

        await activity.RunAsync(new ComposeDigestInput(1490, 38, weekEnding, null));

        Assert.Equal(expected, called);
    }
}

public sealed class MonthlyDigestSubjectTests
{
    [Fact]
    public void Subject_NamesTheTopicAndMonth() =>
        Assert.Equal(
            "RegTrack Insights: Acme Holdings - Monthly overview, October 2026",
            SendDigestFromArtifactActivity.BuildSubject("Acme Holdings", new DateOnly(2026, 10, 4)));
}

internal static class MonthlyExamples
{
    private static readonly DateTime AsOf = new(2026, 10, 4, 6, 0, 0);
    /// <summary>Settings as appsettings.json ships them - there are no defaults in code to fall back on.</summary>
    public static readonly FreeMonthlySettings Settings = new()
    {
        AllowPersonNames = true,
        TokenCaps = Enum.GetValues<MonthlyDigestSlot>().ToDictionary(s => s, s => s == MonthlyDigestSlot.Overview ? 12000 : 9000),
        MaxDraftAttempts = 2,
    };


    public static MonthlyDigestData ByName(string name) => name switch
    {
        "overview" => Overview(),
        "users" => Users(),
        "location" => Location(),
        "act" => Act(),
        "licence" => Licence(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static MonthlyFact Fact(string key, int value, bool asAt = false, bool headline = false, string label = "items", string window = "stock") =>
        new(key, value, label, "section", 100, window, "volume", 3, asAt, headline);

    private static MonthlyCandidate Candidate(
        int slot, string detector, string kind, string label, int problem, int population,
        int? item = null, int? baseCount = null, int? metricPct = null, int? tenantPct = null,
        string? context = null, DateTime? eventDate = null, bool asAt = false) =>
        new(slot, detector, slot, 1, 1, kind, 1000 + slot, label, context is null ? null : "location", context,
            "metric", metricPct, tenantPct, item, baseCount, eventDate, asAt, problem, population, problem - 1);

    /// <summary>A candidate the proc returned WITHOUT a DefaultSlot - ranked, but not one of its two defaults.</summary>
    public static MonthlyCandidate Unslotted(
        string detector, string kind, long entityId, string? label, int rank, int priority,
        int? item = null, int? baseCount = null, int problem = 3, int population = 20) =>
        new(null, detector, priority, 2, rank, kind, entityId, label, null, null,
            "metric", null, null, item, baseCount, null, false, problem, population, problem - 1);

    /// <summary>The Location slot with one aggregate-mode pattern and three examples attached to it.</summary>
    public static MonthlyDigestData LocationWithAggregateExamples()
    {
        var location = Location();
        return location with
        {
            Facts =
            [
                .. location.Facts,
                new("pat_chronic_backlog", 18, "locations have an unusually high share of overdue work open for more than 90 days", "patterns", 720, "stock", "operational_continuity", 2, false, false),
                new("pat_chronic_backlog_of", 24, "locations with overdue work were compared", "patterns", 721, "ctx", "volume", 5, false, false),
            ],
            Examples =
            [
                Example("chronic_backlog", "pat_chronic_backlog", 1, 3001, "Khavda Plant", item: 210, baseCount: 300),
                Example("chronic_backlog", "pat_chronic_backlog", 2, 3002, "Jabalpur Depot", item: 90, baseCount: 120),
                Example("chronic_backlog", "pat_chronic_backlog", 3, 3003, "Surat Office", item: 40, baseCount: 55),
            ],
        };
    }

    /// <summary>A grid-6 example row: a member illustrating an aggregate pattern, with its own count and base and no rate.</summary>
    public static MonthlyExample Example(
        string detector, string patternFactKey, int rank, long entityId, string label,
        int? item = null, int? baseCount = null, string? context = null) =>
        new(detector, patternFactKey, rank, "location", entityId, label, context is null ? null : "location", context,
            item, baseCount, "overdue items more than 90 days late");

    private static MonthlyDigestData Data(DateOnly sunday, string headlineSource, MonthlyFact[] facts, MonthlyCandidate[] candidates) =>
        new(MonthlyDigestCalendar.For(sunday), AsOf.AddDays(sunday.DayNumber - new DateOnly(2026, 10, 4).DayNumber), headlineSource, facts, [], candidates, []);

    public static MonthlyDigestData Overview() => Data(new DateOnly(2026, 10, 4), "fact",
    [
        new("lm_open_liability", 5, "still-open items from last month that carry personal criminal liability for the responsible officer", "last_month", 150, "prev", "personal_liability", 1, true, true),
        Fact("od_liability", 12), Fact("od_over_90_liability", 7), Fact("od_total", 146), Fact("od_over_90_days", 38),
        Fact("od_never_touched", 21), Fact("od_no_owner", 4),
        Fact("lm_due", 812, asAt: true), Fact("lm_on_time_pct", 91, asAt: true), Fact("lm_still_open", 24, asAt: true),
        Fact("lm_open_never_touched", 9, asAt: true),
        Fact("tm_open_past_due", 33), Fact("rm_due", 268), Fact("rm_liability", 19), Fact("rm_no_owner", 6),
        Fact("rm_critical", 44), Fact("lic_total", 57), Fact("lic_expired_unrenewed", 4), Fact("lic_expiring_unrenewed", 2),
    ],
    [
        Candidate(1, "licence_expiring_unrenewed", "licence", "Factory Licence", 2, 3, context: "Pune Plant", eventDate: new DateTime(2026, 10, 20)),
        Candidate(2, "liability_overdue_location", "location", "Bhiwandi Warehouse", 2, 9, item: 6),
    ]);

    public static MonthlyDigestData Users() => Data(new DateOnly(2026, 10, 11), "candidate",
    [
        Fact("u_people_with_open_work", 42), Fact("u_people_with_overdue", 17), Fact("u_open_items", 611),
        Fact("u_open_no_owner", 0), Fact("u_open_no_reviewer", 23), Fact("u_open_self_reviewed", 9),
        Fact("u_people_self_reviewing", 2), Fact("u_inactive_people_with_work", 1),
        Fact("u_open_items_inactive_owner", 38), Fact("u_top3_overdue_share_pct", 64),
        Fact("t_od_total", 146), Fact("t_od_liability", 12),
    ],
    [
        Candidate(1, "deactivated_owner", "person", "Rajesh Iyer", 1, 42, item: 38),
        Candidate(2, "overdue_concentration", "person", "Meera Nair", 1, 17, item: 51, baseCount: 146, metricPct: 34),
    ]);

    public static MonthlyDigestData Location() => Data(new DateOnly(2026, 10, 18), "candidate",
    [
        Fact("loc_in_scope", 24, window: "ctx"), Fact("loc_with_obligations", 22, window: "ctx"), Fact("loc_without_obligations", 2, window: "ctx"),
        Fact("loc_with_last_month_open", 11, asAt: true, window: "prev"), Fact("loc_single_performer", 3),
        Fact("loc_with_overdue", 15), Fact("loc_with_liability_overdue", 4), Fact("loc_with_over_90_days", 9),
        Fact("loc_top3_overdue_share_pct", 58), Fact("t_lm_still_open", 24, asAt: true, window: "prev"), Fact("t_od_total", 146),
    ],
    [
        Candidate(1, "last_month_slippage", "location", "Bhiwandi Warehouse", 2, 15, item: 13, baseCount: 48, metricPct: 27, tenantPct: 9, asAt: true),
        Candidate(2, "single_point_of_failure", "location", "Nashik Depot", 3, 10, item: 41),
    ]);

    public static MonthlyDigestData Act() => Data(new DateOnly(2026, 10, 25), "fact",
    [
        Fact("law_in_scope", 37, window: "ctx"), Fact("law_with_liability_oblig", 11, window: "ctx"),
        Fact("law_with_last_month_open", 16, asAt: true, headline: true, window: "prev"), Fact("t_lm_still_open", 24, asAt: true, window: "prev"),
        Fact("law_with_overdue", 21), Fact("law_with_liability_overdue", 5), Fact("law_with_over_90_days", 14),
        Fact("law_overdue_at_2plus_locs", 8), Fact("law_top3_overdue_share_pct", 47), Fact("t_od_total", 146),
    ],
    [
        Candidate(1, "multi_location_pattern", "act", "Factories Act 1948", 1, 20, item: 9, baseCount: 12, metricPct: 75, tenantPct: 31),
    ]);

    // 29 Nov 2026 - a real fifth Sunday, so the Licence slot (and its word cap) is what is exercised.
    public static MonthlyDigestData Licence() => Data(new DateOnly(2026, 11, 29), "candidate",
    [
        Fact("lic_total", 57), Fact("lic_valid", 41), Fact("lic_expiring_rest_of_month", 5),
        Fact("lic_expiring_unrenewed", 3), Fact("lic_expiring_renewal_filed", 2),
        Fact("lic_lapsed_last_month", 4, asAt: true), Fact("lic_lapsed_last_month_unrenewed", 2, asAt: true),
        Fact("lic_lapsed_this_month", 1), Fact("lic_lapsed_this_month_unrenewed", 1),
        Fact("lic_expired_total", 9), Fact("lic_expired_unrenewed", 6), Fact("loc_with_expired_unrenewed", 4),
    ],
    [
        Candidate(1, "licence_expiring_unrenewed", "licence", "Trade Licence", 3, 5, context: "Pune Plant", eventDate: new DateTime(2026, 11, 30)),
        Candidate(2, "licence_lapsed_recent_unrenewed", "licence", "Shops Licence", 3, 5, context: "Thane Office", eventDate: new DateTime(2026, 10, 12), asAt: true),
    ]);

    /// <summary>
    /// The "Output:" blockquote of a prompt file, as the model would send it: quote markers removed,
    /// wrapped lines joined, paragraphs separated by a blank line.
    /// </summary>
    public static string ExampleOutput(string promptFile)
    {
        var lines = File.ReadAllLines(Path.Combine(PromptDirectory(), promptFile));
        var start = Array.FindIndex(lines, l => l.TrimEnd() == "Output:");
        Assert.True(start >= 0, $"{promptFile} has no 'Output:' worked example.");

        var paragraphs = new List<string>();
        var current = new List<string>();
        foreach (var line in lines.Skip(start + 1).SkipWhile(l => !l.StartsWith('>')).TakeWhile(l => l.StartsWith('>')))
        {
            var content = line.Length > 1 ? line[1..].Trim() : string.Empty;
            if (content.Length == 0)
            {
                if (current.Count > 0) paragraphs.Add(string.Join(" ", current));
                current.Clear();
            }
            else
            {
                current.Add(content);
            }
        }
        if (current.Count > 0) paragraphs.Add(string.Join(" ", current));

        return string.Join("\n\n", paragraphs);
    }

    internal static string PromptDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "RegtrackInsights", "prompts");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Could not find src/RegtrackInsights/prompts above the test output directory.");
    }
}
