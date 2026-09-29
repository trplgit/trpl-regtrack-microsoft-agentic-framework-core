using DurableTask.Core;
using Insights.Data;
using Insights.Domain;
using Insights.Worker;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public enum DigestClaimDomain
{
    Email,
    InsightJson,
}

public sealed record ResolveDigestRecipientsInput(
    int TenantId,
    string? AsOf,
    DigestClaimDomain ClaimDomain = DigestClaimDomain.Email);

/// <summary>One recipient the digest will be delivered to.</summary>
public sealed record DigestRecipientRef(long UserId, string Email, string? Name);

/// <summary>
/// Recipients who share an identical (branch, category) scope, and therefore share one generated
/// digest. <paramref name="RepresentativeUserId"/> is the user the procs read as; every member of
/// the group would produce the same numbers.
/// </summary>
public sealed record DigestScopeGroup(
    string ScopeSignature, int RepresentativeUserId, IReadOnlyList<DigestRecipientRef> Recipients);

/// <param name="Groups">Recipients grouped by the signature of their SQL scope pairs, in first-seen order.</param>
/// <param name="RecipientsWithoutScope">Recipients dropped because they hold no EntitiesAssignment pair on this tenant.</param>
public sealed record ResolveDigestRecipientsOutput(
    bool ShouldProceed,
    string Decision,
    string Reason,
    string TenantName,
    string WeekEnding,
    IReadOnlyList<DigestScopeGroup> Groups,
    int RecipientsWithoutScope);

/// <summary>
/// Node 1 of the free digest orchestration: gate the tenant, resolve its recipients, and group them
/// by scope.
///
/// Everything non-deterministic about WHO for the whole run happens HERE - the entitlement gate,
/// the recipient query, the already-claimed filter, each recipient's scope pairs, and the clock
/// read that fixes the week-ending date. Recipients are grouped by the signature of their SQL
/// scope (EntitiesAssignment pairs), so the orchestrator composes once per distinct scope.
///
/// <para>[2026-09-29] The RegTrack show-entitlements API check added 2026-09-27 was removed on the
/// product owner's instruction; a recipient's scope is again their plain SQL scope.</para>
/// </summary>
public sealed class ResolveDigestRecipientsActivity(
    IFreeDigestRepository repository,
    IInsightJsonRepository insightJsonRepository,
    IScopeRepository scopeRepository,
    FreeDigestMetrics metrics,
    ILogger<ResolveDigestRecipientsActivity> logger)
    : AsyncTaskActivity<ResolveDigestRecipientsInput, ResolveDigestRecipientsOutput>
{
    protected override Task<ResolveDigestRecipientsOutput> ExecuteAsync(TaskContext context, ResolveDigestRecipientsInput input) => RunAsync(input);

    internal async Task<ResolveDigestRecipientsOutput> RunAsync(ResolveDigestRecipientsInput input)
    {
        var asOf = string.IsNullOrWhiteSpace(input.AsOf) ? DateTime.UtcNow : DateTime.Parse(input.AsOf);

        /*  [TRAP] THE CLOCK READ BELONGS HERE, NOT IN THE ORCHESTRATOR.
            WeekEndingFor reads the current date, and the result is the third part of the claim
            key. Computed in the orchestrator body it would be recalculated on every replay, so a
            replay spanning midnight on a Sunday would produce a different key and re-send the
            whole tenant. Fixed once here and threaded through as a string.                      */
        var weekEnding = DigestWeek.EndingFor(asOf).ToString("yyyy-MM-dd");

        var tenants = await repository.GetEntitledTenantsAsync();
        var tenant = tenants.FirstOrDefault(t => t.CustomerId == input.TenantId)
                     ?? new FreeDigestTenant(input.TenantId, $"Tenant {input.TenantId}");

        // Gate first. An unentitled or superseded tenant costs nothing beyond this call.
        var gate = await repository.EvaluateGateAsync(input.TenantId);
        if (!gate.ShouldProceed)
        {
            metrics.RecordSkipped(SkipReasonFor(gate.Decision));
            return new ResolveDigestRecipientsOutput(false, gate.Decision.ToString(), gate.Reason, tenant.TenantName, weekEnding, [], 0);
        }

        var recipients = await repository.GetRecipientsAsync(input.TenantId);

        /*  [DESIGN DOC Sec.5.3 - THE COST BOUNDARY] Subtract recipients who already hold this
            week's claim, BEFORE anything is aggregated or composed.

            The spec puts the boundary here explicitly: "resolve recipients ... NONE => EXIT
            before any aggregation or LLM call", and "steps 5-7 - the only steps that cost
            anything - run ONLY when there is a real, entitled, opted-in recipient".

            Without this the claim was only consulted inside SendDigestActivity, i.e. AFTER
            composition. Observed live: a re-run of an already-sent week made 3 LLM calls and then
            reported Sent:0, Skipped:4 - full token cost, zero emails. At ~600 tenants any deploy
            inside the send window, manual retry, or scheduler double-fire repeats the entire
            weekly LLM bill for nothing (Sec.10.5).

            The claim in SendDigestActivity STAYS. This filter is cheap and racy by nature - two
            workers resolving in the same instant both see an unclaimed recipient - and that race
            is exactly what the atomic claim is for. This saves the money; that guarantees
            at-most-once.                                                                        */
        var alreadyClaimed = input.ClaimDomain switch
        {
            DigestClaimDomain.Email => (await repository.GetClaimedUserIdsAsync(
                input.TenantId, DigestWeek.EndingFor(asOf))).ToHashSet(),
            DigestClaimDomain.InsightJson => (await insightJsonRepository.GetClaimedUserIdsAsync(
                input.TenantId, DigestWeek.EndingFor(asOf))).ToHashSet(),
            _ => throw new ArgumentOutOfRangeException(nameof(input.ClaimDomain), input.ClaimDomain, "Unknown digest claim domain."),
        };

        if (alreadyClaimed.Count > 0)
        {
            var before = recipients.Count;
            recipients = recipients.Where(r => !alreadyClaimed.Contains(r.UserId)).ToList();

            for (var i = recipients.Count; i < before; i++)
                metrics.RecordSkipped(FreeDigestSkipReason.AlreadySent);
        }

        if (recipients.Count == 0)
        {
            /*  Every recipient already served this week. This is the spec's step-4 exit, reached
                without spending a token - previously this same state cost a full set of LLM calls
                before anything noticed.                                                          */
            return new ResolveDigestRecipientsOutput(
                false,
                EntitlementDecision.ExitNoRecipients.ToString(),
                "Every recipient already holds this week's claim - exiting before any aggregation or LLM call.",
                tenant.TenantName, weekEnding, [], 0);
        }

        var (groups, withoutScope) = await GroupByScopeAsync(
            scopeRepository, input.TenantId, recipients.Select(r => new DigestRecipientRef(r.UserId, r.Email, r.Name)));

        for (var i = 0; i < withoutScope; i++)
            metrics.RecordSkipped(FreeDigestSkipReason.NoScope);

        logger.LogInformation(
            "ResolveDigestRecipientsActivity: tenant {TenantId} - {GroupCount} scope group(s), {WithoutScope} recipient(s) without scope, for week ending {WeekEnding}.",
            input.TenantId, groups.Count, withoutScope, weekEnding);

        return new ResolveDigestRecipientsOutput(
            true, gate.Decision.ToString(), gate.Reason, tenant.TenantName, weekEnding, groups, withoutScope);
    }

    /// <summary>
    /// Groups recipients by the signature of their SQL scope pairs - compliance AND licence
    /// (<see cref="ScopeSignature.For(IEnumerable{ScopePair}, IEnumerable{LicenceScopePair})"/>),
    /// in first-seen order; the first member of each group is its representative. A recipient with
    /// no pair is counted, never grouped - an empty scope is DENY, never "unrestricted"
    /// (<see cref="IScopeRepository.GetScopePairsAsync"/>).
    ///
    /// <para>Shared with the Monday re-resolve (<see cref="ResolveDigestDispatchActivity"/>) and the
    /// preview workers, so every caller groups exactly as the Sunday run does.</para>
    /// </summary>
    public static async Task<(IReadOnlyList<DigestScopeGroup> Groups, int WithoutScope)> GroupByScopeAsync(
        IScopeRepository scopeRepository, int tenantId, IEnumerable<DigestRecipientRef> recipients,
        CancellationToken cancellationToken = default)
    {
        var members = new Dictionary<string, List<DigestRecipientRef>>(StringComparer.Ordinal);
        var order = new List<string>();
        var withoutScope = 0;

        foreach (var recipient in recipients)
        {
            var userId = checked((int)recipient.UserId);
            var pairs = await scopeRepository.GetScopePairsAsync(userId, tenantId, cancellationToken);

            /*  [2026-09-29] Licence scope too: the email's licence figures are cut by
                LIC_EntitiesAssignment (sql/35), so users may only share an email when their
                licence assignments match as well - see ScopeSignature.For(pairs, licencePairs). */
            var licencePairs = pairs.Count == 0
                ? []
                : await scopeRepository.GetLicenceScopePairsAsync(userId, tenantId, cancellationToken);
            var signature = ScopeSignature.For(pairs, licencePairs);
            if (signature.Length == 0)
            {
                withoutScope++;
                continue;
            }

            if (!members.TryGetValue(signature, out var list))
            {
                list = [];
                members[signature] = list;
                order.Add(signature);
            }

            list.Add(recipient);
        }

        var groups = order
            .Select(signature => new DigestScopeGroup(signature, checked((int)members[signature][0].UserId), members[signature]))
            .ToList();

        return (groups, withoutScope);
    }

    /// <summary>Maps the gate's verdict onto the closed set of skip reasons the metric carries.</summary>
    private static FreeDigestSkipReason SkipReasonFor(EntitlementDecision decision) => decision switch
    {
        EntitlementDecision.ExitSuperseded => FreeDigestSkipReason.Superseded,
        EntitlementDecision.ExitNoRecipients => FreeDigestSkipReason.NoRecipients,
        _ => FreeDigestSkipReason.NotEntitled,
    };
}
