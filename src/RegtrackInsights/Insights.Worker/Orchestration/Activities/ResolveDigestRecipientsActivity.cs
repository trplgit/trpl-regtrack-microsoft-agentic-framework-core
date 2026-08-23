using DurableTask.Core;
using Insights.Data;
using Insights.Domain;
using Insights.Worker;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ResolveDigestRecipientsInput(int TenantId, string? AsOf);

/// <summary>One recipient the digest will be delivered to.</summary>
public sealed record DigestRecipientRef(long UserId, string Email, string? Name);

/// <summary>
/// Recipients who share an identical authorised scope, and therefore share one generated digest.
/// <paramref name="RepresentativeUserId"/> is the user the aggregates are computed for - every
/// member of the group would produce the same fifteen numbers.
/// </summary>
public sealed record DigestScopeGroup(string ScopeSignature, int RepresentativeUserId, IReadOnlyList<DigestRecipientRef> Recipients);

public sealed record ResolveDigestRecipientsOutput(
    bool ShouldProceed,
    string Decision,
    string Reason,
    string TenantName,
    string WeekEnding,
    IReadOnlyList<DigestScopeGroup> Groups,
    int RecipientsWithoutScope);

/// <summary>
/// Node 1 of the free digest orchestration: gate the tenant, resolve its recipients, and group
/// them by authorised scope.
///
/// Everything non-deterministic for the whole run happens HERE - the entitlement gate, the
/// recipient query, the scope lookups, and the clock read that fixes the week-ending date. The
/// orchestrator body downstream is pure control flow over what this returns, which is what makes
/// replay safe (CLAUDE.md 6).
/// </summary>
public sealed class ResolveDigestRecipientsActivity(
    IFreeDigestRepository repository,
    IScopeRepository scope,
    FreeDigestMetrics metrics)
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

        var groups = new Dictionary<string, (int RepresentativeUserId, List<DigestRecipientRef> Members)>(StringComparer.Ordinal);
        var withoutScope = 0;

        foreach (var recipient in recipients)
        {
            /*  [FAIL CLOSED] A recipient with no authorised pairs would otherwise receive a
                perfectly well-formed email full of zeros - sql/06 returns nothing for an empty
                scope rather than throwing, and an all-zero digest looks fine. Dropped here, and
                counted so the run reports it.                                                   */
            var scopedUserId = checked((int)recipient.UserId);
            var pairs = await scope.GetScopePairsAsync(scopedUserId, input.TenantId);
            var signature = ScopeSignature.For(pairs);

            if (signature.Length == 0)
            {
                withoutScope++;
                metrics.RecordSkipped(FreeDigestSkipReason.NoScope);
                continue;
            }

            if (!groups.TryGetValue(signature, out var group))
            {
                group = (scopedUserId, []);
                groups[signature] = group;
            }

            group.Members.Add(new DigestRecipientRef(recipient.UserId, recipient.Email, recipient.Name));
        }

        var result = groups
            .Select(kv => new DigestScopeGroup(kv.Key, kv.Value.RepresentativeUserId, kv.Value.Members))
            .ToList();

        return new ResolveDigestRecipientsOutput(
            true, gate.Decision.ToString(), gate.Reason, tenant.TenantName, weekEnding, result, withoutScope);
    }
    /// <summary>Maps the gate's verdict onto the closed set of skip reasons the metric carries.</summary>
    private static FreeDigestSkipReason SkipReasonFor(EntitlementDecision decision) => decision switch
    {
        EntitlementDecision.ExitSuperseded => FreeDigestSkipReason.Superseded,
        EntitlementDecision.ExitNoRecipients => FreeDigestSkipReason.NoRecipients,
        _ => FreeDigestSkipReason.NotEntitled,
    };
}
