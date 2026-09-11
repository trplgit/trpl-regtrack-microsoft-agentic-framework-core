using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Xunit;

namespace Insights.UnitTests;

public class InjectCoverageGridActivityTests
{
    // [FIX - stale fixture, found live 2026-09-09] Same root cause as CoverageGridInjectorTests:
    // Inject() was narrowed on 2026-09-07 to match a `<section id="di-pane-3">` wrapper, not the
    // bare `di-covgrid-root` div this fixture used - so this test's early-return path was being
    // exercised, not the real injection logic, since the narrowing shipped.
    private const string DocumentWithPlaceholder =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" +
        "<section class=\"di-pane\" id=\"di-pane-3\" aria-label=\"Coverage\"><div id=\"di-pane-3-body\"></div></section>" +
        "</body></html>";

    [Fact]
    public async Task RunAsync_DocumentWithPlaceholderAndRealRows_InjectsOneTilePerLeafRow()
    {
        var rows = new List<LocationRow>
        {
            new() { BranchID = 1, BranchName = "A", StateName = "Haryana", NodeType = EntityNodeType.Leaf, DistinctPerformers = 1 },
            new() { BranchID = 2, BranchName = "B", StateName = "Haryana", NodeType = EntityNodeType.Leaf, DistinctPerformers = 1 },
        };
        var activity = new InjectCoverageGridActivity();

        var result = await activity.RunAsync(new InjectCoverageGridInput(DocumentWithPlaceholder, rows));

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(result.Html, "di-covtile di-covtile--").Count);
    }

    [Fact]
    public async Task RunAsync_NullLocationRows_ReturnsHtmlUnchanged()
    {
        var activity = new InjectCoverageGridActivity();

        var result = await activity.RunAsync(new InjectCoverageGridInput(DocumentWithPlaceholder, null));

        Assert.Equal(DocumentWithPlaceholder, result.Html);
    }
}
