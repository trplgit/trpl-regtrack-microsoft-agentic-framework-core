using Insights.Domain;
using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;
using Xunit;

namespace Insights.UnitTests;

public class ValidateFixedHolisticStructureActivityTests
{
    private const string CleanHtml = "<html><body>No score hero, no blocked panes.</body></html>";

    private const string FakeBadgeOnBlockedPane =
        "<label for=\"di-tab-6\" class=\"di-tab\">Actions<span class=\"di-tab__count tnum\">6</span></label>" +
        "<section class=\"di-pane\" id=\"di-pane-6\" data-blocked=\"true\"></section>";

    // Design spec 2026-09-09 - a per-dimension render prompt (e.g.
    // 05_report_html_dimension_selection_user.md) legitimately reuses the real Angular product's
    // own ".di-components" class for a non-score strip (the role strip / occurrence-status strip) -
    // the real component itself reuses that class this way, confirmed by reading
    // detailed-insights.component.html directly. This document has a ".di-components" wrapper with
    // zero ".di-comp" cards inside it - a real FIXED_HOLISTIC_STRUCTURE_INVALID trigger if this
    // gate ran on it, but it must NOT run at all for a non-fixed_holistic ReportType.
    private const string DimensionSelectionDocumentReusingDiComponentsForANonScoreStrip =
        "<div class=\"di-components ur-roles\"><span>Role structure across 656 users</span></div>";

    [Fact]
    public async Task RunAsync_StructurallyValidHtml_ReturnsIt()
    {
        var activity = new ValidateFixedHolisticStructureActivity();

        var result = await activity.RunAsync(new ValidateFixedHolisticStructureInput(CleanHtml, FixedHolisticComposition.ReportType));

        Assert.Equal(CleanHtml, result.Html);
    }

    [Fact]
    public async Task RunAsync_FakeBadgeOnBlockedPane_ThrowsFixedHolisticStructureInvalid()
    {
        var activity = new ValidateFixedHolisticStructureActivity();

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() =>
            activity.RunAsync(new ValidateFixedHolisticStructureInput(FakeBadgeOnBlockedPane, FixedHolisticComposition.ReportType)));
        Assert.Equal("FIXED_HOLISTIC_STRUCTURE_INVALID", ex.ReasonCode);
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-09] Confirmed against a real tenant-29 run: a
    /// "dimension_selection:Departments" render was refused ("found 0 .di-component card(s),
    /// expected exactly 7") for legitimately reusing ".di-components" on a strip that was never a
    /// score hero. This gate's checks are fixed_holistic-template-specific invariants by
    /// construction (DimensionSelectionComposition never computes a composite score at all) - it
    /// must be skipped entirely for any other ReportType, not just tolerant of the class collision.
    /// </summary>
    [Fact]
    public async Task RunAsync_NonFixedHolisticReportType_SkipsEvaluationEntirely_EvenWithAMisleadingDiComponentsWrapper()
    {
        var activity = new ValidateFixedHolisticStructureActivity();

        var result = await activity.RunAsync(new ValidateFixedHolisticStructureInput(
            DimensionSelectionDocumentReusingDiComponentsForANonScoreStrip, DimensionSelectionComposition.ReportType));

        Assert.Equal(DimensionSelectionDocumentReusingDiComponentsForANonScoreStrip, result.Html);
    }
}
