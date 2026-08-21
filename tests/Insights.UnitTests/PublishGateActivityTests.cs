using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class PublishGateActivityTests
{
    [Fact]
    public async Task RunAsync_Approved_ReturnsApprovedTrue()
    {
        var scopeRepository = new Mock<IScopeRepository>();
        scopeRepository.Setup(r => r.AuditScopeAsync(38, 29, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScopeAuditResult(0, 0, 0));

        var gate = new PublishGate(scopeRepository.Object);
        var activity = new PublishGateActivity(gate);

        var narrative = new NarrativeResult([]);
        var result = await activity.RunAsync(new PublishGateInput(38, 29, narrative, []));

        Assert.True(result.Approved);
    }

    [Fact]
    public async Task RunAsync_UnmappedAssertionId_ThrowsGateRefused()
    {
        var scopeRepository = new Mock<IScopeRepository>();
        var gate = new PublishGate(scopeRepository.Object);
        var activity = new PublishGateActivity(gate);

        var narrative = new NarrativeResult([new NarrativeBlockResult("hero", "prose", ["not-a-real-id"])]);
        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() =>
            activity.RunAsync(new PublishGateInput(38, 29, narrative, [])));

        Assert.Equal("GATE_REFUSED", ex.ReasonCode);
        scopeRepository.Verify(r => r.AuditScopeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
