using Insights.Data;
using Insights.Domain;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <inheritdoc cref="IFreeDigestService"/>
public sealed class FreeDigestService(
    IFreeDigestRepository repository,
    IDictionaryRepository dictionary,
    IScopeRepository scope,
    FreeDigestPipeline pipeline,
    FreeDigestSettings settings,
    ILogger<FreeDigestService> logger) : IFreeDigestService
{
    public async Task<FreeDigestBatchResult> RunWeeklyAsync(DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        WarnIfRecipientOverrideActive();
        /*  Once, up front. A ComplianceStatus missing from the dictionary is a deployment-wide
            fault, not a per-tenant one - every status-derived figure would be computed against
            an incomplete map. Throws DictionaryCoverageException; nothing is sent.            */
        await dictionary.AssertStatusCoverageAsync(cancellationToken);

        var tenants = await repository.GetEntitledTenantsAsync(cancellationToken);
        logger.LogInformation("Free digest weekly run starting for {TenantCount} entitled tenant(s).", tenants.Count);

        var results = new List<FreeDigestTenantResult>(tenants.Count);

        foreach (var tenant in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            /*  Per-tenant catch is load-bearing. GetAggregatesAsync can throw
                FreeDigestDictionaryGapException, and one misconfigured tenant must not abort the
                other 599. A caught refusal is recorded as SKIPPED - never as sent. Checklist
                nuance 8 ("the email never fails to go out") covers LLM, budget and validator
                failures, which fall back to the template; it does NOT mean swallowing a refusal
                and sending numbers we do not trust.                                            */
            try
            {
                results.Add(await RunForTenantCoreAsync(tenant, asOf, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Free digest failed for tenant {CustomerId}; continuing with the rest.", tenant.CustomerId);
                results.Add(new FreeDigestTenantResult(
                    tenant.CustomerId, tenant.TenantName, EntitlementDecision.ExitZeroCost,
                    $"run failed: {ex.Message}", []));
            }
        }

        var batch = new FreeDigestBatchResult(results);
        logger.LogInformation(
            "Free digest weekly run complete. Tenants {Processed}, skipped {TenantsSkipped}, emails sent {Sent}, recipients skipped {RecipientsSkipped}.",
            batch.TenantsProcessed, batch.TenantsSkipped, batch.EmailsSent, batch.RecipientsSkipped);

        return batch;
    }

    public async Task<FreeDigestTenantResult> RunForTenantAsync(int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        WarnIfRecipientOverrideActive();
        var tenants = await repository.GetEntitledTenantsAsync(cancellationToken);
        var tenant = tenants.FirstOrDefault(t => t.CustomerId == customerId)
                     ?? new FreeDigestTenant(customerId, $"Tenant {customerId}");

        return await RunForTenantCoreAsync(tenant, asOf, cancellationToken);
    }

    private async Task<FreeDigestTenantResult> RunForTenantCoreAsync(
        FreeDigestTenant tenant, DateTime? asOf, CancellationToken cancellationToken)
    {
        /*  Gate first, always. Cheapest-first is the whole point: an unentitled tenant costs
            nothing - no recipient query, no aggregation, no LLM call, no email.               */
        var gate = await repository.EvaluateGateAsync(tenant.CustomerId, cancellationToken);
        if (!gate.ShouldProceed)
        {
            logger.LogInformation("Tenant {CustomerId} skipped: {Decision} - {Reason}", tenant.CustomerId, gate.Decision, gate.Reason);
            return new FreeDigestTenantResult(tenant.CustomerId, tenant.TenantName, gate.Decision, gate.Reason, []);
        }

        var recipients = await repository.GetRecipientsAsync(tenant.CustomerId, cancellationToken);

        /*  The gate counts recipients with the same predicate this query uses. A mismatch means
            the two have drifted apart, which would make EXIT_NO_RECIPIENTS meaningless - worth
            a warning rather than silence, but not worth refusing to send.                     */
        if (recipients.Count != gate.RecipientCount)
        {
            logger.LogWarning(
                "Tenant {CustomerId}: gate counted {GateCount} recipient(s) but enumeration returned {ListCount}. " +
                "The two predicates have diverged - usp_Insights_FreeDigestGate and GetRecipientsAsync must agree.",
                tenant.CustomerId, gate.RecipientCount, recipients.Count);
        }

        var outcomes = new List<FreeDigestRecipientOutcome>(recipients.Count);

        foreach (var recipient in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(await RunForRecipientAsync(tenant, recipient, asOf, cancellationToken));
        }

        return new FreeDigestTenantResult(tenant.CustomerId, tenant.TenantName, gate.Decision, gate.Reason, outcomes);
    }

    private async Task<FreeDigestRecipientOutcome> RunForRecipientAsync(
        FreeDigestTenant tenant, FreeDigestRecipient recipient, DateTime? asOf, CancellationToken cancellationToken)
    {
        /*  [FAIL CLOSED] A recipient with no authorised (branch, category) pairs would otherwise
            receive a perfectly well-formed email full of zeros: sql/06 constrains on
            tvfInsightsScopedInstances and simply returns nothing for an empty scope - it does not
            THROW. An all-zero digest looks fine, which is exactly why this has to be checked
            explicitly rather than noticed.                                                      */
        /*  User.ID is BIGINT but tvfInsightsScopePairs and usp_Insights_FreeDigestAggregates both
            take @UserID INT. Checked so an id beyond int range fails loudly here rather than
            silently truncating into some other user's scope.                                  */
        var scopedUserId = checked((int)recipient.UserId);

        var pairs = await scope.GetScopePairsAsync(scopedUserId, tenant.CustomerId, cancellationToken);
        if (pairs.Count == 0)
        {
            logger.LogInformation(
                "Tenant {CustomerId} recipient {UserId} skipped: no authorised scope, so the digest would be all zeros.",
                tenant.CustomerId, recipient.UserId);

            return new FreeDigestRecipientOutcome(
                recipient.UserId, recipient.Email, Sent: false, Source: null,
                Reason: "no authorised (branch, category) scope for this tenant", ProviderUsed: null);
        }

        var result = await pipeline.RunForRecipientAsync(
            customerId: tenant.CustomerId,
            /*  Per-recipient, never tenant-wide. Checklist nuance 6: pass the UserID so the 2-D
                scope constraint applies. NULL means tenant-wide and is only correct when the
                recipient has been verified tenant-wide - a free recipient must never receive
                numbers from outside their authorised scope. The cost is one aggregates call per
                recipient rather than one per tenant.                                            */
            userId: scopedUserId,
            recipientEmail: settings.ResolveDeliveryAddress(recipient.Email),
            recipientName: string.IsNullOrWhiteSpace(recipient.Name) ? null : recipient.Name,
            tenantName: tenant.TenantName,
            unsubscribeUrl: settings.BuildUnsubscribeUrl(tenant.CustomerId, recipient.UserId),
            asOf: asOf,
            cancellationToken: cancellationToken);

        return new FreeDigestRecipientOutcome(
            recipient.UserId, settings.ResolveDeliveryAddress(recipient.Email), result.Sent, result.Source, result.Reason, result.ProviderUsed);
    }

    /// <summary>
    /// Says so, every run, while mail is being diverted. A safety valve nobody can see is one
    /// that ships to production still on.
    /// </summary>
    private void WarnIfRecipientOverrideActive()
    {
        if (!string.IsNullOrWhiteSpace(settings.RecipientOverride))
        {
            logger.LogWarning(
                "Email:RecipientOverride is set to {Override} - EVERY digest will be delivered there instead of the " +
                "recipient resolved from the database. This must be empty in production.",
                settings.RecipientOverride);
        }
    }
}


