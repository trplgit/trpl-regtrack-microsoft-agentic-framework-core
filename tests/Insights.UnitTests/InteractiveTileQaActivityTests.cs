using Insights.Presentation;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class InteractiveTileQaActivityTests
{
    [Fact]
    public async Task RunAsync_DelegatesToCheckerAndWrapsFindings()
    {
        var finding = new TileFinding("section.card[data-tile-qa-index=\"0\"]", "Title", "click", TileFindingSeverity.Functional, "desc", "before.png", "after.png");
        var checker = new Mock<IInteractiveTileChecker>();
        checker.Setup(c => c.FindIssuesAsync("<html></html>", It.IsAny<CancellationToken>())).ReturnsAsync([finding]);

        var activity = new InteractiveTileQaActivity(checker.Object, NullLogger<InteractiveTileQaActivity>.Instance);
        var result = await activity.RunAsync(new InteractiveTileQaInput("<html></html>"));

        Assert.Single(result.Findings);
        Assert.Equal(TileFindingSeverity.Functional, result.Findings[0].Severity);
    }

    [Fact]
    public async Task RunAsync_NoFindings_ReturnsEmptyList()
    {
        var checker = new Mock<IInteractiveTileChecker>();
        checker.Setup(c => c.FindIssuesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var activity = new InteractiveTileQaActivity(checker.Object, NullLogger<InteractiveTileQaActivity>.Instance);
        var result = await activity.RunAsync(new InteractiveTileQaInput("<html></html>"));

        Assert.Empty(result.Findings);
    }
}
