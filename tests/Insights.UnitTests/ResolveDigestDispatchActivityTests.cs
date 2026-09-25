using Insights.Data;
using Insights.Data.Email;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>The email gateway gate (2026-09-25): resolved live after the entitlement gate, before anyone is claimed.</summary>
public sealed class ResolveDigestDispatchActivityTests
{
    private static (ResolveDigestDispatchActivity Activity, Mock<IFreeDigestArtifactRepository> Artifacts) Build(EmailGatewayResolution resolution)
    {
        var repo = new Mock<IFreeDigestRepository>();
        repo.Setup(r => r.GetEntitledTenantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new FreeDigestTenant(23, "ABC Training")]);
        repo.Setup(r => r.EvaluateGateAsync(23, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeDigestGateResult(23, EntitlementDecision.Proceed, 1, "ok", true));
        repo.Setup(r => r.GetRecipientsAsync(23, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FreeDigestRecipient(357, "someone@example.com", "Someone")]);

        var pairs = new List<ScopePair> { new(100, 1) };
        var scope = new Mock<IScopeRepository>();
        scope.Setup(s => s.GetScopePairsAsync(357, 23, It.IsAny<CancellationToken>())).ReturnsAsync(pairs);

        var artifacts = new Mock<IFreeDigestArtifactRepository>();
        artifacts.Setup(a => a.GetForDispatchAsync(23, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FreeDigestArtifact(Guid.NewGuid(), 23, new DateOnly(2026, 8, 23), ScopeSignature.For(pairs), 357,
                "ABC Training", DateTime.UtcNow, DateTime.UtcNow, "llm", "c", "p", [], "k", "v")]);

        var resolver = new Mock<IEmailGatewayResolver>();
        resolver.Setup(r => r.ResolveAsync(23, It.IsAny<CancellationToken>())).ReturnsAsync(resolution);

        var activity = new ResolveDigestDispatchActivity(repo.Object, artifacts.Object, scope.Object, resolver.Object,
            NullLogger<ResolveDigestDispatchActivity>.Instance);

        return (activity, artifacts);
    }

    [Fact]
    public async Task TheResolvedGatewayIsReturnedWithTheGroups()
    {
        var (activity, _) = Build(new EmailGatewayResolution(EmailGateway.SendGrid, EmailGatewaySource.FailoverSwitch, "switch", []));

        var result = await activity.RunAsync(new ResolveDigestDispatchInput(23, 3));

        Assert.True(result.ShouldProceed);
        Assert.Single(result.Groups);
        Assert.Equal((int)EmailGateway.SendGrid, result.EmailGatewayId);
    }

    /// <summary>A refused configuration stops the whole tenant before any artifact is read or recipient claimed.</summary>
    [Fact]
    public async Task ARefusedGateway_RefusesTheTenant()
    {
        var (activity, artifacts) = Build(new EmailGatewayResolution(null, EmailGatewaySource.Refused, "unknown EmailGateWayType 7", []));

        var result = await activity.RunAsync(new ResolveDigestDispatchInput(23, 3));

        Assert.False(result.ShouldProceed);
        Assert.Equal(ResolveDigestDispatchActivity.EmailGatewayRefusedDecision, result.Decision);
        Assert.Contains("EmailGateWayType 7", result.Reason);
        Assert.Empty(result.Groups);
        Assert.Null(result.EmailGatewayId);
        artifacts.Verify(a => a.GetForDispatchAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
