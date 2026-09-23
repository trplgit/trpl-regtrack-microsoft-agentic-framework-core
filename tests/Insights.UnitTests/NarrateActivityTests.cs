using Insights.Agents;
using Insights.Data;
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
        agent.Setup(a => a.NarrateAsync(plan, It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<IReadOnlyList<Finding>>(), null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<NarrativeResult>(expected, 2200));

        var activity = new NarrateActivity(agent.Object);
        var result = await activity.RunAsync(new NarrateInput(plan, [], [], null, null));

        Assert.Equal(expected, result.Narrative);
        Assert.Equal(2200, result.TotalTokens);
    }

    [Fact]
    public async Task RunAsync_RunIdGivenAndReasoningSummaryPresent_RecordsIt()
    {
        var agent = new Mock<INarrativeAgent>();
        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var expected = new NarrativeResult([]);
        agent.Setup(a => a.NarrateAsync(plan, It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<IReadOnlyList<Finding>>(), null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<NarrativeResult>(expected, 2200, "led with the overdue-rate finding because..."));

        var recorder = new Mock<IAgentReasoningRecorder>();
        var activity = new NarrateActivity(agent.Object, recorder.Object);
        await activity.RunAsync(new NarrateInput(plan, [], [], null, null), runId: "run-123");

        recorder.Verify(r => r.RecordAsync("run-123", "narrate", "led with the overdue-rate finding because...", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_NoRunIdGiven_NeverCallsRecorder()
    {
        var agent = new Mock<INarrativeAgent>();
        var plan = new CompositionPlan(new CompositionHero("coverage_map", "why"), [], [], []);
        var expected = new NarrativeResult([]);
        agent.Setup(a => a.NarrateAsync(plan, It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<IReadOnlyList<Finding>>(), null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<NarrativeResult>(expected, 2200, "some reasoning"));

        var recorder = new Mock<IAgentReasoningRecorder>();
        var activity = new NarrateActivity(agent.Object, recorder.Object);
        await activity.RunAsync(new NarrateInput(plan, [], [], null, null));

        recorder.Verify(r => r.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
