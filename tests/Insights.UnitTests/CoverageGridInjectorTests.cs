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
    // [FIX - stale fixture, found live 2026-09-09] Inject() was narrowed on 2026-09-07 to replace
    // the WHOLE pane body via a `<section id="di-pane-3">...</section>` match (see PaneOpenToken/
    // PaneContentToken below) - this fixture still used the pre-2026-09-07 shape (a bare
    // `di-covgrid-root` div with no `di-pane-3` wrapper at all), so PaneOpenToken never matched and
    // every test below silently exercised the "nothing to do" early-return, not real Inject
    // behaviour. All these tests were passing for the wrong reason since the narrowing shipped.
    private const string DocumentWithPlaceholder =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" +
        "<section class=\"di-pane\" id=\"di-pane-3\" aria-label=\"Coverage\"><div id=\"di-pane-3-body\"></div></section>" +
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

    // [FIX - found live 2026-09-09, matched against the real product's own Coverage KPI card]
    // The card was showing STORE counts under "Mapped"/"Has ownerless" labels that don't match
    // what the real product shows there - "Stores mapped" is (leaf total - unmapped), not the
    // stricter "Healthy" (zero-flags) count, and "Ownerless obligations" is a real OBLIGATION
    // count (sum of each row's real Ownerless field) split leaf vs corporate-rollup, not a store
    // count at all. Both numbers were already being computed correctly elsewhere in this same
    // document (chip/legend text) - only the KPI card's own pairs had the wrong figures under
    // right-sounding labels.
    [Fact]
    public void Inject_KpiCard_StoresMappedPair_IsTotalLeafMinusUnmapped_WithRealPct()
    {
        var rows = new List<LocationRow>
        {
            Leaf(1, "A", "Haryana", 10, 0, 0, 1, flags: ""),
            Leaf(2, "B", "Haryana", 10, 0, 0, 1, flags: "high_ownerless"),
            Leaf(3, "C", "Haryana", 0, 0, 0, 0, flags: "no_obligations_configured"),
            Leaf(4, "D", "Haryana", 10, 0, 0, 1, flags: ""),
        };

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        Assert.Contains("Stores mapped", result, StringComparison.Ordinal);
        Assert.Contains("di-kpi__pair-val tnum\">3<", result, StringComparison.Ordinal); // 4 leaf - 1 unmapped = 3
        Assert.Contains("75.0% of 4 leaf stores", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_KpiCard_OwnerlessObligationsPair_SumsRealFieldNotStoreCount_SplitsLeafVsRollup()
    {
        var rows = new List<LocationRow>
        {
            Leaf(1, "A", "Haryana", 10, 0, 3, 1, flags: "high_ownerless"),
            Leaf(2, "B", "Haryana", 10, 0, 4, 1, flags: "high_ownerless"),
            new() { BranchID = 3, BranchName = "Rollup", StateName = "Haryana", NodeType = EntityNodeType.Intermediate, Instances = 500, Ownerless = 6, DistinctPerformers = 1 },
        };

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        Assert.Contains("Ownerless obligations", result, StringComparison.Ordinal);
        Assert.Contains("di-kpi__pair-val tnum\">13<", result, StringComparison.Ordinal); // 3 + 4 leaf + 6 rollup = 13
        Assert.Contains("7 on leaf stores", result, StringComparison.Ordinal);
        Assert.Contains("6 on corporate rollup", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_KpiCard_NoRollupOwnerless_OmitsRollupClauseEntirely_NeverShowsZero()
    {
        var rows = new List<LocationRow> { Leaf(1, "A", "Haryana", 10, 0, 3, 1, flags: "high_ownerless") };

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        Assert.DoesNotContain("corporate rollup", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_KpiCard_OmitsPeerCoverageGapsPair_WhenUnderConfiguredIsZero()
    {
        var rows = new List<LocationRow> { Leaf(1, "A", "Haryana", 10, 0, 0, 1) };

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        Assert.DoesNotContain("Peer-coverage gaps", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_KpiCard_IncludesDescriptionParagraph_MentioningRollupOwnerlessWhenPresent()
    {
        var rows = new List<LocationRow>
        {
            Leaf(1, "A", "Haryana", 10, 0, 0, 1),
            new() { BranchID = 2, BranchName = "Rollup", StateName = "Haryana", NodeType = EntityNodeType.Intermediate, Instances = 500, Ownerless = 6, DistinctPerformers = 1 },
        };

        var result = CoverageGridInjector.Inject(DocumentWithPlaceholder, rows);

        Assert.Contains("one box per location", result, StringComparison.Ordinal);
        Assert.Contains("is not a leaf store", result, StringComparison.Ordinal);
        Assert.Contains("6", result, StringComparison.Ordinal);
    }
}
