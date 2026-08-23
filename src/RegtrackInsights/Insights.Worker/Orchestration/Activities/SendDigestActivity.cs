using DurableTask.Core;
using Insights.Data;
using Insights.Data.Email;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record SendDigestInput(
    int TenantId, string TenantName, long UserId, string Email, string? Name,
    string Body, string Source, string WeekEnding);

public sealed record SendDigestOutput(bool Sent, string? Reason, string? ProviderUsed);

/// <summary>
/// Node 3: deliver one recipient's copy of an already-composed body. No LLM call here.
///
/// -- [TRAP] THE CLAIM BELONGS IN THIS ACTIVITY -----------------------------------------------
/// The once-per-week claim is taken here, per recipient, immediately before sending - not in the
/// orchestrator and not in ComposeDigest.
///
///   In the orchestrator, it would be re-executed on replay and stop protecting anything.
///   In ComposeDigest, one claim would cover a whole group, so a failure halfway through a
///   thousand sends would consume the entire group's week.
///
/// Per-recipient and inside the activity means the orchestrator can retry this freely: a retry
/// after a successful send finds the claim already taken and skips, which is exactly the
/// behaviour that makes retry safe to turn on at all.
/// </summary>
public sealed class SendDigestActivity(
    IFreeDigestRepository repository,
    FreeDigestEmailRenderer renderer,
    IEmailSender emailSender,
    FreeDigestSettings settings,
    FreeDigestMetrics metrics)
    : AsyncTaskActivity<SendDigestInput, SendDigestOutput>
{
    protected override Task<SendDigestOutput> ExecuteAsync(TaskContext context, SendDigestInput input) => RunAsync(input);

    internal async Task<SendDigestOutput> RunAsync(SendDigestInput input)
    {
        var weekEnding = DateOnly.ParseExact(input.WeekEnding, "yyyy-MM-dd");

        if (!await repository.TryClaimSendAsync(input.TenantId, input.UserId, weekEnding))
        {
            metrics.RecordSkipped(FreeDigestSkipReason.AlreadySent);
            return new SendDigestOutput(false, $"already sent for week ending {input.WeekEnding}", null);
        }

        try
        {
            var html = await renderer.RenderHtmlAsync(
                input.Body,
                input.TenantName,
                weekEnding.ToDateTime(TimeOnly.MinValue),
                settings.UpgradeUrl,
                settings.BuildUnsubscribeUrl(input.TenantId, input.UserId));

            var subject = BuildSubject(input.TenantName, weekEnding);

            var sendResult = await emailSender.SendAsync(new EmailMessage(
                settings.ResolveDeliveryAddress(input.Email),
                string.IsNullOrWhiteSpace(input.Name) ? null : input.Name,
                subject, html, settings.FromAddress, settings.FromName));

            await repository.RecordOutcomeAsync(input.TenantId, input.UserId, weekEnding,
                "sent", input.Source.ToLowerInvariant(), sendResult.ProviderUsed);

            metrics.RecordSent(input.Source.ToLowerInvariant(), sendResult.ProviderUsed);

            return new SendDigestOutput(true, null, sendResult.ProviderUsed);
        }
        catch
        {
            /*  Hand the claim back before rethrowing, or a transient provider error would consume
                this recipient's only attempt for the week. Rethrown so the orchestrator's retry
                policy sees the failure - swallowing it here would silently drop the recipient.  */
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
