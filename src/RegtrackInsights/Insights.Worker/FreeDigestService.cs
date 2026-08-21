using System.Diagnostics;
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
    FreeDigestMetrics metrics,
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

            /*  Per-tenant catch is load-bearing. One misconfigured tenant must not abort the
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
                metrics.RecordSkipped(FreeDigestSkipReason.Failed);
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
        var stopwatch = Stopwatch.StartNew();

        /*  Gate first, always. Cheapest-first is the whole point: an unentitled tenant costs
            nothing - no recipient query, no aggregation, no LLM call, no email.               */
        var gate = await repository.EvaluateGateAsync(tenant.CustomerId, cancellationToken);
        if (!gate.ShouldProceed)
        {
            logger.LogInformation("Tenant {CustomerId} skipped: {Decision} - {Reason}", tenant.CustomerId, gate.Decision, gate.Reason);
            metrics.RecordSkipped(SkipReasonFor(gate.Decision));
            metrics.RecordTenantDuration(stopwatch.Elapsed.TotalMilliseconds);
            return new FreeDigestTenantResult(tenant.CustomerId, tenant.TenantName, gate.Decision, gate.Reason, []);
        }

        var recipients = await repository.GetRecipientsAsync(tenant.CustomerId, cancellationToken);

        /*  The gate counts recipients with the same predicate this query uses. A mismatch means
            the two have drifted apart, which would make EXIT_NO_RECIPIENTS meaningless.        */
        if (recipients.Count != gate.RecipientCount)
        {
            logger.LogWarning(
                "Tenant {CustomerId}: gate counted {GateCount} recipient(s) but enumeration returned {ListCount}. " +
                "The two predicates have diverged - usp_Insights_FreeDigestGate and GetRecipientsAsync must agree.",
                tenant.CustomerId, gate.RecipientCount, recipients.Count);
        }

        var weekEnding = WeekEndingFor(asOf ?? DateTime.UtcNow);
        var outcomes = new List<FreeDigestRecipientOutcome>(recipients.Count);

        foreach (var recipient in recipients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(await RunForRecipientAsync(tenant, recipient, weekEnding, asOf, cancellationToken));
        }

        metrics.RecordTenantDuration(stopwatch.Elapsed.TotalMilliseconds);
        return new FreeDigestTenantResult(tenant.CustomerId, tenant.TenantName, gate.Decision, gate.Reason, outcomes);
    }

    private async Task<FreeDigestRecipientOutcome> RunForRecipientAsync(
        FreeDigestTenant tenant, FreeDigestRecipient recipient, DateOnly weekEnding, DateTime? asOf, CancellationToken cancellationToken)
    {
        /*  [FAIL CLOSED] A recipient with no authorised (branch, category) pairs would otherwise
            receive a perfectly well-formed email full of zeros: sql/06 constrains on
            tvfInsightsScopedInstances and simply returns nothing for an empty scope - it does not
            THROW. An all-zero digest looks fine, which is exactly why this is checked explicitly
            rather than noticed.                                                                 */
        var scopedUserId = checked((int)recipient.UserId);
        var pairs = await scope.GetScopePairsAsync(scopedUserId, tenant.CustomerId, cancellationToken);
        if (pairs.Count == 0)
        {
            logger.LogInformation(
                "Tenant {CustomerId} recipient {UserId} skipped: no authorised scope, so the digest would be all zeros.",
                tenant.CustomerId, recipient.UserId);
            metrics.RecordSkipped(FreeDigestSkipReason.NoScope);

            return Skipped(recipient, "no authorised (branch, category) scope for this tenant");
        }

        /*  CLAIM BEFORE SENDING. Exactly one caller per (customer, user, week) wins; a restart,
            a double-trigger or an overlapping scheduler tick all lose here rather than sending a
            second email. See sql/15 for why this biases toward under-sending.                   */
        if (!await repository.TryClaimSendAsync(tenant.CustomerId, recipient.UserId, weekEnding, cancellationToken))
        {
            logger.LogInformation(
                "Tenant {CustomerId} recipient {UserId} skipped: already claimed for week ending {WeekEnding}.",
                tenant.CustomerId, recipient.UserId, weekEnding);
            metrics.RecordSkipped(FreeDigestSkipReason.AlreadySent);

            return Skipped(recipient, $"already sent for week ending {weekEnding:yyyy-MM-dd}");
        }

        try
        {
            var result = await pipeline.RunForRecipientAsync(
                customerId: tenant.CustomerId,
                /*  Per-recipient, never tenant-wide. Checklist nuance 6: pass the UserID so the
                    2-D scope constraint applies. NULL means tenant-wide and is only correct when
                    the recipient has been verified tenant-wide.                                 */
                userId: scopedUserId,
                recipientEmail: settings.ResolveDeliveryAddress(recipient.Email),
                recipientName: string.IsNullOrWhiteSpace(recipient.Name) ? null : recipient.Name,
                tenantName: tenant.TenantName,
                unsubscribeUrl: settings.BuildUnsubscribeUrl(tenant.CustomerId, recipient.UserId),
                asOf: asOf,
                cancellationToken: cancellationToken);

            var source = result.Source?.ToString().ToLowerInvariant();

            await repository.RecordOutcomeAsync(tenant.CustomerId, recipient.UserId, weekEnding,
                result.Sent ? "sent" : "skipped", source, result.ProviderUsed, result.Reason, cancellationToken);

            if (result.Sent)
                metrics.RecordSent(source ?? "unknown", result.ProviderUsed ?? "unknown");
            else
                metrics.RecordSkipped(FreeDigestSkipReason.Failed);

            return new FreeDigestRecipientOutcome(
                recipient.UserId, settings.ResolveDeliveryAddress(recipient.Email),
                result.Sent, result.Source, result.Reason, result.ProviderUsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /*  The send never happened, so hand the claim back - otherwise a transient provider
                error would silently consume this recipient's only attempt for the week.        */
            await repository.ReleaseClaimAsync(tenant.CustomerId, recipient.UserId, weekEnding, CancellationToken.None);

            logger.LogError(ex, "Tenant {CustomerId} recipient {UserId} failed; claim released for retry.",
                tenant.CustomerId, recipient.UserId);
            metrics.RecordSkipped(FreeDigestSkipReason.Failed);

            return Skipped(recipient, $"failed: {ex.Message}");
        }
    }

    private FreeDigestRecipientOutcome Skipped(FreeDigestRecipient recipient, string reason) =>
        new(recipient.UserId, settings.ResolveDeliveryAddress(recipient.Email),
            Sent: false, Source: null, Reason: reason, ProviderUsed: null);

    private static FreeDigestSkipReason SkipReasonFor(EntitlementDecision decision) => decision switch
    {
        EntitlementDecision.ExitSuperseded => FreeDigestSkipReason.Superseded,
        EntitlementDecision.ExitNoRecipients => FreeDigestSkipReason.NoRecipients,
        _ => FreeDigestSkipReason.NotEntitled,
    };

    /// <summary>
    /// The Sunday that closes this digest's week.
    ///
    /// DATE-level and week-aligned ON PURPOSE: it is the third part of the claim key, so two runs
    /// on different days of the same week must produce the SAME value or the claim stops
    /// preventing anything.
    /// </summary>
    internal static DateOnly WeekEndingFor(DateTime asOf)
    {
        var date = DateOnly.FromDateTime(asOf.Date);
        var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(daysUntilSunday);
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
