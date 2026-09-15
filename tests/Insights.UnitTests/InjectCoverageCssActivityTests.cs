using Insights.Worker.Orchestration.Activities;
using Xunit;

namespace Insights.UnitTests;

public class InjectCoverageCssActivityTests
{
    [Fact]
    public async Task RunAsync_DocumentWithCoverageGrid_ReturnsHtmlWithColourCssInjected()
    {
        const string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" +
            "<div class=\"di-covgrid\"></div></body></html>";
        var activity = new InjectCoverageCssActivity();

        var result = await activity.RunAsync(new InjectCoverageCssInput(html));

        Assert.Contains(".di-covtile--healthy{background:#2e9e5b}", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DocumentWithNoCoveragePane_ReturnsHtmlUnchanged()
    {
        const string html = "<!DOCTYPE html><html><head></head><body>no coverage here</body></html>";
        var activity = new InjectCoverageCssActivity();

        var result = await activity.RunAsync(new InjectCoverageCssInput(html));

        Assert.Equal(html, result.Html);
    }
}
