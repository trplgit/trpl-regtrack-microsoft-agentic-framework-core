using Insights.Worker.Orchestration.Activities;
using Xunit;

namespace Insights.UnitTests;

public class InjectCoverageScriptActivityTests
{
    private const string DocumentWithCoverageGrid = """
        <!DOCTYPE html><html><head><meta charset="utf-8"></head><body>
        <div class="di-covgrid"><button class="di-covtile" data-st="healthy"></button></div>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_DocumentWithCoverageGrid_ReturnsHtmlWithDrivingScriptInjected()
    {
        var activity = new InjectCoverageScriptActivity();

        var result = await activity.RunAsync(new InjectCoverageScriptInput(DocumentWithCoverageGrid));

        Assert.Contains(".di-covmap", result.Html, StringComparison.Ordinal);
        // Original content preserved, just extended.
        Assert.Contains("di-covtile", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DocumentWithNoCoverageGrid_ReturnsHtmlUnchanged()
    {
        const string noGrid = "<!DOCTYPE html><html><head></head><body>no coverage here</body></html>";
        var activity = new InjectCoverageScriptActivity();

        var result = await activity.RunAsync(new InjectCoverageScriptInput(noGrid));

        Assert.Equal(noGrid, result.Html);
    }
}
