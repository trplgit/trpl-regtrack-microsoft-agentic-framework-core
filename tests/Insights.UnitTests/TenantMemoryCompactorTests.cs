using Insights.Domain;

namespace Insights.UnitTests;

public sealed class TenantMemoryCompactorTests
{
    private static string Entry(string date, string first, int detailLines) =>
        $"### {date} (last_30_days)\n- {first}\n" + string.Concat(Enumerable.Range(1, detailLines).Select(i => $"- detail {i} for {date}: {new string('x', 60)}\n"));

    private const string Keep = "### Keep\n- Baseline Aug 2026 (last_30_days): overdue 70.1%, 214 licences\n- Transport has led lapses every run since 2026-06-27\n";

    [Fact]
    public void SanitizeHeadings_TurnsTopLevelHeadingsIntoSubheadings_SoNoDimensionCanBeOverwritten()
    {
        var text = "- note\n## Act\n- fake act note\n# Title\n### 2026-09-28 (q2)\n- ok";

        var clean = TenantMemoryCompactor.SanitizeHeadings(text);

        Assert.DoesNotContain("\n## ", "\n" + clean);
        Assert.DoesNotContain("\n# ", "\n" + clean);
        Assert.Contains("### Act", clean);
        Assert.Contains("### 2026-09-28 (q2)", clean);

        // The file-level parser must see only the one real section.
        var file = TenantMemorySections.ReplaceSection("## Act\n- real act note\n", "Users", clean);
        Assert.Equal("- real act note", TenantMemorySections.ExtractSection(file, "Act"));
    }

    [Fact]
    public void Compact_UnderTheLimit_ReturnsTheTextUnchanged()
    {
        var text = Keep + Entry("2026-09-28", "Overdue up", 2);

        var result = TenantMemoryCompactor.Compact(text, 6000);

        Assert.NotNull(result);
        Assert.Equal(text.TrimEnd(), result!.Text);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Compact_OverTheLimit_KeepsTheKeepBlockAndNewestRunsWhole_AndOldRunsKeepTheirFirstPoint()
    {
        var text = Keep
            + Entry("2026-09-28", "Newest headline", 10)
            + Entry("2026-08-28", "Second headline", 10)
            + Entry("2026-07-28", "Oldest-but-one headline", 10)
            + Entry("2026-06-28", "Oldest headline", 10);

        var result = TenantMemoryCompactor.Compact(text, 2600);

        Assert.NotNull(result);
        Assert.True(result!.Changed);
        Assert.True(result.Text.Length <= 2600);
        // Keep block untouched, word for word.
        Assert.Contains(Keep.TrimEnd(), result.Text);
        // Newest two runs are complete.
        Assert.Contains("detail 10 for 2026-09-28", result.Text);
        Assert.Contains("detail 10 for 2026-08-28", result.Text);
        // Older runs keep their dated heading and their most important (first) point.
        Assert.Contains("### 2026-07-28 (last_30_days)", result.Text);
        Assert.Contains("Oldest-but-one headline", result.Text);
        Assert.DoesNotContain("detail 1 for 2026-07-28", result.Text);
    }

    [Fact]
    public void Compact_WhenShorteningIsNotEnough_DropsTheOldestRunsFirst_AndSaysSo()
    {
        var text = Keep + string.Concat(Enumerable.Range(0, 40).Select(i =>
            Entry(new DateOnly(2026, 9, 28).AddMonths(-i).ToString("yyyy-MM-dd"), $"Headline {i}", 3)));

        var result = TenantMemoryCompactor.Compact(text, 2000);

        Assert.NotNull(result);
        Assert.True(result!.Text.Length <= 2000);
        Assert.Contains(Keep.TrimEnd(), result.Text);
        Assert.Contains("Headline 0", result.Text);                 // newest survives
        Assert.DoesNotContain("Headline 39", result.Text);          // oldest went first
        Assert.Matches(@"older run\(s\) removed", result.Text);
    }

    [Fact]
    public void Compact_KeepBlockAloneTooBig_Refuses_SoTheModelDecidesWhatToCut()
    {
        var hugeKeep = "### Keep\n" + string.Concat(Enumerable.Range(0, 80).Select(i => $"- must keep fact {i} {new string('y', 40)}\n"));

        var result = TenantMemoryCompactor.Compact(hugeKeep + Entry("2026-09-28", "x", 1), 3000);

        Assert.Null(result.Text);
        Assert.Equal(TenantMemoryCompactor.KeepTooLarge, result.Refusal);
    }

    [Fact]
    public void Compact_OneHugeLineThatCannotBeShortened_RefusesAsEntryTooLarge_NotKeep()
    {
        var result = TenantMemoryCompactor.Compact(new string('x', 6001), 6000);

        Assert.Null(result.Text);
        Assert.Equal(TenantMemoryCompactor.EntryTooLarge, result.Refusal);
    }

    [Fact]
    public void PlanSummary_HandsOverOnlyTheOlderRuns_KeepAndTwoNewestStayOut()
    {
        var text = Keep
            + Entry("2026-06-28", "Oldest headline", 10)
            + Entry("2026-09-28", "Newest headline", 10)
            + Entry("2026-08-28", "Second headline", 10)
            + Entry("2026-07-28", "Third headline", 10);

        var plan = TenantMemoryCompactor.PlanSummary(text, 2600);

        Assert.NotNull(plan);
        Assert.Contains("Oldest headline", plan!.OlderEntries);
        Assert.Contains("Third headline", plan.OlderEntries);
        Assert.DoesNotContain("Newest headline", plan.OlderEntries);
        Assert.DoesNotContain("Second headline", plan.OlderEntries);
        Assert.DoesNotContain("Baseline Aug 2026", plan.OlderEntries);
        Assert.Equal(2, plan.OlderCount);
        Assert.True(plan.SummaryBudget > 200);

        var summary = "### Summary of older runs (2026-06-28 – 2026-07-28)\n- 2026-06-28, 2026-07-28: Oldest headline; Third headline";
        Assert.True(TenantMemoryCompactor.IsValidSummary(summary, plan.SummaryBudget));

        var assembled = TenantMemoryCompactor.Assemble(plan, summary);
        Assert.True(assembled.Length <= 2600);
        Assert.StartsWith("### Keep", assembled);
        Assert.Contains(Keep.TrimEnd(), assembled);
        Assert.Contains("detail 10 for 2026-09-28", assembled);
        Assert.Contains("detail 10 for 2026-08-28", assembled);
        Assert.True(assembled.IndexOf("2026-08-28", StringComparison.Ordinal) < assembled.IndexOf("Summary of older runs", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanSummary_UnderTheLimitOrNothingOld_ReturnsNull()
    {
        Assert.Null(TenantMemoryCompactor.PlanSummary(Keep + Entry("2026-09-28", "x", 1), 6000));
        Assert.Null(TenantMemoryCompactor.PlanSummary(Keep + Entry("2026-09-28", "x", 60) + Entry("2026-08-28", "y", 60), 2000));
    }

    [Theory]
    [InlineData("- no heading")]
    [InlineData("### 2026-09-28 (q2)\n- wrong heading")]
    [InlineData("### Summary of older runs (2026-06-28 – 2026-07-28)\n## Act\n- sneaks in a section")]
    [InlineData("")]
    public void IsValidSummary_RejectsAnythingThatIsNotOneSafeSummaryBlock(string summary)
    {
        Assert.False(TenantMemoryCompactor.IsValidSummary(summary, 5000));
    }

    [Fact]
    public void IsValidSummary_RejectsOverBudget()
    {
        var summary = "### Summary of older runs (2026-06-28 – 2026-07-28)\n- " + new string('s', 500);
        Assert.False(TenantMemoryCompactor.IsValidSummary(summary, 300));
    }

    [Fact]
    public void Compact_OrdersRunsByDate_NotByPositionInTheText()
    {
        var text = Keep
            + Entry("2026-06-28", "Oldest headline", 10)
            + Entry("2026-09-28", "Newest headline", 10)
            + Entry("2026-08-28", "Second headline", 10);

        var result = TenantMemoryCompactor.Compact(text, 2200);

        Assert.NotNull(result);
        Assert.Contains("detail 10 for 2026-09-28", result!.Text);
        Assert.DoesNotContain("detail 1 for 2026-06-28", result.Text);
        Assert.True(result.Text.IndexOf("2026-09-28", StringComparison.Ordinal) < result.Text.IndexOf("2026-06-28", StringComparison.Ordinal));
    }
}
