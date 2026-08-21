using Insights.Domain;
using Insights.Presentation;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class PlaywrightQaActivityTests
{
    [Fact]
    public async Task RunAsync_HasIssuesTrue_StillReturnsResult_DoesNotThrow()
    {
        var runner = new Mock<IReportQaRunner>();
        runner.Setup(r => r.RunAsync("<html></html>", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReportQaResult(true, ["console error"], false, []));

        var activity = new PlaywrightQaActivity(runner.Object);
        var result = await activity.RunAsync(new PlaywrightQaInput("<html></html>"));

        Assert.True(result.Result.HasIssues);
    }
}
