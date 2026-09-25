using DurableTask.Core;
using DurableTask.Core.Exceptions;
using Insights.Data.Email;
using Insights.Worker.Orchestration.Activities;

namespace Insights.Worker.Orchestration;

public sealed record FreeDigestSendOrchestrationInput(int TenantId, int ArtifactFreshnessDays);

public sealed record FreeDigestSendOrchestrationOutput(
    int TenantId, string TenantName, string Decision, string Reason,
    int ScopeGroups, int Sent, int Skipped, int RecipientsSkippedNoMatchingArtifact, int RecipientsSkippedNoScope,
    int Failed = 0);

/// <summary>
/// MONDAY half of the two-phase free digest (ADR-0001, 2026-09-10). Re-gates and re-resolves LIVE
/// (ResolveDigestDispatchActivity - never trusts Sunday's resolution, spec 5.3), fetches each scope
/// group's stored artifact ONCE, then fans out the actual send per recipient exactly as
/// FreeDigestOrchestrator already did - same batching, same per-recipient claim, same "email never
/// fails to go out for a reason other than a real refusal" posture.
///
/// -- WHY SENDS STILL FAN OUT IN BATCHES --------------------------------------------------------
/// The rate limit (RateLimitedEmailSender, ~5/sec) is what actually paces the outbound calls now -
/// this fan-out no longer exists to throttle the mail provider itself, it exists so a 1,000-
/// recipient tenant does not hold one giant WhenAll open for the whole send. The limiter makes the
/// pacing real regardless of how many sends are scheduled at once.
///
/// -- ONE RECIPIENT'S FAILURE NEVER STOPS THE TENANT (2026-09-25) -------------------------------
/// Each recipient's send is awaited through <see cref="SendOneAsync"/>, which turns an exhausted
/// retry (TaskFailedException) into a recorded 'failed' outcome instead of an exception. Before
/// this, that exception escaped Task.WhenAll, faulted the orchestration, and every later batch of
/// that tenant never went out - and, the digest being weekly, those recipients simply lost the
/// week. Now the run completes and reports Sent / Skipped / Failed.
///
/// Version deliberately left at 1.0 (product decision, 2026-09-25): only this code exists after
/// deploy. It is replay-safe for any instance already in flight - the success path schedules the
/// same tasks as before, an old history cannot contain an exhausted retry (that faulted it), and a
/// missing EmailGatewayId is resolved live by the send activity.
/// </summary>
public sealed class FreeDigestSendOrchestrator : TaskOrchestration<FreeDigestSendOrchestrationOutput, FreeDigestSendOrchestrationInput>
{
    public const string Name = "FreeDigestSendOrchestrator";
    public const string Version = "1.0";

    internal const int SendFanOutBatchSize = 25;

    public override async Task<FreeDigestSendOrchestrationOutput> RunTask(OrchestrationContext context, FreeDigestSendOrchestrationInput input)
    {
        /*  Same retry policy on EVERY step in this orchestrator, not just the send call - a
            transient SQL blip on the re-gate/re-resolve step, or a transient blob read hiccup on
            fetch, must not fault the whole tenant's Monday run any more than a transient
            ElasticEmail failure should. 3 attempts, exponential backoff, capped at 2 minutes.    */
        var retry = new RetryOptions(TimeSpan.FromSeconds(5), maxNumberOfAttempts: 3)
        {
            BackoffCoefficient = 2.0,
            MaxRetryInterval = TimeSpan.FromMinutes(2),
        };

        var dispatch = await context.ScheduleWithRetry<ResolveDigestDispatchOutput>(
            typeof(ResolveDigestDispatchActivity).Name, "1.0", retry,
            new ResolveDigestDispatchInput(input.TenantId, input.ArtifactFreshnessDays));

        if (!dispatch.ShouldProceed || dispatch.Groups.Count == 0)
        {
            return new FreeDigestSendOrchestrationOutput(
                input.TenantId, dispatch.TenantName, dispatch.Decision, dispatch.Reason,
                ScopeGroups: 0, Sent: 0, Skipped: 0, dispatch.RecipientsSkippedNoMatchingArtifact, dispatch.RecipientsSkippedNoScope);
        }

        var sent = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var group in dispatch.Groups)
        {
            var fetched = await context.ScheduleWithRetry<FetchDigestArtifactOutput>(
                typeof(FetchDigestArtifactActivity).Name, "1.0", retry, new FetchDigestArtifactInput(group.Artifact));

            var weekEnding = group.Artifact.WeekEnding.ToString("yyyy-MM-dd");

            foreach (var batch in group.Recipients.Chunk(SendFanOutBatchSize))
            {
                var pending = new List<Task<SendDigestFromArtifactOutput>>(batch.Length);

                foreach (var recipient in batch)
                {
                    pending.Add(SendOneAsync(context, retry, new SendDigestFromArtifactInput(
                        input.TenantId, dispatch.TenantName, recipient.UserId, recipient.Email, recipient.Name,
                        fetched.Html, weekEnding, dispatch.EmailGatewayId)));
                }

                var results = await Task.WhenAll(pending);

                foreach (var result in results)
                {
                    if (result.Sent) sent++;
                    else if (result.Failed) failed++;
                    else skipped++;
                }
            }

            await context.ScheduleTask<object?>(
                typeof(MarkDigestArtifactDispatchedActivity).Name, "1.0",
                new MarkDigestArtifactDispatchedInput(group.Artifact.ArtifactId.ToString()));
        }

        return new FreeDigestSendOrchestrationOutput(
            input.TenantId, dispatch.TenantName, dispatch.Decision, dispatch.Reason,
            dispatch.Groups.Count, sent, skipped, dispatch.RecipientsSkippedNoMatchingArtifact, dispatch.RecipientsSkippedNoScope,
            failed);
    }

    /// <summary>
    /// One recipient: send with retries; if every attempt fails, record 'failed' and return a
    /// result instead of throwing, so the rest of the tenant still goes out. A permanent rejection
    /// (400/403/422) never reaches the catch - the activity records it and returns Failed itself.
    /// </summary>
    private static async Task<SendDigestFromArtifactOutput> SendOneAsync(
        OrchestrationContext context, RetryOptions retry, SendDigestFromArtifactInput sendInput)
    {
        try
        {
            return await context.ScheduleWithRetry<SendDigestFromArtifactOutput>(
                typeof(SendDigestFromArtifactActivity).Name, "1.0", retry, sendInput);
        }
        catch (TaskFailedException ex)
        {
            var provider = sendInput.EmailGatewayId is { } id && Enum.IsDefined(typeof(EmailGateway), id)
                ? ((EmailGateway)id).ToString()
                : null;

            var detail = $"failed after {retry.MaxNumberOfAttempts} attempts: {ex.InnerException?.Message ?? ex.Message}";

            try
            {
                await context.ScheduleWithRetry<bool>(
                    typeof(RecordDigestSendFailedActivity).Name, "1.0", retry,
                    new RecordDigestSendFailedInput(sendInput.TenantId, sendInput.UserId, sendInput.WeekEnding, provider, detail));
            }
            catch (TaskFailedException)
            {
                // The log write itself failed on every attempt. Still count the recipient as
                // failed and carry on - losing one log row must not cost everyone after them
                // their email. The activity already logged the failure at Error level.
            }

            return new SendDigestFromArtifactOutput(false, detail, provider, Failed: true);
        }
    }
}
