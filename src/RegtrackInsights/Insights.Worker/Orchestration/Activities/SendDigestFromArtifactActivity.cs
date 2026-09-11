using DurableTask.Core;
using Insights.Data;
using Insights.Data.Email;
using Insights.Presentation;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record SendDigestFromArtifactInput(
    int TenantId, string TenantName, long UserId, string Email, string? Name, string ArtifactHtml, string WeekEnding);

public sealed record SendDigestFromArtifactOutput(bool Sent, string? Reason, string? ProviderUsed);

/// <summary>
/// MONDAY, node 3: deliver one recipient's copy of an already-generated artifact. No LLM call, no
/// re-render of the shell - only a targeted swap of the unsubscribe sentinel for this recipient's
/// real, signed link (FreeDigestEmailRenderer.SubstituteUnsubscribeUrl), then send.
///
/// Same claim shape as SendDigestActivity (sql/15's InsightsFreeDigestLog, per recipient,
/// immediately before sending) - this IS the once-per-week send guarantee, unrelated to and
/// unaffected by the separate GENERATE-phase artifact-slot claim.
/// </summary>
public sealed class SendDigestFromArtifactActivity(
    IFreeDigestRepository repository, IEmailSender emailSender, FreeDigestSettings settings, FreeDigestMetrics metrics,
    ILogger<SendDigestFromArtifactActivity> logger)
    : AsyncTaskActivity<SendDigestFromArtifactInput, SendDigestFromArtifactOutput>
{
    protected override Task<SendDigestFromArtifactOutput> ExecuteAsync(TaskContext context, SendDigestFromArtifactInput input) => RunAsync(input);

    internal async Task<SendDigestFromArtifactOutput> RunAsync(SendDigestFromArtifactInput input)
    {
        var weekEnding = DateOnly.ParseExact(input.WeekEnding, "yyyy-MM-dd");

        if (!await repository.TryClaimSendAsync(input.TenantId, input.UserId, weekEnding))
        {
            metrics.RecordSkipped(FreeDigestSkipReason.AlreadySent);
            return new SendDigestFromArtifactOutput(false, $"already sent for week ending {input.WeekEnding}", null);
        }

        try
        {
            var html = FreeDigestEmailRenderer.SubstituteUnsubscribeUrl(
                input.ArtifactHtml, settings.BuildUnsubscribeUrl(input.TenantId, input.UserId));

            var subject = BuildSubject(input.TenantName, weekEnding);

            var sendResult = await emailSender.SendAsync(new EmailMessage(
                settings.ResolveDeliveryAddress(input.Email),
                string.IsNullOrWhiteSpace(input.Name) ? null : input.Name,
                subject, html, settings.FromAddress, settings.FromName));

            await repository.RecordOutcomeAsync(input.TenantId, input.UserId, weekEnding, "sent", providerUsed: sendResult.ProviderUsed);

            metrics.RecordSent("artifact", sendResult.ProviderUsed);

            return new SendDigestFromArtifactOutput(true, null, sendResult.ProviderUsed);
        }
        catch (Exception ex)
        {
            // Retried up to 3x by FreeDigestSendOrchestrator's RetryOptions before this
            // ultimately fails the orchestration - log every attempt, not just the last, so a
            // permanently-bad request (EmailProviderException carries the provider's own error
            // body - see that type's doc comment) is visibly distinguishable from three
            // successive transient blips without needing Durable Task's own history dump.
            logger.LogWarning(ex,
                "SendDigestFromArtifactActivity: tenant {TenantId} user {UserId} - send failed, releasing claim for retry.",
                input.TenantId, input.UserId);

            /*  Hand the claim back before rethrowing - a transient provider error must not consume
                this recipient's only attempt for the week.                                        */
            await repository.ReleaseClaimAsync(input.TenantId, input.UserId, weekEnding);
            metrics.RecordSkipped(FreeDigestSkipReason.Failed);
            throw;
        }
    }

    /// <summary>
    /// The subject line. NAMES THE TENANT, and that is the whole point.
    ///
    /// A user can be mapped to more than one customer - a conglomerate CCO covering two legal
    /// entities is the normal case, not an edge case. They receive one correctly-scoped digest
    /// per tenant, and the claim key (CustomerID, UserID, WeekEnding) makes both sends legitimate
    /// rather than a duplicate. But with a tenant-less subject both arrive titled identically, so
    /// the only way to tell them apart is to open them - and a skimmed inbox reads the second as
    /// a repeat of the first and never opens it.
    ///
    /// Falls back to the plain form when the name is missing rather than emitting a dangling
    /// separator: an empty tenant name is a data gap, not a reason to send a malformed subject.
    /// </summary>
    internal static string BuildSubject(string? tenantName, DateOnly weekEnding)
    {
        // InvariantCulture: the worker may run under any locale, and the month name in a
        // customer-facing subject must not depend on the host machine.
        var week = weekEnding.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

        return string.IsNullOrWhiteSpace(tenantName)
            ? $"RegTrack Insights - week ending {week}"
            : $"RegTrack Insights: {tenantName.Trim()} - week ending {week}";
    }
}
