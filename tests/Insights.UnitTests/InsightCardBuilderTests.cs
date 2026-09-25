using Insights.Agents;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// ADR-0004 (revised 2026-09-24): the card is the ten fields the /insights hub binds. Every one of
/// them except the headline and narrative is derived from procedure values and the calendar, so
/// these pin those derivations - and pin the width of the contract itself.
/// </summary>
public sealed class InsightCardBuilderTests
{
    private static InsightCard Build(MonthlyDigestData data, string source = "llm")
    {
        var input = InsightCardInput.Build(data);
        var (headline, narrative) = InsightCardFallback.Build(input.Guardrails);
        return InsightCardBuilder.Build(input, 1403, 88214, data.Edition.Sunday,
            new InsightCardText(headline, narrative, source, null, true, 10, 20, string.Empty));
    }

    [Fact]
    public void Overview_FactHeadline_IsDescriptive_WithTheWeekItCloses()
    {
        var card = Build(MonthlyExamples.Overview());   // Sunday 2026-10-04, lm_open_liability (prev, tier 1) leads

        Assert.Equal("free", card.Tier);
        Assert.Equal("descriptive", card.Type);
        Assert.Equal("high", card.Severity);
        Assert.Equal("2026-09-28", card.WeekOf);        // WeekEnding - 6, a Monday
        Assert.Equal("ins_1403_88214_20260928", card.InsightId);

        Assert.Equal(5, card.PrimaryMetric.Current);
        Assert.Equal("obligations", card.PrimaryMetric.Unit);
        Assert.Equal("lower_is_better", card.PrimaryMetric.Direction);
        Assert.Equal(0, card.PrimaryMetric.Target);
    }

    [Fact]
    public void Users_CandidateHeadline_IsDiagnostic_AndNamesThePerson()
    {
        var card = Build(MonthlyExamples.Users());   // deactivated_owner Rajesh Iyer leads

        Assert.Equal("diagnostic", card.Type);
        Assert.Equal(38, card.PrimaryMetric.Current);
        Assert.Contains("Rajesh Iyer", card.PrimaryMetric.Label);
        Assert.Contains("Rajesh Iyer", card.Headline);
    }

    [Fact]
    public void Licence_ExpiringFinding_IsPredictive()
    {
        var card = Build(MonthlyExamples.Licence());   // licence_expiring_unrenewed, Trade Licence at Pune Plant

        Assert.Equal("predictive", card.Type);
        Assert.Equal("licences", card.PrimaryMetric.Unit);
        Assert.Contains("Pune Plant", card.Headline);
        Assert.StartsWith("Likely to slip", card.Title);
    }

    [Fact]
    public void Title_SaysWhereItStandsInTime_ThenWhatItIsAbout_AndCarriesNoFigure()
    {
        Assert.Equal(
            "Likely to slip — work falling due in the rest of this month",
            InsightCardRules.Title("predictive", "obligations", InsightCardRules.TitleTheme("rm_due"), "curr", "rest_of_month"));

        Assert.Equal(
            "What changed — personal exposure last month",
            InsightCardRules.Title("descriptive", "obligations", InsightCardRules.TitleTheme("lm_open_liability"), "prev", "last_month"));

        Assert.Equal(
            "Where it sits — personal exposure",
            InsightCardRules.Title("diagnostic", "people", InsightCardRules.TitleTheme("pat_liability_share"), "stock", "ownership"));

        // A theme that already names its month does not take the suffix as well.
        Assert.Equal(
            "What changed — work still open from last month",
            InsightCardRules.Title("descriptive", "obligations", InsightCardRules.TitleThemeForDetector("last_month_slippage"), "prev", "last_month"));

        // No digits anywhere: there is nothing in a title for a closed-number check to defend.
        foreach (var name in new[] { "overview", "users", "location", "act", "licence" })
            Assert.DoesNotMatch(@"\d", Build(MonthlyExamples.ByName(name)).Title);
    }

    [Fact]
    public void Title_UsesDifferentWordsFromTheChipsAndTheModelsOwnLines()
    {
        foreach (var name in new[] { "overview", "users", "location", "act", "licence" })
        {
            var card = Build(MonthlyExamples.ByName(name));

            // The theme after the dash must not be the chip phrasing repeated back.
            var theme = card.Title.Split('—')[1].Trim();
            foreach (var chip in card.SupportingMetrics)
                Assert.NotEqual(chip.Label, theme, StringComparer.OrdinalIgnoreCase);
        }

        // The three vocabularies stay distinct for the phrase that kept repeating.
        Assert.Equal("personal exposure", InsightCardRules.TitleTheme("pat_liability_share"));
        Assert.Equal("with liability", InsightCardRules.MetricNoun("pat_liability_share"));
        Assert.Equal("with personal liability", InsightCardRules.ShortNounForFact("pat_liability_share"));
    }

    [Fact]
    public void Target_IsTheIdealImpliedByDirection_NeverAForecast()
    {
        Assert.Equal(0, InsightCardRules.Target("lower_is_better", "od_total"));
        Assert.Equal(0, InsightCardRules.Target("neutral", "obligations_in_scope"));
        Assert.Equal(100, InsightCardRules.Target("higher_is_better", "lm_on_time_pct"));

        // A rate is the only thing that completes at 100; a count that should grow still targets nothing.
        Assert.Equal(0, InsightCardRules.Target("higher_is_better", "lm_already_closed"));
    }

    [Fact]
    public void TheCardIsExactlyTheFieldsTheFrontendBinds()
    {
        var properties = typeof(InsightCard).GetProperties().Select(p => p.Name).ToHashSet();

        Assert.Equal(
            new[]
            {
                "InsightId", "Tier", "Type", "Severity", "WeekOf", "Title", "Headline", "Narrative",
                "PrimaryMetric", "SupportingMetrics",
            }.ToHashSet(),
            properties);

        Assert.Equal(
            new[] { "Label", "Current", "Target", "Unit", "Direction" }.ToHashSet(),
            typeof(InsightPrimaryMetric).GetProperties().Select(p => p.Name).ToHashSet());

        Assert.Equal(
            new[] { "Label", "Value", "Unit" }.ToHashSet(),
            typeof(InsightSupportingMetric).GetProperties().Select(p => p.Name).ToHashSet());
    }

    [Fact]
    public void SupportingMetrics_AreChips_ShortEnoughToPrintBesideTheirNumeral()
    {
        foreach (var name in new[] { "overview", "users", "location", "act", "licence" })
        {
            var data = MonthlyExamples.ByName(name);
            var card = Build(data);
            var fromTheData = data.Facts.Select(f => f.FactValue).ToHashSet();

            Assert.True(card.SupportingMetrics.Count <= InsightCardBuilder.MaxSupportingMetrics);
            Assert.All(card.SupportingMetrics, m => Assert.Contains(m.Value, fromTheData));

            foreach (var metric in card.SupportingMetrics)
            {
                // "939 lapses", not "939 licences that have lapsed without a renewal in progress".
                Assert.InRange(metric.Label.Length, 1, 34);
                Assert.DoesNotContain(".", metric.Label);
                Assert.Equal(metric.Label.ToLowerInvariant(), metric.Label);
            }

            // No two chips, and no chip and the headline, may say the same thing twice.
            Assert.Equal(
                card.SupportingMetrics.Select(m => m.Label).Distinct().Count(),
                card.SupportingMetrics.Count);

            // A chip of one reads "1 obligation", never "1 obligations".
            foreach (var metric in card.SupportingMetrics.Where(m => m.Value == 1))
                Assert.DoesNotContain(metric.Unit, metric.Label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Singular_IsTheUnitAsItReadsAfterOne()
    {
        Assert.Equal("obligation", InsightCardRules.Singular("obligations"));
        Assert.Equal("person", InsightCardRules.Singular("people"));
        Assert.Equal("Act", InsightCardRules.Singular("Acts"));
        Assert.Equal("category", InsightCardRules.Singular("categories"));
        Assert.Equal("licence type", InsightCardRules.Singular("licence types"));
    }

    [Fact]
    public void SupportingMetrics_AreNeverEmpty_EvenWhenEveryOtherFactRepeatsTheHeadline()
    {
        /*  [FOUND LIVE on tenant 23, week 2026-08-24] Headline "31 licences with no renewal in
            progress"; every other licence fact also reduces to "lapsed", so the no-repeat rule
            dropped all of them and the card shipped with nothing for the compact line. The
            denominators must step in rather than the array going out empty.                    */
        var sunday = new DateOnly(2026, 8, 23);
        var data = new MonthlyDigestData(
            MonthlyDigestCalendar.For(sunday),
            sunday.ToDateTime(new TimeOnly(12, 0)),
            "fact",
            [
                // Both of these pass every filter; they are dropped only because each reduces to
                // the headline's own qualifier, "lapsed". That is what emptied the live card.
                new MonthlyFact("lic_expired_unrenewed", 31, "licences have no renewal in progress", "licences", 100, "stock", "licence_continuity", 1, false, true),
                new MonthlyFact("lic_lapsed_recent", 12, "licences lapsed this month", "licences", 110, "stock", "licence_continuity", 2, false, false),
                MonthlyExamples.Fact("lic_total", 37, label: "licences", window: "ctx"),
                MonthlyExamples.Fact("loc_with_licences", 4, label: "locations hold licences", window: "ctx"),
            ],
            [], [], []);

        var card = Build(data);

        Assert.NotEmpty(card.SupportingMetrics);
        Assert.All(card.SupportingMetrics, m => Assert.NotEqual("lapsed", m.Label));

        // Every fixture keeps at least one chip too - the hub always has a compact line to print.
        foreach (var name in new[] { "overview", "users", "location", "act", "licence" })
            Assert.NotEmpty(Build(MonthlyExamples.ByName(name)).SupportingMetrics);
    }

    [Fact]
    public void SupportingMetrics_NameTheirUnit_OnlyWhenTheChipsCountDifferentThings()
    {
        // Users leads on a people figure whose supporting figures count obligations: both must say so.
        var users = Build(MonthlyExamples.Users());
        if (users.SupportingMetrics.Any(m => !m.Unit.Equals(users.PrimaryMetric.Unit, StringComparison.OrdinalIgnoreCase)))
            Assert.All(users.SupportingMetrics, m => Assert.StartsWith(m.Unit, m.Label, StringComparison.OrdinalIgnoreCase));

        // Where every figure counts the same thing, the unit is not repeated onto every chip.
        var location = Build(MonthlyExamples.Location());
        if (location.SupportingMetrics.All(m => m.Unit.Equals(location.PrimaryMetric.Unit, StringComparison.OrdinalIgnoreCase)))
            Assert.All(location.SupportingMetrics, m => Assert.DoesNotContain($"{m.Unit} {m.Unit}", m.Label));
    }

    [Fact]
    public void PatternFacts_CountMembers_NotObligations()
    {
        // The shared detectors (sql/37) run for whichever subject they were called from.
        Assert.Equal("people", InsightCardRules.UnitForFact("pat_liability_share", MonthlyDigestSlot.Users));
        Assert.Equal("locations", InsightCardRules.UnitForFact("pat_liability_share", MonthlyDigestSlot.Location));
        Assert.Equal("Acts", InsightCardRules.UnitForFact("pat_overdue_concentration", MonthlyDigestSlot.Act));

        // ... and the one whose name reads backwards: it counts the Acts, not the locations.
        Assert.Equal("Acts", InsightCardRules.UnitForFact("pat_multi_location_pattern", MonthlyDigestSlot.Act));
        Assert.Equal("licence types", InsightCardRules.UnitForFact("pat_licence_type_lapse_rate", MonthlyDigestSlot.Licence));
        Assert.Equal("locations", InsightCardRules.UnitForFact("pat_expired_unrenewed_location", MonthlyDigestSlot.Licence));

        // An ordinary fact is unaffected by the subject it arrived on.
        Assert.Equal("obligations", InsightCardRules.UnitForFact("od_total", MonthlyDigestSlot.Users));
        Assert.Equal("people", InsightCardRules.UnitForFact("u_people_with_overdue", MonthlyDigestSlot.Users));
    }

    [Fact]
    public void Type_NeverComesFromAContextFact()
    {
        Assert.Throws<InvalidOperationException>(() => InsightCardRules.TypeForFact("ctx", "estate", "obligations_in_scope"));
        Assert.Equal("predictive", InsightCardRules.TypeForFact("curr", "rest_of_month", "rm_liability"));
        Assert.Equal("diagnostic", InsightCardRules.TypeForFact("curr", "this_month", "tm_open_past_due"));
        Assert.Equal("descriptive", InsightCardRules.TypeForFact("prev", "last_month", "lm_still_open"));
        Assert.Equal("diagnostic", InsightCardRules.TypeForFact("stock", "overdue_now", "od_total"));
    }

    [Fact]
    public void Vocabulary_NeverSaysTasksStoresOrAccounts()
    {
        foreach (var key in new[] { "od_total", "lic_expired_unrenewed", "loc_with_overdue", "law_with_overdue", "u_people_with_overdue", "u_open_no_owner" })
        {
            var unit = InsightCardRules.UnitForFact(key);
            Assert.DoesNotContain("task", unit);
            Assert.DoesNotContain("store", unit);
            Assert.DoesNotContain("account", unit);
        }

        foreach (var name in new[] { "overview", "users", "location", "act", "licence" })
        {
            var card = Build(MonthlyExamples.ByName(name));
            foreach (var text in new[] { card.Title, card.PrimaryMetric.Label }.Concat(card.SupportingMetrics.Select(m => m.Label)))
            {
                Assert.DoesNotContain("task", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("store", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("account", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

/// <summary>
/// [FOUND on tenant 1082, 2026-09-23] Two licence rows, both "Motor Vehicle Pollution under
/// Control" at "Khavda", held both slots and the card named the same licence and site twice.
/// </summary>
public sealed class InsightCardRepeatedNameTests
{
    [Fact]
    public void Card_NamesTheSameLicenceAtTheSameSiteOnce_EvenOnDifferentDates()
    {
        var data = MonthlyExamples.Licence();
        var first = data.Candidates[0];
        var twin = first with { DefaultSlot = 2, EntityId = 2002, EventDate = first.EventDate!.Value.AddDays(1) };

        var input = InsightCardInput.Build(data with { Candidates = [first, twin] });

        Assert.Single(input.NamedFindings);
        Assert.Contains("{{NAME_1}}", input.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("{{NAME_2}}", input.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Email_NamesTheSameLicenceSiteAndDateOnce_WhenBothHoldTheProcsDefaultSlots()
    {
        var data = MonthlyExamples.Licence();
        var first = data.Candidates[0];
        var twin = first with { DefaultSlot = 2, EntityId = 2002 };

        var prompt = FreeMonthlyDigestPrompt.Build(data with { Candidates = [first, twin] });

        Assert.Single(prompt.NamedFindings);
    }

    [Fact]
    public void Card_StillNamesTwoDifferentLicences()
    {
        var input = InsightCardInput.Build(MonthlyExamples.Licence());

        Assert.Equal(2, input.NamedFindings.Count);
    }
}

/// <summary>
/// [FOUND 2026-09-25] A card said "{site} holds 81% of the overdue work" where 81% was the share
/// of that site's OWN overdue obligations carrying liability, and gave no period at all.
/// </summary>
public sealed class InsightCardPeriodAndShareTests
{
    [Fact]
    public void EveryFactSentToTheModel_CarriesItsPeriodInWords()
    {
        var input = InsightCardInput.Build(MonthlyExamples.Overview());

        Assert.Contains("\"period\":\"obligations that fell due in {{PREV_MONTH}}", input.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\"period\":\"\"", input.UserMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stock", "including those that fell due in earlier months")]
    [InlineData("prev", "{{PREV_MONTH}}")]
    [InlineData("curr", "{{CURR_MONTH}}")]
    public void PeriodOf_NamesTheWindow(string windowScope, string expected) =>
        Assert.Contains(expected, InsightCardInput.PeriodOf(windowScope, "section", "key"), StringComparison.Ordinal);

    [Fact]
    public void Figures_SayALiabilityPercentageIsAShareOfTheMembersOwnOverdueWork()
    {
        var finding = MonthlyExamples.Users().Candidates[0] with { Metric = "overdue_with_liability_pct" };

        var figures = InsightCardInput.Figures(finding);

        Assert.Contains("OWN overdue obligations", figures, StringComparison.Ordinal);
        Assert.Contains("not a share of the organisation's overdue work", figures, StringComparison.Ordinal);
        Assert.Contains("{{AS_AT}}", figures, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("First sentence. Second sentence here.", true)]
    [InlineData("First sentence. Second one. And a third.", true)]
    [InlineData("One. Two here. Three here. Four here.", false)]
    public void Narrative_AllowsTwoOrThreeSentences(string text, bool allowed)
    {
        var count = InsightCardWriter.SentenceCount(text);
        Assert.Equal(allowed, count is >= InsightCardWriter.MinNarrativeSentences and <= InsightCardWriter.MaxNarrativeSentences);
    }
}

public sealed class InsightCardWriterParseTests
{
    [Fact]
    public void Parse_ToleratesACodeFence()
    {
        var parsed = InsightCardWriter.Parse("```json\n{\"headline\":\"h\",\"narrative\":\"n\"}\n```");

        Assert.NotNull(parsed);
        Assert.Equal("h", parsed!.Value.Headline);
        Assert.Equal("n", parsed.Value.Narrative);
    }

    [Fact]
    public void Parse_RefusesAnythingWithoutBothLines() =>
        Assert.Null(InsightCardWriter.Parse("{\"headline\":\"h\"}"));

    [Theory]
    [InlineData("One sentence only.", 1)]
    [InlineData("First sentence here. Second sentence here.", 2)]
    [InlineData("First sentence. Second one. And a third.", 3)]
    [InlineData("There are 1,234 obligations. {{NAME_1}} holds 51 of them.", 2)]
    public void SentenceCount_SplitsOnTerminalsFollowedByACapital(string text, int expected) =>
        Assert.Equal(expected, InsightCardWriter.SentenceCount(text));
}
