using System.Text.RegularExpressions;
using Insights.Agents;
using Insights.Domain;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// [2026-10-12] Fact-bound figures (FreeMonthlyFactTokens). The model writes tokens; these checks are
/// what stop a real number from being written under the wrong description. Each live slip from prod
/// tenant 1008 has its own test.
/// </summary>
public class FreeMonthlyFactTokensTests
{
    private static FreeMonthlyDigestPrompt Prompt(string name) => FreeMonthlyDigestPrompt.Build(MonthlyExamples.ByName(name));

    private static MonthlyFact F(string key, int value, string window = "stock", string label = "items") =>
        new(key, value, label, "section", 100, window, "operational_continuity", 2, false, false);

    /// <summary>An Act slot shaped like tenant 1008's: law_overdue_at_2plus_locs = 137, pattern 59 of 210.</summary>
    private static FreeMonthlyDigestPrompt ActLikeMinda() => FreeMonthlyDigestPrompt.Build(MonthlyExamples.Data(new DateOnly(2026, 10, 25), "fact",
    [
        F("law_in_scope", 332, "ctx"),
        new("law_with_last_month_open", 101, "laws still have obligations open from last month", "s", 1, "prev", "operational_continuity", 1, true, true),
        F("law_overdue_at_2plus_locs", 137),
        F("pat_multi_location_pattern", 59), F("pat_multi_location_pattern_of", 210, "ctx"),
        F("t_od_total", 13820), F("t_od_liability", 4742),
    ], []));

    private static IReadOnlyList<string> Problems(FreeMonthlyDigestPrompt p, string body) => FreeMonthlyFactTokens.Problems(body, p);

    // -- registry ------------------------------------------------------------------------------

    [Theory]
    [InlineData("overview")]
    [InlineData("users")]
    [InlineData("location")]
    [InlineData("act")]
    [InlineData("licence")]
    public void EveryTokenValueIsAlreadyInTheClosedSet(string name)
    {
        var p = Prompt(name);
        Assert.NotEmpty(p.FactTokens);
        foreach (var t in p.FactTokens.Values)
            Assert.True(t.IsPercent ? p.AllowedPercentages.Contains(t.Value) : p.AllowedNumbers.Contains(t.Value),
                $"{t.Token} = {t.Value} is not in the validator's closed set");
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("users")]
    [InlineData("location")]
    [InlineData("act")]
    [InlineData("licence")]
    public void EveryTokenIsPrintedInTheUserMessage(string name)
    {
        var p = Prompt(name);
        foreach (var t in p.FactTokens.Keys)
            Assert.Contains(t, p.UserMessage);
    }

    [Fact]
    public void ForbiddenFactsGetNoToken() =>
        Assert.DoesNotContain("{{F:pat_expired_unrenewed_location}}", FreeMonthlyDigestPrompt.Build(MonthlyExamples.LicenceMinda()).FactTokens.Keys);

    [Fact]
    public void FactTokensDoNotLeakIntoNameBindings() =>
        Assert.DoesNotContain(Prompt("act").Bindings.Keys, k => k.Contains(':'));

    /// <summary>Every fact key the deployed v4 slot procs emit resolves to a unit (or is knowingly unit-less).</summary>
    [Fact]
    public void EveryDeployedFactKeyHasAUnit()
    {
        var root = Path.Combine(MonthlyExamples.PromptDirectory(), "..", "..", "..", "sql", "v4-free-tier-detailed-report");
        var keys = new[] { "36", "38", "39", "40", "41" }
            .SelectMany(n => Directory.GetFiles(root, n + "_*.sql"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\(N?'([a-z][a-z0-9_]+)'\s*,\s*(?:N?'|[0-9]|@)").Select(m => m.Groups[1].Value))
            .Distinct()
            .Where(k => !DetectorNames.Contains(k))
            .ToList();
        Assert.NotEmpty(keys);

        var slot = MonthlyDigestSlot.Overview;
        var missing = keys.Where(k => FreeMonthlyFactTokens.UnitOfFact(k, slot).Length == 0 && !UnitLess.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "fact keys with no unit: " + string.Join(", ", missing));
    }

    private static readonly HashSet<string> DetectorNames = new(StringComparer.Ordinal)
    {
        "deactivated_owner", "self_review", "ghost_location", "single_point_of_failure", "multi_location_pattern",
        "expired_unrenewed_location", "licence_type_lapse_rate", "liability_share", "overdue_concentration",
    };

    private static readonly HashSet<string> UnitLess = new(StringComparer.Ordinal) { "prev_month_not_settled" };

    // -- binding -------------------------------------------------------------------------------

    [Fact]
    public void BindRendersCommasAndPercentSigns()
    {
        var p = ActLikeMinda();
        Assert.Equal("Of the 13,820 overdue compliances, 4,742 carry personal liability.",
            FreeMonthlyFactTokens.Bind("Of the {{F:t_od_total}} overdue compliances, {{F:t_od_liability}} carry personal liability.", p));

        var users = Prompt("users");
        Assert.Equal("or 34%.", FreeMonthlyFactTokens.Bind("or {{NAME_2:pct}}%.", users));
    }

    // -- the live slips --------------------------------------------------------------------------

    /// <summary>Prod 1008, 2026-10-09: "137 of the 210 Acts ... higher share" - the right pair was 59 of 210.</summary>
    [Fact]
    public void ACountIsNeverWrittenAgainstAnotherPatternsWhole()
    {
        var p = ActLikeMinda();
        var wrong = Problems(p, "In total, from all months, {{F:law_overdue_at_2plus_locs}} of the {{F:pat_multi_location_pattern_of}} Acts that apply at 2 or more locations are overdue across a higher share of their locations than the other Acts.");
        Assert.Contains(wrong, x => x.Contains("is not what {{F:law_overdue_at_2plus_locs}} is counted against"));

        var right = Problems(p, "In total, from all months, {{F:pat_multi_location_pattern}} of the {{F:pat_multi_location_pattern_of}} Acts that apply at 2 or more locations are overdue across a higher share of their locations than the other Acts.");
        Assert.Empty(right);
    }

    /// <summary>Prod 1008, 2026-10-09: "21 of the 22 sites where it applies" - a slippage example counting compliances.</summary>
    [Fact]
    public void ACompliancesCountIsNeverWrittenAsSites()
    {
        var p = Prompt("location");
        var problems = Problems(p, "In {{PREV_MONTH}}, {{F:loc_with_last_month_open}} of your {{F:loc_in_scope}} locations still have compliances open. At your {{NAME_1}} site, {{NAME_1:item}} of the {{NAME_1:base}} sites have it overdue.");
        Assert.Contains(problems, x => x.Contains("{{NAME_1:item}} as sites") && x.Contains("counts compliances"));
    }

    /// <summary>An all-months figure under a last-month frame.</summary>
    [Fact]
    public void AnAllMonthsFigureIsNeverUnderLastMonth()
    {
        var p = ActLikeMinda();
        var problems = Problems(p, "In {{PREV_MONTH}}, {{F:law_with_last_month_open}} of your {{F:law_in_scope}} Acts still had compliances open, and {{F:t_od_total}} compliances are overdue.");
        Assert.Contains(problems, x => x.Contains("puts {{F:t_od_total}} under last month"));
    }

    /// <summary>Prod 1008, 2026-10-09: "611 compliances were due" - 611 was the count still open.</summary>
    [Fact]
    public void AnOpenCountIsNeverSaidToHaveFallenDue()
    {
        var p = Prompt("location");
        var problems = Problems(p, "In {{PREV_MONTH}}, {{F:t_lm_still_open}} compliances were due across your organisation.");
        Assert.Contains(problems, x => x.Contains("{{F:t_lm_still_open}} were due"));
    }

    /// <summary>A plain count described with comparison words.</summary>
    [Fact]
    public void APlainCountCannotCarryAComparison()
    {
        var p = ActLikeMinda();
        var problems = Problems(p, "In total, from all months, {{F:law_overdue_at_2plus_locs}} of your {{F:law_in_scope}} Acts are overdue at 2 or more locations, a higher share than the other Acts.");
        Assert.Contains(problems, x => x.Contains("none of them is a comparison"));
    }

    // -- the other rules ------------------------------------------------------------------------

    [Fact]
    public void TypedFiguresAndNumberWordsAreRejected()
    {
        var p = ActLikeMinda();
        Assert.Contains(Problems(p, "In total, from all months, 13,820 compliances are overdue."), x => x.Contains("types the figure"));
        Assert.Contains(Problems(p, "In total, from all months, three Acts are overdue."), x => x.Contains("spells the figure 'three'"));
        Assert.Empty(Problems(p, "In total, from all months, {{F:t_od_total}} compliances are overdue, and the oldest have been overdue for more than 90 days."));
    }

    [Fact]
    public void AnUnknownTokenIsRejected() =>
        Assert.Contains(Problems(ActLikeMinda(), "In total, from all months, {{F:od_total}} compliances are overdue."), x => x.Contains("not one of the figures in the input"));

    [Fact]
    public void ALiabilityFigureMustSayLiability()
    {
        var p = ActLikeMinda();
        Assert.Contains(Problems(p, "In total, from all months, {{F:t_od_liability}} of the {{F:t_od_total}} overdue compliances are serious."),
            x => x.Contains("'personal liability'"));
        Assert.Empty(Problems(p, "In total, from all months, {{F:t_od_liability}} of the {{F:t_od_total}} overdue compliances carry personal liability."));
    }

    [Fact]
    public void OfThoseMustPointBackToItsOwnWhole()
    {
        var p = ActLikeMinda();
        Assert.Empty(Problems(p, "In total, from all months, {{F:t_od_total}} compliances are overdue. {{F:t_od_liability}} of those carry personal liability."));
        Assert.Contains(Problems(p, "In total, from all months, {{F:law_overdue_at_2plus_locs}} Acts are overdue at 2 or more locations. {{F:t_od_liability}} of those carry personal liability."),
            x => x.Contains("{{F:t_od_liability}} \"of those\" right after {{F:law_overdue_at_2plus_locs}}"));

        // [FOUND 2026-10-14, Minda Overview] "those" is the NEAREST figure - here the over-90 part, not the total.
        Assert.Contains(OverviewProblems("In total, from all months, {{F:od_total}} compliances are overdue. {{F:od_over_90_days}} of those have been overdue for more than 90 days. {{F:od_liability}} of those carry personal liability."),
            x => x.Contains("{{F:od_liability}} \"of those\" right after {{F:od_over_90_days}}"));
    }

    [Fact]
    public void ANamedFigureSitsWithItsName()
    {
        var p = Prompt("users");
        Assert.Contains(Problems(p, "In total, from all months, {{F:t_od_total}} compliances are overdue. {{NAME_2:item}} of the {{NAME_2:base}} overdue compliances sit with one person."),
            x => x.Contains("does not name {{NAME_2}}"));
        Assert.Empty(Problems(p, "In total, from all months, {{NAME_2}} holds {{NAME_2:item}} of the {{NAME_2:base}} overdue compliances, or {{NAME_2:pct}}."));
    }

    [Fact]
    public void ANamedFigureCannotUseAnotherFactAsItsWhole()
    {
        // overdue_concentration's base equals t_od_total in value - equal values are exactly how slips got through.
        var p = Prompt("users");
        Assert.Contains(Problems(p, "In total, from all months, {{NAME_2}} holds {{NAME_2:item}} of the {{F:t_od_total}} overdue compliances."),
            x => x.Contains("{{NAME_2:item}} as part of {{F:t_od_total}}"));
    }

    [Fact]
    public void AShareCannotBeCalledHigherWhenItIsLower()
    {
        var data = MonthlyExamples.Location() with
        {
            Candidates =
            [
                MonthlyExamples.Candidate(1, "liability_share", "location", "Bhiwandi Warehouse", 2, 15, item: 5, baseCount: 48, metricPct: 10, tenantPct: 35),
            ],
        };
        var p = FreeMonthlyDigestPrompt.Build(data);
        Assert.Contains(Problems(p, "In total, from all months, at your {{NAME_1}} site, {{NAME_1:item}} of its {{NAME_1:base}} overdue compliances carry personal liability, or {{NAME_1:pct}}, higher than your company average of {{NAME_1:tenant_pct}}."),
            x => x.Contains("is higher than") && x.Contains("but it is lower"));
    }

    [Fact]
    public void AFigureFromLastMonthNeedsItsPeriod() =>
        Assert.Contains(Problems(Prompt("location"), "{{F:t_lm_still_open}} compliances are still open."), x => x.Contains("without saying which period"));

    // -- after repair --------------------------------------------------------------------------

    /// <summary>
    /// Repair can cut a trailing clause after the checks passed. A pattern count that loses its
    /// comparison words must be caught again.
    /// </summary>
    [Fact]
    public void ASentenceRepairShortensIsCheckedAgain()
    {
        var p = ActLikeMinda();
        const string raw = "In total, from all months, {{F:pat_multi_location_pattern}} of the {{F:pat_multi_location_pattern_of}} Acts that apply at 2 or more locations are overdue, reflecting a higher share of their locations than the other Acts.";
        Assert.Empty(Problems(p, raw));

        var repaired = "In total, from all months, 59 of the 210 Acts that apply at 2 or more locations are overdue.";
        Assert.Contains(FreeMonthlyFactTokens.ProblemsAfterRepair(raw, repaired, p), x => x.Contains("after repair shortened a sentence"));
        Assert.Empty(FreeMonthlyFactTokens.ProblemsAfterRepair(raw, FreeMonthlyFactTokens.Bind(raw, p), p));
    }

    [Fact]
    public void ValidatorMessagesNameTheTokenBehindEachFigure()
    {
        var p = ActLikeMinda();
        const string raw = "In total, from all months, {{F:t_od_total}} compliances are overdue.";
        Assert.Contains("({{F:t_od_total}})", FreeMonthlyFactTokens.Annotate("puts 13,820 in the wrong place", raw, p));
    }

    // -- the prompts stay in step with the code ---------------------------------------------------

    private static string Prompts(params string[] files) =>
        string.Join("\n", files.Select(f => File.ReadAllText(Path.Combine(MonthlyExamples.PromptDirectory(), f))));

    private static readonly string[] AllPromptFiles =
    [
        "06_freetier_monthly_shared_rules.md", "06a_freetier_monthly_overview.md", "06b_freetier_monthly_users.md",
        "06c_freetier_monthly_location.md", "06d_freetier_monthly_act.md", "06e_freetier_monthly_licence.md",
    ];

    /// <summary>Every closing line the shared rules offer survives repair - otherwise the model is told to write a line the code deletes.</summary>
    [Fact]
    public void EveryClosingLineInTheRulesIsKeptByRepair()
    {
        var rules = Prompts("06_freetier_monthly_shared_rules.md");
        var table = rules[rules.IndexOf("## 6. Closing lines", StringComparison.Ordinal)..rules.IndexOf("## 7.", StringComparison.Ordinal)];
        var lines = Regex.Matches(table, @"^\|[^|]+\| ""([^""]+)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.True(lines.Count >= 15, $"found only {lines.Count} closing lines");

        var kept = (string[])typeof(FreeMonthlyDraftRepair)
            .GetField("KeptWithoutAFigure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        foreach (var line in lines)
            Assert.True(kept.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)), $"closing line not kept by repair: {line}");
    }

    /// <summary>
    /// A {{F:key}} a slot prompt names must be a key THAT slot's proc emits - a key from another slot
    /// is an unknown token on every run of this one. The shared rules name no concrete key at all:
    /// they go with every slot, and only an Overview run has od_total.
    /// </summary>
    [Theory]
    [InlineData("06_freetier_monthly_shared_rules.md", new string[0])]
    [InlineData("06a_freetier_monthly_overview.md", new[] { "36" })]
    [InlineData("06b_freetier_monthly_users.md", new[] { "38", "37" })]
    [InlineData("06c_freetier_monthly_location.md", new[] { "39", "37" })]
    [InlineData("06d_freetier_monthly_act.md", new[] { "40", "37" })]
    [InlineData("06e_freetier_monthly_licence.md", new[] { "41" })]
    public void EveryFactTokenAPromptNamesIsEmittedForItsSlot(string promptFile, string[] procFiles)
    {
        var root = Path.Combine(MonthlyExamples.PromptDirectory(), "..", "..", "..", "sql", "v4-free-tier-detailed-report");
        var sql = string.Join("\n", procFiles.SelectMany(n => Directory.GetFiles(root, n + "_*.sql")).Select(File.ReadAllText));
        var named = Regex.Matches(Prompts(promptFile), @"\{\{F:([a-z0-9_]+)\}\}").Select(m => m.Groups[1].Value).Distinct();
        foreach (var key in named)
            Assert.True(sql.Contains("'" + key + "'", StringComparison.Ordinal), $"{promptFile} names {{{{F:{key}}}}}, which its slot's proc does not emit");
    }

    [Fact]
    public void ADataGapTokenIsInTheClosedSet()
    {
        var data = MonthlyExamples.Licence() with { DataQuality = [new MonthlyDataQuality("licences_without_end_date", 12, "licences have no end date recorded")] };
        var p = FreeMonthlyDigestPrompt.Build(data);
        var q = p.FactTokens.Values.Where(t => t.Token.StartsWith("{{Q:", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(q);
        Assert.All(q, t => Assert.Contains(t.Value, p.AllowedNumbers));
        Assert.Equal("12", FreeMonthlyFactTokens.Bind(q[0].Token, p));
        Assert.Contains(q[0].Token, p.UserMessage);
    }

    [Fact]
    public void ATokenLeftInTheFinalTextFailsClosed()
    {
        Assert.NotEmpty(FreeMonthlyFactTokens.Unbound("In total, {{F:od_total}} compliances are overdue."));
        Assert.NotEmpty(FreeMonthlyFactTokens.Unbound("Bhiwandi holds {{NAME_1:item}} of them."));
        Assert.Empty(FreeMonthlyFactTokens.Unbound("In total, 146 compliances are overdue as at {{AS_AT}}."));
    }

    /// <summary>A quoted sentence in the prompts is copied; it must not type a figure the checks then reject.</summary>
    [Fact]
    public void NoPromptSentenceTypesAFigure()
    {
        var quoted = Regex.Matches(Prompts(AllPromptFiles), @"""((?:[A-Z]|\{\{|\.\.\.)[^""`]*?\{\{[^""`]*?)""").Select(m => m.Groups[1].Value);
        foreach (var q in quoted)
        {
            var bare = Regex.Replace(q, @"\{\{[^}]+\}\}", " ");
            bare = Regex.Replace(bare, @"\bmore than 90 days\b|\b2 or more\b|\b1 of\b|\b3 (?=\w+ with the most)", " ", RegexOptions.IgnoreCase);
            Assert.False(Regex.IsMatch(bare, @"\d"), $"prompt sentence types a figure: {q}");
        }
    }

    // -- wording: the 2026-10-13 Minda Overview (new code, attempt 1, accepted) -------------------

    private static readonly FreeMonthlyDigestPrompt OverviewPrompt = Prompt("overview");

    private static IReadOnlyList<string> OverviewProblems(string body) => Problems(OverviewPrompt, "Good morning,\n\nHere is your monthly update on compliance work across your organisation. The figures below are as of {{AS_AT}}.\n\n" + body);

    [Fact]
    public void LabelWordingIsNeverCopied()
    {
        Assert.Contains(OverviewProblems("In total, from all months, {{F:od_total}} compliances are overdue, whatever date they were originally due."), x => x.Contains("label wording"));
        Assert.Contains(OverviewProblems("Before the end of {{CURR_MONTH}}, {{F:rm_due}} compliances fall due between today and the end of the month."), x => x.Contains("label wording"));
    }

    [Fact]
    public void ThePeriodIsSaidOnce() =>
        Assert.Contains(OverviewProblems("In {{PREV_MONTH}}, {{F:lm_due}} compliances fell due last month."), x => x.Contains("says the period twice"));

    [Fact]
    public void TheIntroductionIsTheFixedOne() =>
        Assert.Contains(Problems(OverviewPrompt, "Good morning,\n\nThis email covers your compliance work. The figures below are as of {{AS_AT}}."), x => x.Contains("Here is your monthly update on"));

    [Fact]
    public void APartAfterItsWholeSaysItIsAPart()
    {
        Assert.Contains(OverviewProblems("In {{PREV_MONTH}}, {{F:lm_due}} compliances fell due. {{F:lm_still_open}} are still open."), x => x.Contains("without its base"));
        Assert.DoesNotContain(OverviewProblems("In {{PREV_MONTH}}, {{F:lm_due}} compliances fell due. {{F:lm_still_open}} of those {{F:lm_due}} are still open."), x => x.Contains("without its base"));
    }

    [Fact]
    public void ARunOfWordsIsNotRepeatedInAParagraph() =>
        Assert.Contains(OverviewProblems("In total, from all months, {{F:od_total}} compliances are overdue. {{F:od_liability}} of those {{F:od_total}} carry personal liability, a higher share than in other categories. {{F:od_over_90_days}} of those {{F:od_total}} are older, a higher share than in other categories."),
            x => x.Contains("repeats"));

    /// <summary>[FOUND 2026-10-13, Minda Overview] The last-month closing line, reworded onto the categories paragraph.</summary>
    [Fact]
    public void ABentLastMonthClosingLineIsSwappedForTheRightOne()
    {
        var fixedUp = FreeMonthlyDraftRepair.WithClosingLine(
            "3 of the 8 compliance categories have a higher overdue share than your other categories, including Secretarial (497 of its 659 overdue). These are the categories where overdue work was most often left unfinished.");
        Assert.DoesNotContain("most often left unfinished", fixedUp);
        Assert.EndsWith("Overdue work is gathered in these categories more than in the rest of your organisation.", fixedUp);

        const string lastMonth = "In {{PREV_MONTH}}, 9 of the 42 locations left a higher share of their work open than the other locations. These are the places where last month's work was most often left unfinished.";
        Assert.Equal(lastMonth, FreeMonthlyDraftRepair.WithClosingLine(lastMonth));
    }

    /// <summary>[OWNER, 2026-10-14] "very repetitive, like personal liability" - at most three paragraphs carry it.</summary>
    [Fact]
    public void PersonalLiabilityIsNotInEveryParagraph()
    {
        const string p = "{{F:od_liability}} of the {{F:od_total}} overdue compliances carry personal liability.";
        Assert.Contains(OverviewProblems($"In total, from all months, {p}\n\n{p}\n\n{p}\n\n{p}"), x => x.Contains("personal liability in 4 paragraphs"));
        Assert.DoesNotContain(OverviewProblems($"In total, from all months, {p}\n\n{p}\n\n{p}"), x => x.Contains("paragraphs - keep it to at most three"));
    }

    [Fact]
    public void AClosingLineIsNotUsedTwice() =>
        Assert.Contains(OverviewProblems(
                "In {{PREV_MONTH}}, {{F:lm_due}} compliances fell due. Each of these is now overdue and adds to your overdue total for as long as it stays open.\n\n"
                + "So far in {{CURR_MONTH}}, {{F:tm_open_past_due}} compliances are past due. Each of these is now overdue and adds to your overdue total for as long as it stays open."),
            x => x.Contains("same closing line twice"));

    /// <summary>
    /// [FOUND 2026-10-14, Minda Act - fell back] An example must never be asked for a comparison it is
    /// then forbidden to make: its own count, in its own words, passes.
    /// </summary>
    [Fact]
    public void AnExampleIsNeverRequiredToCarryItsPatternsComparison()
    {
        var p = FreeMonthlyDigestPrompt.Build(MonthlyExamples.LocationWithAggregateExamples());
        var problems = Problems(p, "Good morning,\n\nHere is your monthly update on compliance work across your locations. The figures below are as of {{AS_AT}}.\n\n"
            + "In total, from all months, at your {{EG_1}} site {{EG_1:item}} of its {{EG_1:base}} overdue compliances have been overdue for more than 90 days.");
        Assert.DoesNotContain(problems, x => x.Contains("{{EG_1:item}} without saying what it counts"));
    }

    /// <summary>[FOUND 2026-10-14, Minda Licence] A different period's opener in the same paragraph is kept; only an exact repeat goes.</summary>
    [Fact]
    public void ADifferentPeriodOpenerIsNeverStripped()
    {
        var body = FreeTierReaderTerms.WithoutRepeatedFigures(
            "In September, 2 licences reached their end date, and all of them have since been renewed. So far in October, 1 more licence has reached its end date.");
        Assert.Contains("So far in October, 1 more", body);

        var same = FreeTierReaderTerms.WithoutRepeatedFigures(
            "In total, from all months, 146 compliances are overdue. In total, from all months, 38 of the 146 are older than 90 days.");
        Assert.Contains("overdue. 38 of the 146", same);
    }

    /// <summary>[2026-10-14, Minda Users] "1 people are both performer and reviewer" - agreement fixed after binding.</summary>
    [Theory]
    [InlineData("1 people are both performer and reviewer.", "1 person is both performer and reviewer.")]
    [InlineData("**1 people** have open work.", "**1 person** has open work.")]
    [InlineData("21 people are both performer and reviewer.", "21 people are both performer and reviewer.")]
    public void OnePersonIsSingular(string input, string expected) =>
        Assert.Equal(expected, FreeTierReaderTerms.Apply(input));
}
