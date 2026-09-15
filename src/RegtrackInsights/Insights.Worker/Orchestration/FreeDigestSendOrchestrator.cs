using DurableTask.Core;
using Insights.Worker.Orchestration.Activities;

namespace Insights.Worker.Orchestration;

public sealed record FreeDigestSendOrchestrationInput(int TenantId, int ArtifactFreshnessDays);

public sealed record FreeDigestSendOrchestrationOutput(
    int TenantId, string TenantName, string Decision, string Reason,
    int ScopeGroups, int Sent, int Skipped, int RecipientsSkippedNoMatchingArtifact, int RecipientsSkippedNoScope);

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

        foreach (var group in dispatch.Groups)
        {
            var fetched = await context.ScheduleWithRetry<FetchDigestArtifactOutput>(
                typeof(FetchDigestArtifactActivity).Name, "1.0", retry, new FetchDigestArtifactInput(group.Artifact));

            foreach (var batch in group.Recipients.Chunk(SendFanOutBatchSize))
            {
                var pending = new List<Task<SendDigestFromArtifactOutput>>(batch.Length);

                foreach (var recipient in batch)
                {
                    pending.Add(context.ScheduleWithRetry<SendDigestFromArtifactOutput>(
                        typeof(SendDigestFromArtifactActivity).Name, "1.0", retry,
                        new SendDigestFromArtifactInput(
                            input.TenantId, dispatch.TenantName, recipient.UserId, recipient.Email, recipient.Name,
                            fetched.Html, group.Artifact.WeekEnding.ToString("yyyy-MM-dd"))));
                }

                var results = await Task.WhenAll(pending);

                foreach (var result in results)
                {
                    if (result.Sent) sent++; else skipped++;
                }
            }

            await context.ScheduleTask<object?>(
                typeof(MarkDigestArtifactDispatchedActivity).Name, "1.0",
                new MarkDigestArtifactDispatchedInput(group.Artifact.ArtifactId.ToString()));
        }

        return new FreeDigestSendOrchestrationOutput(
            input.TenantId, dispatch.TenantName, dispatch.Decision, dispatch.Reason,
            dispatch.Groups.Count, sent, skipped, dispatch.RecipientsSkippedNoMatchingArtifact, dispatch.RecipientsSkippedNoScope);
    }
}
