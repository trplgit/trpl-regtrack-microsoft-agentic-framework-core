using Insights.Data;
using Insights.Domain;
using Insights.Worker;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Logging.Abstractions;
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
///
/// <para>[2026-09-29] The activity groups recipients by their plain SQL scope signature again (the
/// 2026-09-27 entitlement API step was removed), so these also cover that grouping.</para>
/// </summary>
public sealed class ResolveDigestRecipientsActivityTests
{
    private const int Tenant = 23;
    private static readonly DateTime AsOf = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<ScopePair> TenantWide = [new ScopePair(16873, 1), new ScopePair(16875, 1)];

    private static FreeDigestRecipient R(long id) => new(id, $"user{id}@example.com", $"User {id}");

    private static ResolveDigestRecipientsActivity Build(
        IReadOnlyList<FreeDigestRecipient> recipients,
        IReadOnlyList<long> claimed,
        IReadOnlyList<long>? jsonClaimed,
        IReadOnlyDictionary<int, IReadOnlyList<ScopePair>>? scopeByUser = null,
        IReadOnlyDictionary<int, IReadOnlyList<LicenceScopePair>>? licencesByUser = null)
    {
        var repo = new Mock<IFreeDigestRepository>();
        var jsonRepo = new Mock<IInsightJsonRepository>();
        var scope = new Mock<IScopeRepository>();

        repo.Setup(r => r.GetEntitledTenantsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FreeDigestTenant(Tenant, "ABC Training Company")]);

        repo.Setup(r => r.EvaluateGateAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeDigestGateResult(
                Tenant, EntitlementDecision.Proceed, recipients.Count, "Entitled.", true));

        repo.Setup(r => r.GetRecipientsAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(recipients);

        repo.Setup(r => r.GetClaimedUserIdsAsync(Tenant, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(claimed);
        jsonRepo.Setup(r => r.GetClaimedUserIdsAsync(Tenant, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jsonClaimed ?? []);

        // Everyone tenant-wide (one group) unless a test says otherwise.
        scope.Setup(s => s.GetScopePairsAsync(It.IsAny<int>(), Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((int userId, int _, CancellationToken _) =>
                scopeByUser is not null && scopeByUser.TryGetValue(userId, out var pairs) ? pairs : TenantWide);

        // No licence assignment unless a test says otherwise.
        scope.Setup(s => s.GetLicenceScopePairsAsync(It.IsAny<int>(), Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync((int userId, int _, CancellationToken _) =>
                licencesByUser is not null && licencesByUser.TryGetValue(userId, out var licences)
                    ? licences
                    : (IReadOnlyList<LicenceScopePair>)[]);

        return new ResolveDigestRecipientsActivity(
            repo.Object, jsonRepo.Object, scope.Object, new FreeDigestMetrics(), NullLogger<ResolveDigestRecipientsActivity>.Instance);
    }

    private static List<long> Members(ResolveDigestRecipientsOutput result) =>
        result.Groups.SelectMany(g => g.Recipients).Select(r => r.UserId).ToList();

    /// <summary>
    /// THE ONE THAT MATTERS. Every recipient already claimed => no group, so the orchestrator
    /// composes nothing and spends nothing.
    /// </summary>
    [Fact]
    public async Task AllRecipientsAlreadyClaimed_ProducesNoGroups()
    {
        var activity = Build([R(357), R(1024)], claimed: [357, 1024], jsonClaimed: []);

        var result = await activity.RunAsync(new ResolveDigestRecipientsInput(Tenant, AsOf.ToString("O")));

        Assert.False(result.ShouldProceed);
        Assert.Empty(result.Groups);
        Assert.Equal(EntitlementDecision.ExitNoRecipients.ToString(), result.Decision);
    }

    /// <summary>Partially claimed: only the unclaimed recipients are grouped.</summary>
    [Fact]
    public async Task PartiallyClaimed_ExcludesOnlyTheClaimedRecipients()
    {
        var activity = Build([R(357), R(1024), R(11782)], claimed: [1024], jsonClaimed: []);

        var result = await activity.RunAsync(new ResolveDigestRecipientsInput(Tenant, AsOf.ToString("O")));

        Assert.True(result.ShouldProceed);

        var members = Members(result);
        Assert.Equal(2, members.Count);
        Assert.DoesNotContain(1024L, members);
        Assert.Contains(357L, members);
        Assert.Contains(11782L, members);
    }

    /// <summary>Nothing claimed: unchanged behaviour, so the filter cannot break a normal week.</summary>
    [Fact]
    public async Task NothingClaimed_KeepsEveryRecipient()
    {
        var activity = Build([R(357), R(1024)], claimed: [], jsonClaimed: []);

        var result = await activity.RunAsync(new ResolveDigestRecipientsInput(Tenant, AsOf.ToString("O")));

        Assert.True(result.ShouldProceed);
        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Recipients.Count);
        Assert.Equal(357, group.RepresentativeUserId);   // the first-seen member represents the group
        Assert.Equal("user357@example.com", group.Recipients[0].Email);
    }

    /// <summary>Different SQL scopes split into separate groups; an empty scope is counted, never grouped.</summary>
    [Fact]
    public async Task RecipientsAreGroupedBySqlScope_AndAnEmptyScopeIsCountedNotGrouped()
    {
        var scopes = new Dictionary<int, IReadOnlyList<ScopePair>>
        {
            [357] = TenantWide,
            [1024] = [new ScopePair(16875, 1), new ScopePair(16873, 1)],   // same set, other order
            [11782] = [new ScopePair(17027, 2)],
            [20000] = [],
        };
        var activity = Build([R(357), R(1024), R(11782), R(20000)], claimed: [], jsonClaimed: [], scopes);

        var result = await activity.RunAsync(new ResolveDigestRecipientsInput(Tenant, AsOf.ToString("O")));

        Assert.True(result.ShouldProceed);
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(new[] { 357L, 1024L }, result.Groups[0].Recipients.Select(r => r.UserId));
        Assert.Equal(ScopeSignature.For(TenantWide), result.Groups[0].ScopeSignature);
        Assert.Equal(11782L, Assert.Single(result.Groups[1].Recipients).UserId);
        Assert.Equal(1, result.RecipientsWithoutScope);
        Assert.DoesNotContain(20000L, Members(result));
    }

    /// <summary>
    /// [2026-09-29] The leak this closes: same compliance scope, DIFFERENT licence assignments. One
    /// shared email would carry the representative's licences to a user not assigned them, so the
    /// two must land in separate groups. Same licences (any order) still share.
    /// </summary>
    [Fact]
    public async Task SameComplianceScope_DifferentLicenceScope_SplitsIntoSeparateGroups()
    {
        var licences = new Dictionary<int, IReadOnlyList<LicenceScopePair>>
        {
            [357]   = [new LicenceScopePair(16873, 4), new LicenceScopePair(16875, 4)],
            [1024]  = [new LicenceScopePair(16875, 4), new LicenceScopePair(16873, 4)],   // same set, other order
            [11782] = [new LicenceScopePair(16873, 4)],                                  // fewer licences
            [20000] = [],                                                                // none at all
        };
        var activity = Build([R(357), R(1024), R(11782), R(20000)], claimed: [], jsonClaimed: [], licencesByUser: licences);

        var result = await activity.RunAsync(new ResolveDigestRecipientsInput(Tenant, AsOf.ToString("O")));

        Assert.Equal(3, result.Groups.Count);
        Assert.Equal(new[] { 357L, 1024L }, result.Groups[0].Recipients.Select(r => r.UserId));
        Assert.Equal(11782L, Assert.Single(result.Groups[1].Recipients).UserId);
        Assert.Equal(20000L, Assert.Single(result.Groups[2].Recipients).UserId);
        // A user with no licence assignment keeps the compliance-only signature.
        Assert.Equal(ScopeSignature.For(TenantWide), result.Groups[2].ScopeSignature);
    }

    [Fact]
    public async Task InsightJsonClaimDomainIgnoresEmailClaims()
    {
        var activity = Build([R(357), R(1024)], claimed: [357, 1024], jsonClaimed: []);

        var result = await activity.RunAsync(new ResolveDigestRecipientsInput(
            Tenant, AsOf.ToString("O"), DigestClaimDomain.InsightJson));

        Assert.True(result.ShouldProceed);
        Assert.Equal(2, Members(result).Count);
    }

    [Fact]
    public async Task InsightJsonClaimDomainExcludesOnlyJsonClaims()
    {
        var activity = Build([R(357), R(1024)], claimed: [], jsonClaimed: [1024]);

        var result = await activity.RunAsync(new ResolveDigestRecipientsInput(
            Tenant, AsOf.ToString("O"), DigestClaimDomain.InsightJson));

        var members = Members(result);
        Assert.Single(members);
        Assert.Equal(357L, members[0]);
    }

    [Fact]
    public async Task EmailClaimDomainIgnoresJsonClaims()
    {
        var activity = Build([R(357), R(1024)], claimed: [], jsonClaimed: [1024]);

        var result = await activity.RunAsync(new ResolveDigestRecipientsInput(
            Tenant, AsOf.ToString("O"), DigestClaimDomain.Email));

        Assert.True(result.ShouldProceed);
        Assert.Equal(2, Members(result).Count);
    }
}
