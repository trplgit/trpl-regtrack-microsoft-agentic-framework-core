using Insights.Agents;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// StripMarkdownFence in isolation - confirmed necessary via a live run (2026-08-20) where
/// GPT-5.2 wrapped a fully correct document in a ```html fence unprompted.
/// </summary>
public sealed class ReportHtmlAgentTests
{
    [Fact]
    public void StripMarkdownFence_RemovesHtmlFencedWrapper()
    {
        const string document = "<!DOCTYPE html><html><body>content</body></html>";
        var fenced = "```html\n" + document + "\n```";

        var result = MafReportHtmlAgent.StripMarkdownFence(fenced);

        Assert.Equal(document, result);
    }

    [Fact]
    public void StripMarkdownFence_RemovesBareFencedWrapper_WithNoLanguageTag()
    {
        const string document = "<!DOCTYPE html><html><body>content</body></html>";
        var fenced = "```\n" + document + "\n```";

        var result = MafReportHtmlAgent.StripMarkdownFence(fenced);

        Assert.Equal(document, result);
    }

    [Fact]
    public void StripMarkdownFence_LeavesAnUnfencedDocumentUnchanged()
    {
        const string document = "<!DOCTYPE html><html><body>content</body></html>";

        var result = MafReportHtmlAgent.StripMarkdownFence(document);

        Assert.Equal(document, result);
    }

    /// <summary>
    /// A stray ``` appearing INSIDE the document body (e.g. quoted in prose) must not trigger
    /// stripping - the pattern only matches when the ENTIRE trimmed input is one fenced block.
    /// </summary>
    [Fact]
    public void StripMarkdownFence_DoesNotStripPartialOrInternalFencing()
    {
        const string document = "<!DOCTYPE html><html><body>see the `code` example: ```not a real fence```</body></html>";

        var result = MafReportHtmlAgent.StripMarkdownFence(document);

        Assert.Equal(document, result);
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] The Coverage tile grid was built from EVERY LocationRow,
    /// including intermediate (non-leaf, rollup) nodes - sql/05_dimension_location.sql's own
    /// #rows output covers both, and nothing filtered before it reached the render prompt. The
    /// real reference design (docs/PAID_TIER_SAMPLE_REFERENCE.md Sec.3.4: "leaf_stores | 632 -
    /// leaf nodes only") and the real Angular reference component (COVERAGE_STORES, one leaf
    /// store per tile) both require leaf-only. A live render used all 177 of tenant 29's branches
    /// (leaf AND intermediate) as if every one were a leaf store - wrong population, not just a
    /// count mismatch. Scoped to this ONE call site (the render prompt's payload) rather than
    /// filtering the shared LocationRows the orchestrator also threads to ComputeScoreActivity -
    /// the composite score's own coverage/backlog math intentionally still sees the full estate
    /// (including instances held directly on intermediate nodes, per this file's own
    /// "instances_on_intermediate_nodes" data-quality note), so filtering it there would silently
    /// change a different, already-correct calculation.
    /// </summary>
    [Fact]
    public void FilterToLeafStores_KeepsOnlyLeafNodeType()
    {
        var rows = new List<LocationRow>
        {
            new() { BranchID = 1, NodeType = EntityNodeType.Leaf },
            new() { BranchID = 2, NodeType = EntityNodeType.Intermediate },
            new() { BranchID = 3, NodeType = EntityNodeType.Leaf },
        };

        var result = MafReportHtmlAgent.FilterToLeafStores(rows);

        Assert.Equal([1, 3], result!.Select(r => r.BranchID));
    }

    [Fact]
    public void FilterToLeafStores_NullInput_ReturnsNull()
    {
        var result = MafReportHtmlAgent.FilterToLeafStores(null);

        Assert.Null(result);
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-02] location_rows was being sent to the render agent as a raw
    /// array (up to 177 real branches) for it to both count for the chip/KPI/legend numbers AND
    /// echo back as individual tiles - confirmed live it silently sampled the tiles instead of
    /// completing them (see CoverageGridInjector's own [BUG FOUND LIVE] note - the grid is now
    /// injected deterministically, not authored by the render agent at all). Once the grid no
    /// longer needs raw rows in the prompt, neither does anything else in this document - the
    /// render agent's only remaining real need is the 5 aggregate numbers, computed here from the
    /// same real Flags-based classification the grid injector uses, never re-derived differently.
    /// </summary>
    [Fact]
    public void ComputeCoverageStatusCounts_LeafOnly_UsesRealFlagsClassification()
    {
        var rows = new List<LocationRow>
        {
            new() { BranchID = 1, NodeType = EntityNodeType.Leaf, Flags = "" },
            new() { BranchID = 2, NodeType = EntityNodeType.Leaf, Flags = "high_ownerless" },
            new() { BranchID = 3, NodeType = EntityNodeType.Leaf, Flags = "no_obligations_configured" },
            new() { BranchID = 4, NodeType = EntityNodeType.Intermediate, Flags = "no_obligations_configured" }, // excluded - not a leaf
        };

        var counts = MafReportHtmlAgent.ComputeCoverageStatusCounts(rows);

        Assert.Equal(3, counts!.Total);
        Assert.Equal(1, counts.Healthy);
        Assert.Equal(1, counts.HasOwnerless);
        Assert.Equal(1, counts.Unmapped);
        Assert.Equal(0, counts.UnderConfigured);
    }

    [Fact]
    public void ComputeCoverageStatusCounts_NullInput_ReturnsNull()
    {
        Assert.Null(MafReportHtmlAgent.ComputeCoverageStatusCounts(null));
    }
}
