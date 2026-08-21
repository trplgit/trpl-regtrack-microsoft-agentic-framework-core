using System.Text.Json;
using Insights.Agents;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class ComposeActivityTests
{
    [Fact]
    public async Task RunAsync_ConvertsJsonElementsToObjectDictionary_AndCallsComposeAsync()
    {
        var agent = new Mock<ICompositionAgent>();
        var expectedPlan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        agent.Setup(a => a.ComposeAsync(
                It.Is<IReadOnlyDictionary<string, object>>(d => d.ContainsKey("Location")),
                "multi_entity", "compliance_health", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPlan);

        var activity = new ComposeActivity(agent.Object);
        var input = new ComposeInput(
            new Dictionary<string, string> { ["Location"] = JsonSerializer.Serialize(new { dimension = "Location" }) },
            "multi_entity", "compliance_health", null, null);

        var result = await activity.RunAsync(input);

        Assert.Equal(expectedPlan, result.Plan);
    }
}
