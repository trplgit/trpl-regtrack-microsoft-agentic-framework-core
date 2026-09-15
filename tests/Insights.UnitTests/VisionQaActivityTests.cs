using Insights.Agents;
using Insights.Domain;
using Insights.Presentation;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class VisionQaActivityTests
{
    [Fact]
    public async Task RunAsync_CapturesScreenshotsThenReviewsThem_NoDefect()
    {
        var qaRunner = new Mock<IReportQaRunner>();
        IReadOnlyList<byte[]> screenshots = [[1, 2, 3], [4, 5, 6]];
        qaRunner.Setup(r => r.RunAsync("<html></html>", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReportQaResult(false, [], false, screenshots));

        var visionAgent = new Mock<IVisionQaAgent>();
        visionAgent.Setup(a => a.ReviewAsync(screenshots, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<VisionQaResult>(new VisionQaResult(false, null), 800));

        var activity = new VisionQaActivity(qaRunner.Object, visionAgent.Object);
        var result = await activity.RunAsync(new VisionQaInput("<html></html>"));

        Assert.False(result.HasVisualDefect);
        Assert.Null(result.Issue);
        Assert.Equal(800, result.TotalTokens);
    }

    [Fact]
    public async Task RunAsync_RealDefectFound_ReturnsTheConcreteIssueText()
    {
        var qaRunner = new Mock<IReportQaRunner>();
        IReadOnlyList<byte[]> screenshots = [[9, 9, 9]];
        qaRunner.Setup(r => r.RunAsync("<html></html>", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReportQaResult(false, [], false, screenshots));

        var visionAgent = new Mock<IVisionQaAgent>();
        visionAgent.Setup(a => a.ReviewAsync(screenshots, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<VisionQaResult>(
                new VisionQaResult(true, "The KPI tile row overlaps the finding cards below it."), 900));

        var activity = new VisionQaActivity(qaRunner.Object, visionAgent.Object);
        var result = await activity.RunAsync(new VisionQaInput("<html></html>"));

        Assert.True(result.HasVisualDefect);
        Assert.Equal("The KPI tile row overlaps the finding cards below it.", result.Issue);
    }
}
