using Insights.Domain;
using Insights.Presentation;

namespace Insights.UnitTests;

public sealed class ReportNumberTracerTests
{
    private const string Rows = """
        [{"ActID":1,"ActName":"Factories Act, 1948","Instances":20,"Overdue":15,"Band":"high"},
         {"ActID":2,"ActName":"Gratuity Act","Instances":13,"Overdue":13,"Band":"high"},
         {"ActID":3,"ActName":"Shops Act","Instances":7,"Overdue":0,"Band":"low"}]
        """;
    private const string Totals = """{"ScopedInstances":40,"OverdueInstances":28,"TenantOverduePct":70.0}""";

    private static string Page(string body) => $"<!DOCTYPE html><html><body>{body}</body></html>";

    [Fact]
    public void RealValuesAndPlainDerivations_AllTrace()
    {
        var html = Page("""
            <p>28 of 40 overdue (70%). Gratuity Act: 13 of 13 (100%). Factories Act: 15 of 20, 75.0%.</p>
            <p>Factories Act runs 5 points above the whole estate. 2 of 3 laws are high band, carrying 33 obligations.</p>
            <p>Generated 27 Sep 2026 at 09:34 UTC. Median law load 13.</p>
            """);

        Assert.Empty(ReportNumberTracer.FindUntraced(html, Rows, Totals, [], []));
    }

    [Fact]
    public void AnInventedCount_IsCaught()
    {
        var html = Page("<p>28 of 40 overdue, with 1,640 penalties raised.</p>");

        Assert.Equal(["1,640"], ReportNumberTracer.FindUntraced(html, Rows, Totals, [], []));
    }

    [Fact]
    public void AnInventedPercentage_IsCaught()
    {
        var html = Page("<p>Overdue rate 63.7%.</p>");

        Assert.Equal(["63.7%"], ReportNumberTracer.FindUntraced(html, Rows, Totals, [], []));
    }

    [Fact]
    public void ANumberOnlyInTheVerifiedNarrative_Traces()
    {
        var html = Page("<p>Filed 412 returns this year.</p>");

        Assert.Empty(ReportNumberTracer.FindUntraced(html, Rows, Totals, [], ["The team filed 412 returns this year."]));
    }

    [Fact]
    public void AssertionValuesRanksAndGaps_Trace()
    {
        var assertion = new Assertion("A-WORST", "overdue_pct", "Gratuity Act", 100m, 1, 3, 70.0m, 30.0m, AssertionDirection.Worse, null);
        var html = Page("<p>Gratuity Act ranks 1 of 3, 30 points above the 70% average.</p>");

        Assert.Empty(ReportNumberTracer.FindUntraced(html, Rows, Totals, [assertion], []));
    }

    [Fact]
    public void PaddedZeros_AreJudgedAtTheRealPrecision()
    {
        // 2 of 3 = 66.666...; "66.70%" is 66.7 padded, not a claim to two-decimal precision.
        var html = Page("<p>66.70% of laws are high band.</p>");

        Assert.Empty(ReportNumberTracer.FindUntraced(html, Rows, Totals, [], []));
    }

    [Fact]
    public void NumbersInsideNamesAndScripts_AreNotClaims()
    {
        var html = Page("""<p>Factories Act, 1948 leads.</p><script>const fake = 98765;</script>""");

        Assert.Empty(ReportNumberTracer.FindUntraced(html, Rows, Totals, [], []));
    }

    // [ADDED 2026-10-09] Three false alarms found live on prod tenant 1271 (Location + Departments
    // refused 3 runs in a row, every attempt). Each was a real, correct number the check could not
    // place - never an invented one. Fixtures below are the same shapes, trimmed.

    [Fact]
    public void TheTimes100MultiplierInAFormulaPopup_IsNotAClaim()
    {
        // Location 1271: <span class="pf-times">&times; 100</span> in a model-written "How it is
        // calculated" popup. No row or total equals 100 on that page, so "100" was flagged.
        const string rows = """[{"BranchID":1,"BranchName":"FDF-3","Instances":927,"Overdue":669}]""";
        const string totals = """{"ScopedInstances":927,"OverdueInstances":669,"TenantOverduePct":72.2}""";
        var html = Page("""
            <p>72.2% overdue</p>
            <span class="pf-frac"><span class="pf-num-value">669</span><span class="pf-den-value">927</span>
            <span class="pf-times">&times; 100</span></span>
            """);

        Assert.Empty(ReportNumberTracer.FindUntraced(html, rows, totals, [], []));
    }

    [Fact]
    public void AnInvented100_OutsideAMultiplier_IsStillCaught()
    {
        const string rows = """[{"BranchID":1,"BranchName":"FDF-3","Instances":927,"Overdue":669}]""";
        const string totals = """{"ScopedInstances":927,"OverdueInstances":669,"TenantOverduePct":72.2}""";
        var html = Page("<p>100 branches were inspected.</p>");

        Assert.Equal(["100"], ReportNumberTracer.FindUntraced(html, rows, totals, [], []));
    }

    [Fact]
    public void ARowsTotalPlusItsResidual_Traces()
    {
        // Departments 1271: "33 unassigned of 7,235 compliances counted". 7,235 = AssignedInstances
        // (7,202) + UnassignedInstances (33) - a plain sum of two totals, never a stored field.
        const string rows = """
            [{"DepartmentID":1,"DepartmentName":"HR","Instances":5000,"Overdue":2000},
             {"DepartmentID":2,"DepartmentName":"Finance","Instances":2202,"Overdue":847}]
            """;
        const string totals = """{"ScopedInstances":3593,"AssignedInstances":7202,"UnassignedInstances":33,"OverdueInstances":2847,"UnassignedPct":0.5}""";
        var html = Page("<p>33 unassigned of 7,235 compliances counted (0.5%).</p>");

        Assert.Empty(ReportNumberTracer.FindUntraced(html, rows, totals, [], []));
    }

    [Fact]
    public void AnInventedCountNearTheTotals_IsStillCaught()
    {
        const string rows = """
            [{"DepartmentID":1,"DepartmentName":"HR","Instances":5000,"Overdue":2000},
             {"DepartmentID":2,"DepartmentName":"Finance","Instances":2202,"Overdue":847}]
            """;
        const string totals = """{"ScopedInstances":3593,"AssignedInstances":7202,"UnassignedInstances":33,"OverdueInstances":2847,"UnassignedPct":0.5}""";
        var html = Page("<p>7,281 compliances counted.</p>");

        Assert.Equal(["7,281"], ReportNumberTracer.FindUntraced(html, rows, totals, [], []));
    }

    [Fact]
    public void ScaleMarksInAHowToReadIllustration_AreNotClaims()
    {
        // Departments 1271: the "How to read this chart" panel's mini illustration (.hr-viz, defined
        // by the render prompts as "optional mini inline-SVG illustration") drew a sample log scale
        // "1 / 10 / 100 / 1,000" - teaching the reader, not a data claim.
        var html = Page("""
            <aside class="hr-panel" aria-label="How to read this chart"><div class="hr-row">
              <div class="hr-viz"><svg viewBox="0 0 200 72"><text x="20">1</text><text x="70">10</text>
                <text x="120">100</text><text x="170">1,000+</text></svg></div>
              <span>Each step to the right is ten times more work.</span></div></aside>
            <p>28 of 40 overdue.</p>
            """);

        Assert.Empty(ReportNumberTracer.FindUntraced(html, Rows, Totals, [], []));
    }

    [Fact]
    public void EvenlySpacedAxisTicks_AreNotClaims()
    {
        // Departments 1271 (after the first fix): a model-typed axis "0 256 512 768 1,024" - equal
        // steps from zero to the axis end. The steps between 0 and the end are scale marks.
        var html = Page("""
            <div class="axis"><span>Department</span><div class="axis-track"><span>0</span><span>256</span>
            <span>512</span><span>768</span><span>1,024</span></div></div><p>28 of 40 overdue.</p>
            """);

        Assert.Equal(["1,024"], ReportNumberTracer.FindUntraced(html, Rows, Totals, [], []));
    }

    [Fact]
    public void ARunThatIsNotEvenlySpaced_IsStillChecked()
    {
        var html = Page("<p>0</p><p>256</p><p>530</p><p>768</p>");

        Assert.Equal(["256", "530", "768"], ReportNumberTracer.FindUntraced(html, Rows, Totals, [], []));
    }

    [Fact]
    public void AnInventedNumberInTheHowToReadText_IsStillCaught()
    {
        // Only the illustration is exempt - the panel's written explanation is still checked.
        var html = Page("""
            <aside class="hr-panel"><div class="hr-row"><div class="hr-viz"><svg><text>1,000</text></svg></div>
              <span>The biggest department carries 1,640 compliances.</span></div></aside>
            """);

        Assert.Equal(["1,640"], ReportNumberTracer.FindUntraced(html, Rows, Totals, [], []));
    }
}
