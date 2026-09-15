using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class ReflectOnNarrativeActivityTests
{
    [Fact]
    public async Task RunAsync_PassesThroughToReflectAsync()
    {
        var agent = new Mock<INarrativeReflectionAgent>();
        var narrative = new NarrativeResult([]);
        IReadOnlyList<Assertion> assertions = [];
        IReadOnlyList<Finding> findings = [];
        var expected = new NarrativeReflectionResult(ReflectionVerdict.Approve, []);

        agent.Setup(a => a.ReflectAsync(narrative, assertions, findings, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<NarrativeReflectionResult>(expected, 1800));

        var activity = new ReflectOnNarrativeActivity(agent.Object);
        var result = await activity.RunAsync(new ReflectOnNarrativeInput(narrative, assertions, findings));

        Assert.Equal(expected, result.Result);
        Assert.Equal(1800, result.TotalTokens);
    }

    [Fact]
    public async Task RunAsync_RunIdGivenAndReasoningSummaryPresent_RecordsIt()
    {
        var agent = new Mock<INarrativeReflectionAgent>();
        var narrative = new NarrativeResult([]);
        IReadOnlyList<Assertion> assertions = [];
        IReadOnlyList<Finding> findings = [];
        var expected = new NarrativeReflectionResult(ReflectionVerdict.Approve, []);

        agent.Setup(a => a.ReflectAsync(narrative, assertions, findings, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<NarrativeReflectionResult>(expected, 1800, "checked every claim against its assertion id..."));

        var recorder = new Mock<IAgentReasoningRecorder>();
        var activity = new ReflectOnNarrativeActivity(agent.Object, recorder.Object);
        await activity.RunAsync(new ReflectOnNarrativeInput(narrative, assertions, findings), runId: "run-123");

        recorder.Verify(r => r.RecordAsync("run-123", "reflect_narrative", "checked every claim against its assertion id...", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_NoRunIdGiven_NeverCallsRecorder()
    {
        var agent = new Mock<INarrativeReflectionAgent>();
        var narrative = new NarrativeResult([]);
        IReadOnlyList<Assertion> assertions = [];
        IReadOnlyList<Finding> findings = [];
        var expected = new NarrativeReflectionResult(ReflectionVerdict.Approve, []);

        agent.Setup(a => a.ReflectAsync(narrative, assertions, findings, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<NarrativeReflectionResult>(expected, 1800, "some reasoning"));

        var recorder = new Mock<IAgentReasoningRecorder>();
        var activity = new ReflectOnNarrativeActivity(agent.Object, recorder.Object);
        await activity.RunAsync(new ReflectOnNarrativeInput(narrative, assertions, findings));

        recorder.Verify(r => r.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
