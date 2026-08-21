using Insights.Agents;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class NarrateActivityTests
{
    [Fact]
    public async Task RunAsync_NoPreviousNarrative_CallsNarrateAsyncWithNullRevision()
    {
        var agent = new Mock<INarrativeAgent>();
        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var expected = new NarrativeResult([]);
        agent.Setup(a => a.NarrateAsync(plan, It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<IReadOnlyList<Finding>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var activity = new NarrateActivity(agent.Object);
        var result = await activity.RunAsync(new NarrateInput(plan, [], [], null, null));

        Assert.Equal(expected, result.Narrative);
    }
}
