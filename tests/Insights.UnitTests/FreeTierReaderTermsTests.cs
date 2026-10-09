using Insights.Agents;
using Xunit;

namespace Insights.UnitTests;

/// <summary>[OWNER, 2026-10-07] The reader's word is "compliance", never "obligation".</summary>
public class FreeTierReaderTermsTests
{
    [Theory]
    [InlineData("1,913 obligations fell due in August", "1,913 compliances fell due in August")]
    [InlineData("Obligations still open from last month", "Compliances still open from last month")]
    [InlineData("1 obligation is overdue", "1 compliance is overdue")]
    [InlineData("an obligation with no reviewer", "a compliance with no reviewer")]
    [InlineData("An obligation was missed.", "A compliance was missed.")]
    [InlineData("281 obligations in your scope; 3 overdue obligations", "281 compliances in your scope. 3 overdue compliances")]
    [InlineData("No change here.", "No change here.")]
    [InlineData("**No other licence type shares this position.**.", "**No other licence type shares this position.**")]
    [InlineData("Where it sits - personal exposure", "Where it sits - personal liability")]
    public void RenamesObligationToCompliance(string input, string expected) =>
        Assert.Equal(expected, FreeTierReaderTerms.Apply(input));

    [Fact]
    public void LeavesNullAndEmptyAlone()
    {
        Assert.Equal(string.Empty, FreeTierReaderTerms.Apply(null));
        Assert.Equal(string.Empty, FreeTierReaderTerms.Apply(string.Empty));
    }

    // [OWNER, 2026-10-08] Commas in every count; years, dates and Act names untouched.
    [Theory]
    [InlineData("**2211 of the 14650 overdue compliances** are held", "**2,211 of the 14,650 overdue compliances** are held")]
    [InlineData("Before the end of October, **1179 compliances fall due**", "Before the end of October, **1,179 compliances fall due**")]
    [InlineData("In September, 2047 compliances fell due.", "In September, 2,047 compliances fell due.")]
    [InlineData("The figures below are as of 8 Oct 2026.", "The figures below are as of 8 Oct 2026.")]
    [InlineData("Under Factories Act, 1948 & Maharashtra Factories Rules, 1963, 1259 remain open.", "Under Factories Act, 1948 & Maharashtra Factories Rules, 1963, 1,259 remain open.")]
    [InlineData("Companies Act 2013 has 1,234 open", "Companies Act 2013 has 1,234 open")]
    [InlineData("due on 2026-10-08 or 08/10/2026", "due on 2026-10-08 or 08/10/2026")]
    [InlineData("Plant 5000 holds 12345 compliances", "Plant 5,000 holds 12,345 compliances")]
    public void AddsThousandsCommas(string input, string expected) =>
        Assert.Equal(expected, FreeTierReaderTerms.Apply(input));

    [Theory]
    [InlineData("15 people have an unusually large share of their work open", "15 people have a high share of their work open")]
    [InlineData("An unusually high share of overdue work", "A high share of overdue work")]
    public void RewordsTheProcsComparisonLabel(string input, string expected) =>
        Assert.Equal(expected, FreeTierReaderTerms.Apply(input));

    [Theory]
    [InlineData("Of the 614 compliances still open from September.", true)]
    [InlineData("Among the 15 people with work still open from September.", true)]
    [InlineData("Of the 2,047 compliances that fell due in September, 614 remain open.", false)]
    [InlineData("79 locations in your organisation.", false)]   // the fallback's own fact line
    [InlineData("Including 192 with personal liability for the responsible officer.", true)]
    [InlineData("Good morning,", false)]
    public void FlagsAFigureSentenceWithNoVerb(string sentence, bool broken) =>
        Assert.Equal(broken, FreeMonthlyDigestValidator.FragmentProblems(sentence).Any());

    [Theory]
    [InlineData("The total includes licences that fell due in earlier months and remain overdue.", true)]
    [InlineData("In addition, 2 licences expired during September and remain overdue.", true)]
    [InlineData("Currently, 19 licences are expired with no renewal in progress.", false)]
    [InlineData("14,647 compliances are overdue, and 19 licences are expired.", false)]
    public void FlagsLicencesCalledOverdue(string sentence, bool wrong) =>
        Assert.Equal(wrong, FreeMonthlyDigestValidator.LicenceOverdueProblems(sentence).Any());

    [Fact]
    public void DropsASentenceThatOnlyRepeatsThePreviousOnesFigures()
    {
        var body = "**Vipin Mishra** holds **2,211 of the 14,628 overdue compliances** or 15%. 15% of the **overdue** work is held by **Vipin Mishra**. Among 17 people, 16 others are in the same situation.\n\nNext paragraph 15% stays.";
        Assert.Equal(
            "**Vipin Mishra** holds **2,211 of the 14,628 overdue compliances** or 15%. Among 17 people, 16 others are in the same situation.\n\nNext paragraph 15% stays.",
            FreeTierReaderTerms.WithoutRepeatedFigures(body));
    }

    [Fact]
    public void KeepsSentencesThatAddAFigureOrHaveNone()
    {
        var body = "Here is your update. The figures below are as of 8 Oct 2026.\n\n19 licences are expired. Until a licence is renewed, there is no valid licence on record.";
        Assert.Equal(body, FreeTierReaderTerms.WithoutRepeatedFigures(body));
    }

    [Theory]
    [InlineData("MVASPL holds **2,240 overdue compliances**; 9 other locations are flagged.", "MVASPL holds **2,240 overdue compliances**. 9 other locations are flagged.")]
    [InlineData("It is open; **critical** work stays.", "It is open. **Critical** work stays.")]
    public void SplitsASemicolonIntoTwoSentences(string input, string expected) =>
        Assert.Equal(expected, FreeTierReaderTerms.Apply(input));

    [Theory]
    [InlineData("Across your organisation, 37% of all overdue compliances sit at the locations holding the most, including your MVASPL site with 2,240 overdue compliances; 9 other locations share that level of overdue work.", true)]
    [InlineData("Your overdue work is concentrated across the 3 locations holding the most.", true)]
    [InlineData("Your 3 locations with the most overdue compliances hold 37% of all 14,618 overdue compliances.", false)]
    public void BansTheLabelWordsForTheTopThreeShare(string sentence, bool banned) =>
        Assert.Equal(banned, new[] { "holding the most", "that level", "is concentrated" }.Any(p => sentence.Contains(p, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void SaysTheAllMonthsOpenerOncePerEmail()
    {
        var body = "In total, from all months, **14,618** compliances are overdue. In total, from all months, 10,396 of the 14,618 are overdue for more than 90 days. In total, from all months, **52 locations** have such work.\n\nIn total, from all months, 11 locations rest with one person.";
        Assert.Equal(
            "In total, from all months, **14,618** compliances are overdue. 10,396 of the 14,618 are overdue for more than 90 days. **52 locations** have such work.\n\n11 locations rest with one person.",
            FreeTierReaderTerms.WithoutRepeatedFigures(body));
    }

    /// <summary>[FOUND LIVE 2026-10-08] The repair's "same shape" filter deleted the 3rd and 4th named sites.</summary>
    [Fact]
    public void RepairKeepsASentenceAboutADifferentNamedThing()
    {
        var body = "Good morning,\n\nHere is your update. The figures below are as of {{AS_AT}}.\n\nAt your {{NAME_1}} site, 416 of its 595 overdue obligations carry personal criminal liability for the responsible officer, or 69%, compared with 35% across your organisation.\n\nAt your {{NAME_3}} site, 150 of its 273 overdue obligations carry personal liability, or 54%, compared with 35% across your organisation. At your {{NAME_4}} site, 111 of its 187 overdue obligations carry personal liability, or 59%, compared with 35% across your organisation.";
        var repaired = FreeMonthlyDraftRepair.Apply(body);
        Assert.Contains("{{NAME_3}}", repaired.Body);
        Assert.Contains("{{NAME_4}}", repaired.Body);
        Assert.DoesNotContain(repaired.Removed, r => r.Contains("repeats an earlier sentence"));
    }
}

public class ExampleUnitTests
{
    /// <summary>[FOUND LIVE 2026-10-09] A slippage example (compliances) written as "sites where it applies".</summary>
    [Fact]
    public void ASlippageExampleIsNeverWrittenAsSites()
    {
        var prompt = FreeMonthlyDigestPrompt.Build(MonthlyExamples.ByName("location"));
        var eg = prompt.Examples.FirstOrDefault(e => !e.Example.PatternFactKey.Contains("multi_location", StringComparison.Ordinal))?.Placeholder
                 ?? prompt.NamedFindings.First(n => n.NamePlaceholder is not null && !n.Candidate.Detector.Contains("multi_location", StringComparison.Ordinal)).NamePlaceholder!;
        var body = "Good morning,\n\nHere is your update. The figures below are as of {{AS_AT}}.\n\nUnder " + eg + ", 21 of its 22 sites where it applies have it overdue. This work was due last month and is not finished.";
        var review = FreeMonthlyDigestValidator.Validate(body, prompt);
        Assert.Contains(review.FailedChecks, f => f.Contains("counts COMPLIANCES, not sites"));
    }
}
