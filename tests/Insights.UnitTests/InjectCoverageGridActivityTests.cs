using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Xunit;

namespace Insights.UnitTests;

public class InjectCoverageGridActivityTests
{
    private const string DocumentWithPlaceholder =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" +
        "<div id=\"di-covgrid-root\"></div></body></html>";

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
