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

    [Fact]
    public async Task RunAsync_StructurallyValidHtml_ReturnsIt()
    {
        var activity = new ValidateFixedHolisticStructureActivity();

        var result = await activity.RunAsync(new ValidateFixedHolisticStructureInput(CleanHtml));

        Assert.Equal(CleanHtml, result.Html);
    }

    [Fact]
    public async Task RunAsync_FakeBadgeOnBlockedPane_ThrowsFixedHolisticStructureInvalid()
    {
        var activity = new ValidateFixedHolisticStructureActivity();

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() =>
            activity.RunAsync(new ValidateFixedHolisticStructureInput(FakeBadgeOnBlockedPane)));
        Assert.Equal("FIXED_HOLISTIC_STRUCTURE_INVALID", ex.ReasonCode);
    }
}
