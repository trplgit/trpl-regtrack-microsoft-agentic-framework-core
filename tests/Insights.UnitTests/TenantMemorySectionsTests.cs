using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// TenantMemorySections is pure - no blob/network I/O - so it is tested directly, same shape as
/// AzureReportBlobWriterPathTests. Sections are delimited by a fixed "## {DimensionName}" heading
/// up to the next "## " heading or EOF; each dimension owns only its own section.
/// </summary>
public sealed class TenantMemorySectionsTests
{
    [Fact]
    public void ExtractSection_EmptyDocument_ReturnsEmptyString()
    {
        var result = TenantMemorySections.ExtractSection("", "Internal");

        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractSection_HeadingAbsent_ReturnsEmptyString()
    {
        const string doc = "## Risk\n2026-09-18: some finding.\n";

        var result = TenantMemorySections.ExtractSection(doc, "Internal");

        Assert.Equal("", result);
    }

    [Fact]
    public void ExtractSection_SingleSection_ReturnsItsBody()
    {
        const string doc = "## Internal\n2026-09-15: 44 of 99 locations run statutory with no internal governance.\n";

        var result = TenantMemorySections.ExtractSection(doc, "Internal");

        Assert.Equal("2026-09-15: 44 of 99 locations run statutory with no internal governance.", result);
    }

    [Fact]
    public void ExtractSection_MultipleSections_ReturnsOnlyTheRequestedOne()
    {
        const string doc =
            "## Internal\n2026-09-15: gap finding.\n\n" +
            "## Risk\n2026-09-18: critical concentration in Gujarat.\n\n" +
            "## Nature\n2026-09-20: 49% uncategorised.\n";

        var result = TenantMemorySections.ExtractSection(doc, "Risk");

        Assert.Equal("2026-09-18: critical concentration in Gujarat.", result);
    }

    [Fact]
    public void ExtractSection_LastSection_ReadsToEndOfDocument()
    {
        const string doc =
            "## Internal\n2026-09-15: gap finding.\n\n" +
            "## Event\n2026-09-22: 129 of 141 event types dormant.\n";

        var result = TenantMemorySections.ExtractSection(doc, "Event");

        Assert.Equal("2026-09-22: 129 of 141 event types dormant.", result);
    }

    [Fact]
    public void ReplaceSection_EmptyDocument_InsertsNewSection()
    {
        var result = TenantMemorySections.ReplaceSection("", "Internal", "2026-09-22: first entry.");

        Assert.Equal("## Internal\n2026-09-22: first entry.\n", result);
    }

    [Fact]
    public void ReplaceSection_ExistingSection_ReplacesItInPlace_LeavesOthersUntouched()
    {
        const string doc =
            "## Internal\n2026-09-15: old entry.\n\n" +
            "## Risk\n2026-09-18: critical concentration in Gujarat.\n";

        var result = TenantMemorySections.ReplaceSection(doc, "Internal", "2026-09-15: old entry.\n2026-09-22: new entry.");

        Assert.Equal(
            "## Internal\n2026-09-15: old entry.\n2026-09-22: new entry.\n\n" +
            "## Risk\n2026-09-18: critical concentration in Gujarat.\n",
            result);
    }

    [Fact]
    public void ReplaceSection_SectionAbsent_AppendsNewSectionAtEnd_LeavesExistingUntouched()
    {
        const string doc = "## Risk\n2026-09-18: critical concentration in Gujarat.\n";

        var result = TenantMemorySections.ReplaceSection(doc, "Nature", "2026-09-22: first Nature entry.");

        Assert.Equal(
            "## Risk\n2026-09-18: critical concentration in Gujarat.\n\n" +
            "## Nature\n2026-09-22: first Nature entry.\n",
            result);
    }

    [Fact]
    public void ReplaceSection_ReplacingMiddleSection_PreservesOrderOfOthers()
    {
        const string doc =
            "## Internal\nA\n\n" +
            "## Risk\nB\n\n" +
            "## Nature\nC\n";

        var result = TenantMemorySections.ReplaceSection(doc, "Risk", "B2");

        Assert.Equal(
            "## Internal\nA\n\n" +
            "## Risk\nB2\n\n" +
            "## Nature\nC\n",
            result);
    }

    [Fact]
    public void ExtractSection_ThenReplaceSection_RoundTripsCleanly()
    {
        var doc = TenantMemorySections.ReplaceSection("", "Internal", "first entry");
        doc = TenantMemorySections.ReplaceSection(doc, "Risk", "risk entry");

        var extracted = TenantMemorySections.ExtractSection(doc, "Internal");

        Assert.Equal("first entry", extracted);
    }
}
