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
}
