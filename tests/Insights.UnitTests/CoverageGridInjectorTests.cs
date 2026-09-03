using Insights.Domain;
using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// [BUG FOUND LIVE, 2026-09-02] The render agent was asked to hand-author one &lt;button&gt; tile
/// per real leaf branch (up to 177 for tenant 29) - confirmed live it silently drew a small SAMPLE
/// (10 of 177) while still stating the true full counts in the chip/legend/KPI text, exactly the
/// same class of problem as the font and the driving script: a large, mechanical, per-row
/// generation task is not something to gamble on an LLM getting complete every time. Same fix -
/// generate it deterministically, post-generation, from the real LocationRows. The render agent's
/// job for the grid becomes a single empty `&lt;div id="di-covgrid-root"&gt;&lt;/div&gt;` placeholder.
/// </summary>
public sealed class CoverageGridInjectorTests
{
    private const string DocumentWithPlaceholder =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" +
        "<div class=\"di-covmap\"><div id=\"di-covgrid-root\"></div></div>" +
        "</body></html>";

    private static LocationRow Leaf(int id, string name, string state, int instances, int overdue, int ownerless, int performers, string flags = "") =>
        new()
        {
            BranchID = id, BranchName = name, StateName = state, NodeType = EntityNodeType.Leaf,
            Instances = instances, Overdue = overdue, Ownerless = ownerless, DistinctPerformers = performers, Flags = flags,
        };

    [Fact]
    public void Inject_OneTilePerLeafRow_NeverSampled()
    {
        var rows = Enumerable.Range(1, 177).Select(i => Leaf(i, $"Branch {i}", "Maharashtra", 10, 2, 0, 1)).ToList();

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        Assert.Equal(177, System.Text.RegularExpressions.Regex.Matches(result, "di-covtile di-covtile--").Count);
    }

    [Fact]
    public void Inject_ExcludesIntermediateNodes_LeafOnly()
    {
        var rows = new List<LocationRow>
        {
            Leaf(1, "Leaf A", "Haryana", 5, 0, 0, 1),
            new() { BranchID = 2, BranchName = "Rollup", StateName = "Haryana", NodeType = EntityNodeType.Intermediate, Instances = 500, DistinctPerformers = 1 },
        };

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "di-covtile di-covtile--"));
        Assert.DoesNotContain("Rollup", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_ClassifiesEachTileFromRealFlags()
    {
        var rows = new List<LocationRow>
        {
            Leaf(1, "Mapped Store", "Haryana", 10, 0, 0, 1, flags: ""),
            Leaf(2, "Ownerless Store", "Haryana", 10, 0, 4, 1, flags: "high_ownerless"),
            Leaf(3, "Unmapped Store", "Haryana", 0, 0, 0, 0, flags: "no_obligations_configured"),
        };

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        Assert.Contains("di-covtile di-covtile--healthy", result, StringComparison.Ordinal);
        Assert.Contains("di-covtile di-covtile--has_ownerless", result, StringComparison.Ordinal);
        Assert.Contains("di-covtile di-covtile--unmapped", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_GroupsByStateDescendingByCount_MostBranchesFirst()
    {
        var rows = new List<LocationRow>
        {
            Leaf(1, "A", "SmallState", 1, 0, 0, 1),
            Leaf(2, "B", "BigState", 1, 0, 0, 1),
            Leaf(3, "C", "BigState", 1, 0, 0, 1),
        };

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        var bigIdx = result.IndexOf("BigState", StringComparison.Ordinal);
        var smallIdx = result.IndexOf("SmallState", StringComparison.Ordinal);
        Assert.True(bigIdx >= 0 && smallIdx >= 0 && bigIdx < smallIdx, "the region with more branches should render first");
    }

    [Fact]
    public void Inject_NullStateName_GroupsAsUnassignedState()
    {
        var rows = new List<LocationRow> { Leaf(1, "A", null!, 1, 0, 0, 1) };

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        Assert.Contains("Unassigned state", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_EscapesBranchNameForHtml()
    {
        var rows = new List<LocationRow> { Leaf(1, "Bob's <Shop>", "Haryana", 1, 0, 0, 1) };

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        Assert.DoesNotContain("<Shop>", result, StringComparison.Ordinal);
        Assert.Contains("&lt;Shop&gt;", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_NoPlaceholderInDocument_LeavesDocumentUnchanged()
    {
        const string noPlaceholder = "<!DOCTYPE html><html><body>no coverage pane here</body></html>";
        var rows = new List<LocationRow> { Leaf(1, "A", "Haryana", 1, 0, 0, 1) };

        var result = CoverageGridInjector.Inject(noPlaceholder, rows);

        Assert.Equal(noPlaceholder, result);
    }

    [Fact]
    public void Inject_NullOrEmptyLocationRows_LeavesDocumentUnchanged()
    {
        Assert.Equal(DocumentWithPlaceholder, CoverageGridInjector.Inject(DocumentWithPlaceholder, null));
        Assert.Equal(DocumentWithPlaceholder, CoverageGridInjector.Inject(DocumentWithPlaceholder, []));
    }

    [Fact]
    public void Inject_NeverAssignsUnderConfigured_NoRealPeerNormExistsYet()
    {
        var rows = Enumerable.Range(1, 20).Select(i => Leaf(i, $"B{i}", "Haryana", 10, 5, 0, 1)).ToList();

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        Assert.DoesNotContain("di-covtile--under_configured", result, StringComparison.Ordinal);
    }
}
