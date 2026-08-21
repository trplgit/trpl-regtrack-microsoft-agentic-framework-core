using Insights.Agents;
using Insights.Data;
using Insights.Data.Email;
using Insights.Domain;
using Insights.Presentation;

namespace Insights.Worker;

/// <summary>
/// gate -> ~13 aggregates -> capped LLM -> validate -> render -> send (design doc Section
/// 10.4). Plain sequential steps for now, not a Durable Task orchestrator - that substrate
/// is Phase 1d (build order step 11); free tier ships first and deliberately does not wait
/// for it (CLAUDE.md Section 9). When step 11 lands, each method here becomes an activity.
/// </summary>
public sealed class FreeDigestPipeline(
    IFreeDigestRepository repository,
    FreeDigestWriter writer,
    FreeDigestEmailRenderer renderer,
    IEmailSender emailSender,
    int tokenCap,
    string fromAddress,
    string fromName,
    string upgradeUrl)
{
    public async Task<FreeDigestRunResult> RunForRecipientAsync(
        int customerId, int? userId, string recipientEmail, string? recipientName,
        string tenantName, string unsubscribeUrl, DateTime? asOf = null, CancellationToken cancellationToken = default)
    {
        var gate = await repository.EvaluateGateAsync(customerId, cancellationToken);
        if (!gate.ShouldProceed)
            return new FreeDigestRunResult(Sent: false, Source: null, Reason: gate.Reason, ProviderUsed: null);

        var aggregates = await repository.GetAggregatesAsync(customerId, userId, asOf, cancellationToken);
        var draft = await writer.WriteAsync(aggregates, tokenCap, cancellationToken);

        var weekEnding = (asOf ?? DateTime.UtcNow).Date;
        string body;
        FreeDigestSource source;
        string? reason = draft.SkippedReason;

        if (draft.Source == FreeDigestSource.Llm)
        {
            var validation = FreeDigestValidator.Validate(draft.Body, aggregates);
            if (validation.IsValid)
            {
                body = draft.Body;
                source = FreeDigestSource.Llm;
            }
            else
            {
                body = await renderer.RenderFallbackBodyAsync(aggregates, recipientName, weekEnding, cancellationToken);
                source = FreeDigestSource.Fallback;
                reason = "validator rejected the LLM body: " + string.Join("; ", validation.FailedChecks);
            }
        }
        else
        {
            body = await renderer.RenderFallbackBodyAsync(aggregates, recipientName, weekEnding, cancellationToken);
            source = FreeDigestSource.Fallback;
        }

        var html = await renderer.RenderHtmlAsync(body, tenantName, weekEnding, upgradeUrl, unsubscribeUrl, cancellationToken);
        var subject = $"RegTrack Insights — week ending {weekEnding:d MMM yyyy}";
        var sendResult = await emailSender.SendAsync(
            new EmailMessage(recipientEmail, recipientName, subject, html, fromAddress, fromName), cancellationToken);

        return new FreeDigestRunResult(Sent: true, Source: source, Reason: reason, ProviderUsed: sendResult.ProviderUsed, Body: body, Html: html);
    }
}
