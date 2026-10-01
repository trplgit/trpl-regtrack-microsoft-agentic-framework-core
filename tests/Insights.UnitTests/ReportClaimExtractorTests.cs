using Insights.Presentation;

namespace Insights.UnitTests;

public sealed class ReportClaimExtractorTests
{
    [Fact]
    public void VisibleText_DropsScriptsStylesAndTheDataBlock_KeepsWhatAReaderSees()
    {
        const string html = "<html><head><style>.a{width:12px}</style></head><body><h1>Overdue work</h1>" +
            "<p>28 of 33 obligations are overdue &amp; late.</p>" +
            "<script type=\"application/json\" id=\"insights-data\">{\"rows\":[{\"Instances\":999}]}</script>" +
            "<script>var x = 777;</script><li>84.8% overall</li></body></html>";

        var text = ReportClaimExtractor.VisibleText(html);

        Assert.Contains("Overdue work", text);
        Assert.Contains("28 of 33 obligations are overdue & late.", text);
        Assert.Contains("84.8% overall", text);
        Assert.DoesNotContain("999", text);
        Assert.DoesNotContain("777", text);
        Assert.DoesNotContain("12px", text);
    }

    [Fact]
    public void ExtractNumbers_FindsCountsPercentagesAndThousands_SkipsDatesAndYears()
    {
        const string text = "Prepared 27 Sep 2026. Period 29 Aug 2026 to 2026-09-28. 28 of 33 overdue (84.8%). " +
            "21,751 assigned. Logins 12m. Rate 84.8% again. FY2026 plan. Median 25.5.";

        var numbers = ReportClaimExtractor.ExtractNumbers(text);

        Assert.Equal(["28", "33", "84.8%", "21,751", "25.5"], numbers);
    }

    [Fact]
    public void ExtractNumbers_SkipsOldYearsAndNumbersThatArePartOfANameOrAnId()
    {
        const string text = "Essential Commodities Act, 1955 has 2 overdue. Act as on dated 27082026 has 1. Law 2812 row. 14 laws.";
        var rowsJson = """[{"ActID":2812,"ActName":"Essential Commodities Act, 1955"},{"ActID":73,"ActName":"Act as on dated 27082026"}]""";

        var numbers = ReportClaimExtractor.ExtractNumbers(text, ReportClaimExtractor.NumbersToIgnore(rowsJson));

        Assert.Equal(["2", "1", "14"], numbers);
    }

    [Fact]
    public void FindUnexplained_MatchesByValue_NotByFormatting()
    {
        IReadOnlyList<string> numbers = ["84.8%", "21,751", "28", "15.2", "7"];
        const string markdown = "Overdue rate = 28 / 33 x 100 = 84.80%. Assigned: 21751. Gap 100.0 - 84.8 = 15.20 points.";

        var missing = ReportClaimExtractor.FindUnexplained(numbers, markdown);

        Assert.Equal(["7"], missing);
    }

    // [ADDED 2026-10-01] fixed_holistic (Entity) shape: several dimensions' own {Rows, ControlTotals}
    // JSON objects, keyed by dimension name, instead of one dimension's bare rows array.
    [Fact]
    public void NumbersToIgnore_DictionaryOverload_IgnoresIdsAcrossEveryContributingDimension()
    {
        const string text = "Branch 23156 has 9 obligations. Act 2812 row. Odisha has 9.";
        var allDimensionData = new Dictionary<string, string>
        {
            ["Location"] = """{"Rows":[{"BranchID":23156,"BranchName":"Odisha"}],"ControlTotals":{}}""",
            ["Act"] = """{"Rows":[{"ActID":2812,"ActName":"Azure Act"}],"ControlTotals":{}}""",
        };

        var numbers = ReportClaimExtractor.ExtractNumbers(text, ReportClaimExtractor.NumbersToIgnore(allDimensionData));

        Assert.Equal(["9"], numbers);
    }

    [Fact]
    public void NumbersToIgnore_DictionaryOverload_NullOrEmpty_IgnoresNothing()
    {
        Assert.Empty(ReportClaimExtractor.NumbersToIgnore((IReadOnlyDictionary<string, string>?)null));
        Assert.Empty(ReportClaimExtractor.NumbersToIgnore(new Dictionary<string, string>()));
    }
}
