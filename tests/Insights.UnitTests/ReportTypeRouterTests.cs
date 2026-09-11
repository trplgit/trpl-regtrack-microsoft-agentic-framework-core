using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// Product rule (2026-09-09): picking "Entity" in the dimension picker routes the whole run to
/// fixed_holistic - Entity has no finalized dimension_selection template of its own, and the
/// designer wants the full 6-tab dashboard for it instead. Every other of the 9 real dimensions
/// keeps its finalized dimension_selection template unchanged.
///
/// [TRIED AND REVERTED SAME DAY, 2026-09-11] briefly widened to "Entity anywhere in the list"
/// (dropping every other requested dimension) - reverted: that silently discarded the other
/// picks, never the actual intent. Back to Entity-ALONE-only.
/// </summary>
public sealed class ReportTypeRouterTests
{
    [Fact]
    public void Resolve_EntityRequested_RoutesToFixedHolistic_AndDropsTheDimensionsList()
    {
        var (reportType, dims) = ReportTypeRouter.Resolve("dimension_selection", ["Entity"]);

        Assert.Equal("fixed_holistic", reportType);
        Assert.Null(dims);
    }

    /// <summary>
    /// Deliberately NOT redirected - matches InsightsReportOrchestrator.RunTask's own condition
    /// exactly (RequestedDimensions is EXACTLY ["Entity"]), so this stays one product decision,
    /// not two divergent ones at two layers.
    /// </summary>
    [Fact]
    public void Resolve_EntityCombinedWithOtherDimensions_PassesThroughUnchanged()
    {
        var (reportType, dims) = ReportTypeRouter.Resolve("dimension_selection", ["Location", "Entity", "Act"]);

        Assert.Equal("dimension_selection", reportType);
        Assert.Equal(["Location", "Entity", "Act"], dims);
    }

    [Theory]
    [InlineData("Location")]
    [InlineData("Risk")]
    [InlineData("Nature")]
    [InlineData("Departments")]
    [InlineData("Act")]
    [InlineData("Users")]
    [InlineData("Internal")]
    [InlineData("Event")]
    public void Resolve_AnyOtherSingleDimension_PassesThroughUnchanged(string dimension)
    {
        var (reportType, dims) = ReportTypeRouter.Resolve("dimension_selection", [dimension]);

        Assert.Equal("dimension_selection", reportType);
        Assert.Equal([dimension], dims);
    }

    [Fact]
    public void Resolve_FixedHolisticRequestDirectly_PassesThroughUnchanged()
    {
        var (reportType, dims) = ReportTypeRouter.Resolve("fixed_holistic", null);

        Assert.Equal("fixed_holistic", reportType);
        Assert.Null(dims);
    }

    [Fact]
    public void Resolve_DimensionSelectionWithNoDimensionsRequested_PassesThroughUnchanged()
    {
        // Malformed input (dimension_selection needs at least one dimension), but this router's
        // only job is the Entity redirect - it is not the place that validates the request.
        var (reportType, dims) = ReportTypeRouter.Resolve("dimension_selection", null);

        Assert.Equal("dimension_selection", reportType);
        Assert.Null(dims);
    }

    [Fact]
    public void Resolve_OtherReportTypeNamedEntityCoincidentally_NeverMatchesOnReportTypeAlone()
    {
        // The redirect is keyed on (reportType == dimension_selection) AND (dimensions contains
        // "Entity") together - a compliance_health or fixed_holistic request is untouched even
        // if it somehow carried a RequestedDimensions list.
        var (reportType, dims) = ReportTypeRouter.Resolve("compliance_health", ["Entity"]);

        Assert.Equal("compliance_health", reportType);
        Assert.Equal(["Entity"], dims);
    }
}
