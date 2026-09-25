using DurableTask.Core;
using Insights.Data;
using Insights.Data.Email;
using Insights.Domain;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ResolveDigestDispatchInput(int TenantId, int ArtifactFreshnessDays);

public sealed record DispatchRecipientRef(long UserId, string Email, string? Name);

public sealed record DispatchGroup(FreeDigestArtifact Artifact, IReadOnlyList<DispatchRecipientRef> Recipients);

/// <param name="EmailGatewayId">
/// The EmailGatewayMaster ID every recipient of this tenant is sent through this run. Nullable,
/// and last, so a history recorded before per-tenant routing still deserialises - null means "not
/// resolved here", and SendDigestFromArtifactActivity then resolves it live. Null NEVER means a
/// default provider.
/// </param>
public sealed record ResolveDigestDispatchOutput(
    bool ShouldProceed, string Decision, string Reason, string TenantName,
    IReadOnlyList<DispatchGroup> Groups, int RecipientsSkippedNoMatchingArtifact, int RecipientsSkippedNoScope,
    int? EmailGatewayId = null);

/// <summary>
/// MONDAY, node 1: the re-gate. Everything Sunday resolved is treated as a CLAIM, never a grant
/// (ADR-0001 D5) - the design spec's own rule that entitlement is evaluated at execution time,
/// never cached at scheduling time, applies here exactly as it does to a fresh Sunday run.
///
/// Re-checks, cheapest first:
///   1. Tenant gate (entitled, not superseded) - unchanged from today's EvaluateGateAsync.
///   1b. Email gateway (2026-09-25) - which provider this tenant's mail goes through, read LIVE
///      from dbo.EmailDeliveryGatewayCustomization so an ops switch flipped since Sunday applies.
///      A configuration that cannot be trusted refuses the whole tenant here, before any
///      recipient is claimed (EmailGatewayRules). Resolved once per tenant per run: a switch
///      flipped mid-send applies from the next tenant/tick, not mid-tenant.
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
    IFreeDigestRepository repository, IFreeDigestArtifactRepository artifacts, IScopeRepository scope,
    IEmailGatewayResolver gatewayResolver, ILogger<ResolveDigestDispatchActivity> logger)
    : AsyncTaskActivity<ResolveDigestDispatchInput, ResolveDigestDispatchOutput>
{
    /// <summary>The Decision value when the tenant's email gateway configuration is refused.</summary>
    public const string EmailGatewayRefusedDecision = "EmailGatewayRefused";

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

        var gateway = await gatewayResolver.ResolveAsync(input.TenantId);
        foreach (var warning in gateway.Warnings)
            logger.LogWarning("ResolveDigestDispatchActivity: {Warning}", warning);

        if (gateway.Gateway is not { } resolvedGateway)
        {
            // Fail closed, fail loudly: a gateway row nobody can interpret is a config error to
            // fix, not a reason to guess a provider for this tenant's customers.
            logger.LogError("ResolveDigestDispatchActivity: {Detail} Nothing is sent for this tenant this run.", gateway.Detail);
            return new ResolveDigestDispatchOutput(
                false, EmailGatewayRefusedDecision, gateway.Detail, tenant.TenantName, [], 0, 0);
        }

        logger.LogInformation("ResolveDigestDispatchActivity: {Detail} (source {Source}).", gateway.Detail, gateway.Source);
        var gatewayId = (int)resolvedGateway;

        var pendingArtifacts = await artifacts.GetForDispatchAsync(input.TenantId, input.ArtifactFreshnessDays);
        if (pendingArtifacts.Count == 0)
        {
            return new ResolveDigestDispatchOutput(
                true, gate.Decision.ToString(), "No undispatched, in-freshness artifacts for this tenant.",
                tenant.TenantName, [], 0, 0, gatewayId);
        }

        /*  [BUG FOUND LIVE, 2026-09-11] GetForDispatchAsync returns every COMPLETE, undispatched,
            in-freshness artifact for the tenant - it does not guarantee at most one per scope
            group. A plain ToDictionary(a => a.ScopeSignature) THROWS "An item with the same key
            has already been added" the moment two complete artifacts share a signature - which
            happens whenever GENERATE has produced more than one un-dispatched artifact for the
            same scope group within the freshness window (a manual re-run, a retried Sunday tick
            with different scope-group cardinality, or - confirmed live - two GENERATE runs
            targeting different weeks close enough together to both still be "fresh"). That is an
            ordinary operational scenario, not a corrupt-data scenario, so it must not crash the
            whole tenant's Monday send - group and keep the most recently generated artifact per
            signature instead. The others are silently correct to drop: a newer generation is
            never wrong content to prefer over an older one for the same authorised scope.        */
        var artifactBySignature = pendingArtifacts
            .GroupBy(a => a.ScopeSignature, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var ordered = g.OrderByDescending(a => a.GeneratedAtUtc).ToList();
                    if (ordered.Count > 1)
                        logger.LogWarning(
                            "ResolveDigestDispatchActivity: tenant {TenantId} scope {ScopeSignature} - {Count} undispatched artifacts within the freshness window, dispatching the most recent ({ArtifactId}, generated {GeneratedAtUtc}); {DroppedCount} older one(s) skipped.",
                            input.TenantId, g.Key, ordered.Count, ordered[0].ArtifactId, ordered[0].GeneratedAtUtc, ordered.Count - 1);
                    return ordered[0];
                },
                StringComparer.Ordinal);

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
            true, gate.Decision.ToString(), gate.Reason, tenant.TenantName, groups, noMatch, noScope, gatewayId);
    }
}
