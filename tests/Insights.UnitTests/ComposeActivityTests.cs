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
            .ReturnsAsync(new AgentCallResult<CompositionPlan>(expectedPlan, 1500));

        var activity = new ComposeActivity(agent.Object);
        var input = new ComposeInput(
            new Dictionary<string, string> { ["Location"] = JsonSerializer.Serialize(new { dimension = "Location" }) },
            "multi_entity", "compliance_health", null, null);

        var result = await activity.RunAsync(input);

        Assert.Equal(expectedPlan, result.Plan);
        Assert.Equal(1500, result.TotalTokens);
    }

    /// <summary>
    /// Design doc Sec.4.4's priority lanes only work end to end if the priority on the DTFx
    /// activity input actually reaches ConcurrencyGatedChatClient - and it gets there via
    /// LlmCallPriorityContext (AsyncLocal), not a parameter, since the agent itself is a shared
    /// singleton (see LlmConcurrencyGate's doc comment). This pins that RunAsync sets it BEFORE
    /// calling the agent and restores it afterwards - a leaked Batch priority would silently
    /// misprioritise whatever LLM call this worker process makes next.
    /// </summary>
    [Fact]
    public async Task RunAsync_MakesItsPriorityAmbient_ForTheDurationOfTheAgentCall_ThenRestoresIt()
    {
        var observedDuringCall = (LlmCallPriority?)null;
        var agent = new Mock<ICompositionAgent>();
        agent.Setup(a => a.ComposeAsync(
                It.IsAny<IReadOnlyDictionary<string, object>>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<(CompositionPlan, IReadOnlyList<CompositionReflectionIssue>)?>(), It.IsAny<CancellationToken>()))
            .Callback(() => observedDuringCall = LlmCallPriorityContext.CurrentOrDefault)
            .ReturnsAsync(new AgentCallResult<CompositionPlan>(new CompositionPlan(new CompositionHero("h", "w"), [], [], []), 1));

        var activity = new ComposeActivity(agent.Object);
        var input = new ComposeInput(
            new Dictionary<string, string> { ["Location"] = JsonSerializer.Serialize(new { dimension = "Location" }) },
            "multi_entity", "compliance_health", null, null, LlmCallPriority.Batch);

        await activity.RunAsync(input);

        Assert.Equal(LlmCallPriority.Batch, observedDuringCall);
        Assert.Equal(LlmCallPriority.Interactive, LlmCallPriorityContext.CurrentOrDefault); // restored, not leaked
    }
}
