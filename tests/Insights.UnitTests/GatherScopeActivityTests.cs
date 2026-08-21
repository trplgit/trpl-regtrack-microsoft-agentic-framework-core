using Insights.Data;
using Insights.Domain;
using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class GatherScopeActivityTests
{
    [Fact]
    public async Task RunAsync_EntitledWithScope_ReturnsScopePairsAndTenantShape()
    {
        var entitlement = new Mock<IEntitlementRepository>();
        entitlement.Setup(r => r.EvaluateGateAsync(29, EntitlementTier.Paid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntitlementGateResult(29, EntitlementTier.Paid, EntitlementDecision.Proceed, 1, "ok", true));

        var pairs = new List<ScopePair> { new(100, 1), new(101, 1) };
        var scope = new Mock<IScopeRepository>();
        scope.Setup(r => r.GetScopePairsAsync(38, 29, It.IsAny<CancellationToken>())).ReturnsAsync(pairs);

        var entity = new Mock<IEntityRepository>();
        entity.Setup(r => r.GetTenantShapeAsync(29, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantShapeResult(29, 3, EntityCountShape.MultiEntity, 45.0m, ComparisonGrain.Apex, "balanced", []));

        var activity = new GatherScopeActivity(entitlement.Object, scope.Object, entity.Object);
        var result = await activity.RunAsync(new GatherScopeInput(38, 29));

        Assert.Equal(2, result.ScopePairs.Count);
        Assert.Equal("multi_entity", result.TenantShape);
    }

    [Fact]
    public async Task RunAsync_EmptyScope_ThrowsOrchestrationRefusedException()
    {
        var entitlement = new Mock<IEntitlementRepository>();
        entitlement.Setup(r => r.EvaluateGateAsync(It.IsAny<int>(), It.IsAny<EntitlementTier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntitlementGateResult(29, EntitlementTier.Paid, EntitlementDecision.Proceed, 1, "ok", true));

        var scope = new Mock<IScopeRepository>();
        scope.Setup(r => r.GetScopePairsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ScopePair>());

        var activity = new GatherScopeActivity(entitlement.Object, scope.Object, new Mock<IEntityRepository>().Object);

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() =>
            activity.RunAsync(new GatherScopeInput(38, 29)));
        Assert.Equal("SCOPE_DENIED", ex.ReasonCode);
    }

    [Fact]
    public async Task RunAsync_NotEntitled_ThrowsOrchestrationRefusedException()
    {
        var entitlement = new Mock<IEntitlementRepository>();
        entitlement.Setup(r => r.EvaluateGateAsync(It.IsAny<int>(), It.IsAny<EntitlementTier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntitlementGateResult(29, EntitlementTier.Paid, EntitlementDecision.ExitZeroCost, 0, "not entitled", false));

        var scope = new Mock<IScopeRepository>();
        var activity = new GatherScopeActivity(entitlement.Object, scope.Object, new Mock<IEntityRepository>().Object);

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() =>
            activity.RunAsync(new GatherScopeInput(38, 29)));
        Assert.Equal("NOT_ENTITLED", ex.ReasonCode);
        scope.Verify(r => r.GetScopePairsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
