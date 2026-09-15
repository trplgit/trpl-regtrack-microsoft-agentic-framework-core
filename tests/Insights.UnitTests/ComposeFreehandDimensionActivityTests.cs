using Insights.Agents;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class ComposeFreehandDimensionActivityTests
{
    [Fact]
    public async Task RunAsync_RegisteredDimension_CallsThatDimensionsAgent()
    {
        var actAgent = new Mock<IFreehandDimensionCompositionAgent>();
        var licenceAgent = new Mock<IFreehandDimensionCompositionAgent>();
        var plan = new CompositionPlan(new CompositionHero("worst_acts", "highest overdue rate for this tenant"), [], [], []);
        IReadOnlyList<Assertion> assertions = [];
        IReadOnlyList<Finding> findings = [];

        actAgent.Setup(a => a.ComposeAsync(assertions, findings, "[]", "{}", "[]", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<CompositionPlan>(plan, 1500));

        var activity = new ComposeFreehandDimensionActivity(new Dictionary<string, IFreehandDimensionCompositionAgent>
        {
            ["Act"] = actAgent.Object,
            ["Licence"] = licenceAgent.Object,
        });

        var result = await activity.RunAsync(new ComposeFreehandDimensionInput("Act", assertions, findings, "[]", "{}", "[]"));

        Assert.Equal(plan, result.Plan);
        Assert.Equal(1500, result.TotalTokens);
        licenceAgent.Verify(a => a.ComposeAsync(It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<IReadOnlyList<Finding>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_UnregisteredDimension_Throws()
    {
        var activity = new ComposeFreehandDimensionActivity(new Dictionary<string, IFreehandDimensionCompositionAgent>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            activity.RunAsync(new ComposeFreehandDimensionInput("Nature", [], [], "[]", "{}", "[]")));
    }
}
