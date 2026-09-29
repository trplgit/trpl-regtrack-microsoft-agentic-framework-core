using Insights.Data;
using Insights.Data.Email;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// The email gateway gate (2026-09-25): resolved live after the entitlement gate, before anyone is
/// claimed. [2026-09-29] Scope is re-resolved live here too (plain SQL scope - the entitlement API
/// step was removed) and matched to Sunday's artifacts inside the activity.
/// </summary>
public sealed class ResolveDigestDispatchActivityTests
{
    private static readonly IReadOnlyList<ScopePair> Pairs = [new ScopePair(16873, 1)];
    private static readonly string Signature = ScopeSignature.For(Pairs);

    private static FreeDigestArtifact Artifact(string signature, DateTime generatedAtUtc) =>
        new(Guid.NewGuid(), 23, new DateOnly(2026, 8, 23), signature, 357,
            "ABC Training", generatedAtUtc, generatedAtUtc, "llm", "c", "p", [], "k", "v");

    private static (ResolveDigestDispatchActivity Activity, Mock<IFreeDigestArtifactRepository> Artifacts, Mock<IScopeRepository> Scope) Build(
        EmailGatewayResolution resolution, IReadOnlyList<FreeDigestArtifact>? pending = null)
    {
        var repo = new Mock<IFreeDigestRepository>();
        repo.Setup(r => r.GetEntitledTenantsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new FreeDigestTenant(23, "ABC Training")]);
        repo.Setup(r => r.EvaluateGateAsync(23, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeDigestGateResult(23, EntitlementDecision.Proceed, 1, "ok", true));
        repo.Setup(r => r.GetRecipientsAsync(23, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FreeDigestRecipient(357, "someone@example.com", "Someone")]);

        var artifacts = new Mock<IFreeDigestArtifactRepository>();
        artifacts.Setup(a => a.GetForDispatchAsync(23, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending ?? [Artifact(Signature, DateTime.UtcNow)]);

        var scope = new Mock<IScopeRepository>();
        scope.Setup(s => s.GetScopePairsAsync(357, 23, It.IsAny<CancellationToken>())).ReturnsAsync(Pairs);
        scope.Setup(s => s.GetLicenceScopePairsAsync(357, 23, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<LicenceScopePair>)[]);

        var resolver = new Mock<IEmailGatewayResolver>();
        resolver.Setup(r => r.ResolveAsync(23, It.IsAny<CancellationToken>())).ReturnsAsync(resolution);

        var activity = new ResolveDigestDispatchActivity(repo.Object, artifacts.Object, scope.Object, resolver.Object,
            NullLogger<ResolveDigestDispatchActivity>.Instance);

        return (activity, artifacts, scope);
    }

    [Fact]
    public async Task TheResolvedGatewayIsReturnedWithTheMatchedGroups()
    {
        var (activity, _, _) = Build(new EmailGatewayResolution(EmailGateway.SendGrid, EmailGatewaySource.FailoverSwitch, "switch", []));

        var result = await activity.RunAsync(new ResolveDigestDispatchInput(23, 3));

        Assert.True(result.ShouldProceed);
        var group = Assert.Single(result.Groups);
        Assert.Equal(357L, Assert.Single(group.Recipients).UserId);
        Assert.Equal((int)EmailGateway.SendGrid, result.EmailGatewayId);
    }

    /// <summary>A refused configuration stops the whole tenant before any artifact is read or recipient claimed.</summary>
    [Fact]
    public async Task ARefusedGateway_RefusesTheTenant()
    {
        var (activity, artifacts, scope) = Build(new EmailGatewayResolution(null, EmailGatewaySource.Refused, "unknown EmailGateWayType 7", []));

        var result = await activity.RunAsync(new ResolveDigestDispatchInput(23, 3));

        Assert.False(result.ShouldProceed);
        Assert.Equal(ResolveDigestDispatchActivity.EmailGatewayRefusedDecision, result.Decision);
        Assert.Contains("EmailGateWayType 7", result.Reason);
        Assert.Empty(result.Groups);
        Assert.Null(result.EmailGatewayId);
        artifacts.Verify(a => a.GetForDispatchAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        scope.Verify(s => s.GetScopePairsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>No artifact waiting: nobody's scope is resolved - it would cost SQL for nothing.</summary>
    [Fact]
    public async Task NoArtifacts_ResolvesNoScope()
    {
        var (activity, _, scope) = Build(new EmailGatewayResolution(EmailGateway.SendGrid, EmailGatewaySource.Default, "default", []), []);

        var result = await activity.RunAsync(new ResolveDigestDispatchInput(23, 3));

        Assert.True(result.ShouldProceed);
        Assert.Empty(result.Groups);
        scope.Verify(s => s.GetScopePairsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>[BUG FOUND LIVE, 2026-09-11] Two artifacts for one signature: the newest wins, nothing throws.</summary>
    [Fact]
    public async Task TwoArtifactsForOneSignature_KeepsTheNewest()
    {
        var older = Artifact(Signature, new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc));
        var newer = Artifact(Signature, new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc));
        var (activity, _, _) = Build(new EmailGatewayResolution(EmailGateway.SendGrid, EmailGatewaySource.Default, "default", []), [older, newer]);

        var result = await activity.RunAsync(new ResolveDigestDispatchInput(23, 3));

        Assert.Equal(newer.ArtifactId, Assert.Single(result.Groups).Artifact.ArtifactId);
    }

    /// <summary>A recipient whose scope changed since Sunday matches no artifact and is counted, never sent to.</summary>
    [Fact]
    public async Task ARecipientWhoseScopeChangedSinceSunday_MatchesNoArtifact()
    {
        var (activity, _, _) = Build(
            new EmailGatewayResolution(EmailGateway.SendGrid, EmailGatewaySource.Default, "default", []),
            [Artifact(ScopeSignature.For([new ScopePair(99999, 1)]), DateTime.UtcNow)]);

        var result = await activity.RunAsync(new ResolveDigestDispatchInput(23, 3));

        Assert.Empty(result.Groups);
        Assert.Equal(1, result.RecipientsSkippedNoMatchingArtifact);
    }

    /// <summary>A group is sent only the artifact of its EXACT signature; a group with none gets nothing.</summary>
    [Fact]
    public void MatchArtifacts_PairsOnlyExactSignatures()
    {
        var artifact = Artifact("sig", DateTime.UtcNow);
        var matched = new DigestScopeGroup("sig", 1, [new DigestRecipientRef(1, "a@example.com", null)]);
        var changed = new DigestScopeGroup("other", 2, [new DigestRecipientRef(2, "b@example.com", null), new DigestRecipientRef(3, "c@example.com", null)]);

        var (groups, withoutArtifact) = ResolveDigestDispatchActivity.MatchArtifacts([artifact], [matched, changed]);

        var group = Assert.Single(groups);
        Assert.Equal(artifact.ArtifactId, group.Artifact.ArtifactId);
        Assert.Equal(1L, Assert.Single(group.Recipients).UserId);
        Assert.Equal(2, withoutArtifact);
    }
}
