using Insights.Presentation;

namespace Insights.UnitTests;

/// <summary>
/// Design doc Sec.11.4 (Partial generation). The one thing that MUST hold: the resulting document
/// still passes ReportEmitNormalizer's own rules (exactly one DOCTYPE, exactly one closing
/// &lt;/html&gt;, document still ends with it) - a placeholder that broke the very safety check it
/// is supposed to survive would be worse than no placeholder at all.
/// </summary>
public sealed class PartialDimensionPlaceholderTests
{
    private const string Document = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><h1>Report</h1></body></html>";

    [Fact]
    public void NoFailures_ReturnsTheDocumentUnchanged()
    {
        var result = PartialDimensionPlaceholder.InsertPlaceholders(Document, []);

        Assert.Equal(Document, result);
    }

    [Fact]
    public void OneFailure_InsertsALabelledPlaceholder_BeforeClosingBody()
    {
        var result = PartialDimensionPlaceholder.InsertPlaceholders(Document, ["Risk"]);

        Assert.Contains("Section unavailable", result);
        Assert.Contains("risk-tier analysis could not be generated for this report", result);
        Assert.True(result.IndexOf("Section unavailable", StringComparison.Ordinal) < result.IndexOf("</body>", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MultipleFailures_InsertsOneBlockPerDimension()
    {
        var result = PartialDimensionPlaceholder.InsertPlaceholders(Document, ["Risk", "Nature", "Event"]);

        Assert.Contains("risk-tier analysis", result);
        Assert.Contains("nature-of-compliance analysis", result);
        Assert.Contains("event-triggered-compliance analysis", result);
    }

    /// <summary>An unrecognised dimension name still gets a placeholder, just less pretty - never silently drop it.</summary>
    [Fact]
    public void UnknownDimensionName_FallsBackToTheRawName_RatherThanDroppingIt()
    {
        var result = PartialDimensionPlaceholder.InsertPlaceholders(Document, ["SomeFutureDimension"]);

        Assert.Contains("SomeFutureDimension analysis could not be generated", result);
    }

    [Fact]
    public void NoBodyTag_FallsBackToInsertingBeforeClosingHtml()
    {
        const string noBody = "<!DOCTYPE html><html><head></head></html>";

        var result = PartialDimensionPlaceholder.InsertPlaceholders(noBody, ["Risk"]);

        Assert.Contains("Section unavailable", result);
        Assert.True(result.IndexOf("Section unavailable", StringComparison.Ordinal) < result.IndexOf("</html>", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tenant/agent data never flows through this path, but the encode call is defence in depth - confirm it does not double-encode or break on a name containing markup-like characters.</summary>
    [Fact]
    public void EscapesTheDimensionNameName_EvenThoughItIsAlwaysAClosedSetInPractice()
    {
        var result = PartialDimensionPlaceholder.InsertPlaceholders(Document, ["<script>"]);

        Assert.DoesNotContain("<script>", result);
        Assert.Contains("&lt;script&gt;", result);
    }

    /// <summary>
    /// The real safety net this whole class exists to never violate - the inserted markup must
    /// still satisfy ReportEmitNormalizer's rule 1 (exactly one DOCTYPE, exactly one closing
    /// &lt;/html&gt;, document starts/ends correctly).
    /// </summary>
    [Fact]
    public void ResultingDocument_StillPassesReportEmitNormalizer()
    {
        var result = PartialDimensionPlaceholder.InsertPlaceholders(Document, ["Location", "Users"]);

        var evaluation = ReportEmitNormalizer.Evaluate(result);

        Assert.True(evaluation.Approved, string.Join("; ", evaluation.Violations));
    }
}
