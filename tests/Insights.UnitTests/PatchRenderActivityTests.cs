using Insights.Agents;
using Insights.Domain;
using Insights.Presentation;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class PatchRenderActivityTests
{
    [Fact]
    public async Task RunAsync_DelegatesToAgent_ReturnsPatchedHtmlAndTokens()
    {
        var finding = new TileFinding("section.card[data-tile-qa-index=\"0\"]", "Title", "click", TileFindingSeverity.Functional, "does nothing", "b.png", "a.png");
        var agent = new Mock<IPatchRenderAgent>();
        agent.Setup(a => a.PatchAsync("<html>old</html>", It.Is<IReadOnlyList<TileFinding>>(f => f.Count == 1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<html>patched</html>", 500));

        var activity = new PatchRenderActivity(agent.Object, NullLogger<PatchRenderActivity>.Instance);
        var result = await activity.RunAsync(new PatchRenderInput("<html>old</html>", [finding]));

        Assert.Equal("<html>patched</html>", result.Html);
        Assert.Equal(500, result.TotalTokens);
    }
}
