using DurableTask.Core;
using Insights.Data;
using Insights.Data.Email;
using Insights.Presentation;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

/// <param name="EmailGatewayId">
/// The EmailGatewayMaster ID ResolveDigestDispatchActivity chose for this tenant. Nullable and last
/// so an input recorded before per-tenant routing still deserialises; null means "resolve it live
/// here", never "use a default".
/// </param>
public sealed record SendDigestFromArtifactInput(
    int TenantId, string TenantName, long UserId, string Email, string? Name, string ArtifactHtml, string WeekEnding,
    int? EmailGatewayId = null);

/// <param name="Failed">
/// True when this recipient was recorded as failed (a permanent provider rejection) rather than
/// skipped. Sent=false alone cannot tell "already sent this week" from "the provider refused it".
/// </param>
public sealed record SendDigestFromArtifactOutput(bool Sent, string? Reason, string? ProviderUsed, bool Failed = false);

/// <summary>
/// MONDAY, node 3: deliver one recipient's copy of an already-generated artifact. No LLM call, no
/// re-render of the shell - only a targeted swap of the unsubscribe sentinel for this recipient's
/// real, signed link (FreeDigestEmailRenderer.SubstituteUnsubscribeUrl), then send through the
/// tenant's own provider (EmailGatewayId -> IEmailSenderRegistry, each provider rate-limited).
///
/// Same claim shape as SendDigestActivity (sql/15's InsightsFreeDigestLog, per recipient,
/// immediately before sending) - this IS the once-per-week send guarantee, unrelated to and
/// unaffected by the separate GENERATE-phase artifact-slot claim.
///
/// -- FAILURES (2026-09-25) --------------------------------------------------------------------
///   400 / 403 / 422  the provider rejected THIS message. Recorded as 'failed' (the claim is kept,
///                    so the rest of this week's run does not retry it), returned, NOT thrown -
///                    retrying an identical request only fails identically.
///   anything else    (401, 429, 5xx, timeout, network) - claim released and rethrown, so the
///                    orchestrator's RetryOptions try again. After the last attempt the
///                    orchestrator records 'failed' (RecordDigestSendFailedActivity) and moves on.
/// Nothing is ever suppressed: a 'failed' row belongs to one week only, so next week the same
/// recipient is attempted fresh - a fix made during the week takes effect automatically.
/// </summary>
public sealed class SendDigestFromArtifactActivity(
    IFreeDigestRepository repository, IEmailSenderRegistry senders, IEmailGatewayResolver gatewayResolver,
    FreeDigestSettings settings, FreeDigestMetrics metrics, ILogger<SendDigestFromArtifactActivity> logger)
    : AsyncTaskActivity<SendDigestFromArtifactInput, SendDigestFromArtifactOutput>
{
    protected override Task<SendDigestFromArtifactOutput> ExecuteAsync(TaskContext context, SendDigestFromArtifactInput input) => RunAsync(input);

    internal async Task<SendDigestFromArtifactOutput> RunAsync(SendDigestFromArtifactInput input)
    {
        var weekEnding = DateOnly.ParseExact(input.WeekEnding, "yyyy-MM-dd");

        var gateway = await ResolveGatewayAsync(input);
        if (gateway is null)
        {
            // Only reachable for an input recorded before per-tenant routing (no EmailGatewayId)
            // whose live lookup is now refused. Nothing claimed, nothing sent.
            metrics.RecordSkipped(FreeDigestSkipReason.Failed);
            return new SendDigestFromArtifactOutput(false, "email gateway configuration refused", null, Failed: true);
        }

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

            var sendResult = await senders.For(gateway.Value).SendAsync(new EmailMessage(
                settings.ResolveDeliveryAddress(input.Email),
                string.IsNullOrWhiteSpace(input.Name) ? null : input.Name,
                subject, html, settings.FromAddress, settings.FromName));

            await repository.RecordOutcomeAsync(input.TenantId, input.UserId, weekEnding, "sent", providerUsed: sendResult.ProviderUsed);

            metrics.RecordSent("artifact", sendResult.ProviderUsed);

            return new SendDigestFromArtifactOutput(true, null, sendResult.ProviderUsed);
        }
        catch (EmailProviderException ex) when (ex.IsPermanentRecipientFailure)
        {
            logger.LogWarning(ex,
                "SendDigestFromArtifactActivity: tenant {TenantId} user {UserId} - {Provider} rejected the message ({StatusCode}); recording failed, not retrying.",
                input.TenantId, input.UserId, ex.ProviderName, (int)ex.StatusCode);

            var detail = $"{(int)ex.StatusCode}: {ex.ResponseBody}";

            try
            {
                await repository.RecordOutcomeAsync(input.TenantId, input.UserId, weekEnding, "failed",
                    providerUsed: ex.ProviderName, detail: detail);
            }
            catch
            {
                // The outcome could not be written - hand the claim back so a retry can try again
                // rather than leaving an unresolved claim that reads as "already sent".
                await repository.ReleaseClaimAsync(input.TenantId, input.UserId, weekEnding);
                throw;
            }

            metrics.RecordSkipped(FreeDigestSkipReason.Failed);
            return new SendDigestFromArtifactOutput(false, detail, ex.ProviderName, Failed: true);
        }
        catch (Exception ex)
        {
            // Retried by FreeDigestSendOrchestrator's RetryOptions - log every attempt, not just the
            // last, so three successive transient blips are visible without a history dump. A 401
            // (bad API key) hits every recipient on that provider, so it is logged at Error.
            var badKey = ex is EmailProviderException { StatusCode: System.Net.HttpStatusCode.Unauthorized };
            logger.Log(badKey ? LogLevel.Error : LogLevel.Warning, ex,
                "SendDigestFromArtifactActivity: tenant {TenantId} user {UserId} via {Gateway} - send failed, releasing claim for retry.",
                input.TenantId, input.UserId, gateway.Value);

            /*  Hand the claim back before rethrowing - a transient provider error must not consume
                this recipient's only attempt for the week.                                        */
            await repository.ReleaseClaimAsync(input.TenantId, input.UserId, weekEnding);
            metrics.RecordSkipped(FreeDigestSkipReason.Failed);
            throw;
        }
    }

    private async Task<EmailGateway?> ResolveGatewayAsync(SendDigestFromArtifactInput input)
    {
        if (input.EmailGatewayId is { } id)
        {
            return Enum.IsDefined(typeof(EmailGateway), id)
                ? (EmailGateway)id
                : throw new InvalidOperationException($"EmailGatewayId {id} is not a known gateway.");
        }

        var resolution = await gatewayResolver.ResolveAsync(input.TenantId);
        if (resolution.IsRefused)
            logger.LogError("SendDigestFromArtifactActivity: {Detail}", resolution.Detail);

        return resolution.Gateway;
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
    ///
    /// The week's Sunday decides which email it is, so the subject names its topic and month:
    /// "RegTrack Insights: Acme Holdings - Monthly overview, October 2026".
    /// </summary>
    internal static string BuildSubject(string? tenantName, DateOnly weekEnding) =>
        BuildSubject(tenantName, Insights.Domain.MonthlyDigestCalendar.For(weekEnding));

    /// <summary>
    /// The subject for an edition we already hold.
    ///
    /// <para>[FOUND LIVE on tenant 1082, 2026-09-22] The date-only overload re-derives the slot from
    /// the Sunday, which is right on the send path but wrong wherever an edition was built some
    /// other way. A September Licence edition borrows the 4th Sunday - September has no 5th - so
    /// re-deriving turned it back into the Act, and a Licence email went out headed "Acts,
    /// September 2026". The slot is already known; it should never be inferred twice.</para>
    /// </summary>
    internal static string BuildSubject(string? tenantName, Insights.Domain.MonthlyDigestEdition edition)
    {
        // InvariantCulture: the worker may run under any locale, and the month name in a
        // customer-facing subject must not depend on the host machine.
        var period = $"{Insights.Domain.MonthlyDigestCalendar.Title(edition.Slot)}, " +
                     edition.CurrMonthStart.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

        return string.IsNullOrWhiteSpace(tenantName)
            ? $"RegTrack Insights - {period}"
            : $"RegTrack Insights: {tenantName.Trim()} - {period}";
    }
}
