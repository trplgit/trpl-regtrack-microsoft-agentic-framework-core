using DurableTask.Core;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ResolveDigestDispatchInput(int TenantId, int ArtifactFreshnessDays);

public sealed record DispatchRecipientRef(long UserId, string Email, string? Name);

public sealed record DispatchGroup(FreeDigestArtifact Artifact, IReadOnlyList<DispatchRecipientRef> Recipients);

public sealed record ResolveDigestDispatchOutput(
    bool ShouldProceed, string Decision, string Reason, string TenantName,
    IReadOnlyList<DispatchGroup> Groups, int RecipientsSkippedNoMatchingArtifact, int RecipientsSkippedNoScope);

/// <summary>
/// MONDAY, node 1: the re-gate. Everything Sunday resolved is treated as a CLAIM, never a grant
/// (ADR-0001 D5) - the design spec's own rule that entitlement is evaluated at execution time,
/// never cached at scheduling time, applies here exactly as it does to a fresh Sunday run.
///
/// Re-checks, cheapest first:
///   1. Tenant gate (entitled, not superseded) - unchanged from today's EvaluateGateAsync.
///   2. Recipients, resolved LIVE - repository.GetRecipientsAsync already excludes anyone
///      suppressed (unsubscribed/bounced) or deleted since Sunday.
///   3. Scope signature, RECOMPUTED live and matched against a Sunday artifact by that same
///      signature. A recipient whose scope changed since Sunday recomputes to a DIFFERENT
///      signature, so they simply match no artifact and are silently skipped this week - correct
///      per ADR-0001 D5 (equality is stricter than a true subset test, but fails closed, which is
///      the right direction to be wrong in).
///
/// A recipient who fails any of these never forms an intent to send, so nothing is claimed and
/// nothing needs releasing - the next Monday tick re-evaluates from live data, which may have
/// changed back.
/// </summary>
public sealed class ResolveDigestDispatchActivity(
    IFreeDigestRepository repository, IFreeDigestArtifactRepository artifacts, IScopeRepository scope)
    : AsyncTaskActivity<ResolveDigestDispatchInput, ResolveDigestDispatchOutput>
{
    protected override Task<ResolveDigestDispatchOutput> ExecuteAsync(TaskContext context, ResolveDigestDispatchInput input) => RunAsync(input);

    internal async Task<ResolveDigestDispatchOutput> RunAsync(ResolveDigestDispatchInput input)
    {
        var tenants = await repository.GetEntitledTenantsAsync();
        var tenant = tenants.FirstOrDefault(t => t.CustomerId == input.TenantId)
                     ?? new FreeDigestTenant(input.TenantId, $"Tenant {input.TenantId}");

        var gate = await repository.EvaluateGateAsync(input.TenantId);
        if (!gate.ShouldProceed)
        {
            return new ResolveDigestDispatchOutput(
                false, gate.Decision.ToString(), gate.Reason, tenant.TenantName, [], 0, 0);
        }

        var pendingArtifacts = await artifacts.GetForDispatchAsync(input.TenantId, input.ArtifactFreshnessDays);
        if (pendingArtifacts.Count == 0)
        {
            return new ResolveDigestDispatchOutput(
                true, gate.Decision.ToString(), "No undispatched, in-freshness artifacts for this tenant.",
                tenant.TenantName, [], 0, 0);
        }

        var artifactBySignature = pendingArtifacts.ToDictionary(a => a.ScopeSignature, StringComparer.Ordinal);

        var recipients = await repository.GetRecipientsAsync(input.TenantId);

        var recipientsBySignature = new Dictionary<string, List<DispatchRecipientRef>>(StringComparer.Ordinal);
        var noScope = 0;

        foreach (var recipient in recipients)
        {
            var scopedUserId = checked((int)recipient.UserId);
            var pairs = await scope.GetScopePairsAsync(scopedUserId, input.TenantId);
            var signature = ScopeSignature.For(pairs);

            if (signature.Length == 0)
            {
                noScope++;
                continue;
            }

            if (!recipientsBySignature.TryGetValue(signature, out var list))
            {
                list = [];
                recipientsBySignature[signature] = list;
            }
            list.Add(new DispatchRecipientRef(recipient.UserId, recipient.Email, recipient.Name));
        }

        var groups = new List<DispatchGroup>();
        var noMatch = 0;

        foreach (var (signature, groupRecipients) in recipientsBySignature)
        {
            if (artifactBySignature.TryGetValue(signature, out var artifact))
            {
                groups.Add(new DispatchGroup(artifact, groupRecipients));
            }
            else
            {
                // Either this scope group never got an artifact generated on Sunday (a partial
                // fleet run), or every recipient in it changed scope since then. Either way, no
                // safe content exists to send them this week - skip, not fail.
                noMatch += groupRecipients.Count;
            }
        }

        return new ResolveDigestDispatchOutput(
            true, gate.Decision.ToString(), gate.Reason, tenant.TenantName, groups, noMatch, noScope);
    }
}
