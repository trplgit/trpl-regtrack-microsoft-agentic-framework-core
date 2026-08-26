using Insights.Data;
using Insights.Domain;
using Insights.Worker;
using Insights.Worker.Orchestration.Activities;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// The cost boundary from design doc Sec.5.3: "resolve recipients ... NONE => EXIT before any
/// aggregation or LLM call".
///
/// These exist because the claim was originally only checked inside SendDigestActivity, i.e.
/// AFTER composition. Observed live on tenant 23: a re-run of an already-sent week made three
/// LLM calls and then reported Sent:0, Skipped:4 - full token cost, zero emails. Nothing failed,
/// so nothing surfaced; only the HTTP log showed it. At ~600 tenants that is the whole weekly
/// LLM bill repeated for every deploy inside the send window (Sec.10.5).
/// </summary>
public sealed class ResolveDigestRecipientsActivityTests
{
    private const int Tenant = 23;
    private static readonly DateTime AsOf = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static FreeDigestRecipient R(long id) => new(id, $"user{id}@example.com", $"User {id}");

    private static ResolveDigestRecipientsActivity Build(
        IReadOnlyList<FreeDigestRecipient> recipients,
        IReadOnlyList<long> claimed,
        out Mock<IScopeRepository> scope)
    {
        var repo = new Mock<IFreeDigestRepository>();

        repo.Setup(r => r.GetEntitledTenantsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FreeDigestTenant(Tenant, "ABC Training Company")]);

        repo.Setup(r => r.EvaluateGateAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeDigestGateResult(
                Tenant, EntitlementDecision.Proceed, recipients.Count, "Entitled.", true));

        repo.Setup(r => r.GetRecipientsAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(recipients);

        repo.Setup(r => r.GetClaimedUserIdsAsync(Tenant, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(claimed);

        scope = new Mock<IScopeRepository>();
        // Every user shares one scope, so grouping collapses to a single group - keeps these
        // tests about the claim filter rather than about signature grouping.
        scope.Setup(s => s.GetScopePairsAsync(It.IsAny<int>(), Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ScopePair(10, 1)]);

        return new ResolveDigestRecipientsActivity(repo.Object, scope.Object, new FreeDigestMetrics());
    }

    /// <summary>
    /// THE ONE THAT MATTERS. Every recipient already claimed => no groups at all, so the
    /// orchestrator has nothing to compose and spends nothing.
    /// </summary>
    [Fact]
    public async Task AllRecipientsAlreadyClaimed_ProducesNoGroupsToCompose()
    {
        var activity = Build([R(357), R(1024)], claimed: [357, 1024], out _);

        var result = await activity.RunAsync(new ResolveDigestRecipientsInput(Tenant, AsOf.ToString("O")));

        Assert.False(result.ShouldProceed);
        Assert.Empty(result.Groups);
        Assert.Equal(EntitlementDecision.ExitNoRecipients.ToString(), result.Decision);
    }

    /// <summary>Partially claimed: only the unclaimed recipient survives into a group.</summary>
    [Fact]
    public async Task PartiallyClaimed_ExcludesOnlyTheClaimedRecipients()
    {
        var activity = Build([R(357), R(1024), R(11782)], claimed: [1024], out _);

        var result = await activity.RunAsync(new ResolveDigestRecipientsInput(Tenant, AsOf.ToString("O")));

        Assert.True(result.ShouldProceed);

        var members = result.Groups.SelectMany(g => g.Recipients).Select(r => r.UserId).ToList();
        Assert.Equal(2, members.Count);
        Assert.DoesNotContain(1024L, members);
        Assert.Contains(357L, members);
        Assert.Contains(11782L, members);
    }

    /// <summary>Nothing claimed: unchanged behaviour, so the filter cannot break a normal week.</summary>
    [Fact]
    public async Task NothingClaimed_KeepsEveryRecipient()
    {
        var activity = Build([R(357), R(1024)], claimed: [], out _);

        var result = await activity.RunAsync(new ResolveDigestRecipientsInput(Tenant, AsOf.ToString("O")));

        Assert.True(result.ShouldProceed);
        Assert.Equal(2, result.Groups.SelectMany(g => g.Recipients).Count());
    }

    /// <summary>
    /// The saving is only real if the expensive lookups are never reached. Scope resolution is
    /// the first per-recipient cost after the filter, so an all-claimed tenant must not touch it.
    /// </summary>
    [Fact]
    public async Task AllRecipientsAlreadyClaimed_DoesNotResolveScope()
    {
        var activity = Build([R(357), R(1024)], claimed: [357, 1024], out var scope);

        await activity.RunAsync(new ResolveDigestRecipientsInput(Tenant, AsOf.ToString("O")));

        scope.Verify(
            s => s.GetScopePairsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
