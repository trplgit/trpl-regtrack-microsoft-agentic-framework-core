using DurableTask.Core;
using Insights.Worker.Orchestration.Activities;

namespace Insights.Worker.Orchestration;

public sealed record FreeDigestOrchestrationInput(int TenantId, string? AsOf);

public sealed record FreeDigestOrchestrationOutput(
    int TenantId, string TenantName, string Decision, string Reason,
    int ScopeGroups, int LlmCalls, int Sent, int Skipped, int RecipientsWithoutScope);

/// <summary>
/// The free weekly digest as a durable, crash-resumable orchestration (design doc 10.2 - the same
/// MAF worker, via a separate entry point that skips agentic composition and the presentation
/// layer).
///
/// resolve -> compose once per scope group -> fan out sends in bounded batches.
///
/// Deterministic body only: no clock read, no DB access, no LLM call here. The week-ending date is
/// fixed by ResolveDigestRecipientsActivity and threaded through as a string, so a replay produces
/// the identical claim key rather than a fresh one (CLAUDE.md 6).
///
/// -- WHY COMPOSE IS OUTSIDE THE RECIPIENT LOOP -----------------------------------------------
/// Recipients sharing a scope receive identical aggregates, so they need one LLM call between
/// them. On a tenant with 1,000 tenant-wide management users that is one call rather than a
/// thousand - the difference between design doc 10.5's cost model holding and being exceeded by
/// orders of magnitude.
///
/// -- WHY SENDS FAN OUT, AND WHY IN BATCHES ---------------------------------------------------
/// Awaiting each send in turn made a 1,000-recipient tenant a 1,000-round-trip serial walk, which
/// is correct but holds the batch lane for the better part of an hour. Scheduling a batch and
/// awaiting them together is the canonical DTFx fan-out and is replay-safe: the tasks are created
/// in a fixed order, so a replay reconstructs exactly the same set.
///
/// It is BOUNDED rather than one WhenAll over every recipient because an unbounded fan-out hands
/// the mail provider a thousand simultaneous sends and takes a thousand claims before learning
/// whether the first one worked. A batch caps outstanding claims and surfaces a provider outage
/// after tens of failures rather than after a thousand.
///
/// [REPLAY] SendFanOutBatchSize is a const, not configuration, on purpose. Configuration can
/// differ between the original execution and a replay, which is precisely the non-determinism the
/// orchestrator body must not contain. Changing it is a code change - and once this orchestration
/// has run in production, a change here needs a Version bump so in-flight instances keep replaying
/// against the shape they started with.
/// </summary>
public sealed class FreeDigestOrchestrator : TaskOrchestration<FreeDigestOrchestrationOutput, FreeDigestOrchestrationInput>
{
    public const string Name = "FreeDigestOrchestrator";
    public const string Version = "1.0";

    /// <summary>
    /// Recipients whose sends are scheduled together before awaiting. Tuned for the mail provider,
    /// not the database: DTFx already throttles how many activities actually execute at once via
    /// the worker's concurrency settings, so this bounds outstanding claims and blast radius.
    /// </summary>
    internal const int SendFanOutBatchSize = 25;

    public override async Task<FreeDigestOrchestrationOutput> RunTask(OrchestrationContext context, FreeDigestOrchestrationInput input)
    {
        /*  Not retried. A refusal here - unentitled, superseded, dictionary gap - is a decision,
            not a blip, and retrying it three times only spends time to reach the same answer
            (spec 4.6 scopes retry to TRANSIENT failures).                                       */
        var resolved = await context.ScheduleTask<ResolveDigestRecipientsOutput>(
            typeof(ResolveDigestRecipientsActivity).Name, "1.0",
            new ResolveDigestRecipientsInput(input.TenantId, input.AsOf));

        if (!resolved.ShouldProceed)
        {
            return new FreeDigestOrchestrationOutput(
                input.TenantId, resolved.TenantName, resolved.Decision, resolved.Reason,
                ScopeGroups: 0, LlmCalls: 0, Sent: 0, Skipped: 0, resolved.RecipientsWithoutScope);
        }

        /*  Backoff on transient failures (spec 4.6). The LLM and the mail provider are the two
            things here that fail intermittently and succeed on a second attempt; a send retry is
            safe because the claim inside SendDigestActivity makes it idempotent.                */
        var retry = new RetryOptions(TimeSpan.FromSeconds(5), maxNumberOfAttempts: 3)
        {
            BackoffCoefficient = 2.0,
            MaxRetryInterval = TimeSpan.FromMinutes(2),
        };

        var llmCalls = 0;
        var sent = 0;
        var skipped = 0;

        foreach (var group in resolved.Groups)
        {
            var composed = await context.ScheduleWithRetry<ComposeDigestOutput>(
                typeof(ComposeDigestActivity).Name, "1.0", retry,
                new ComposeDigestInput(input.TenantId, group.RepresentativeUserId, resolved.WeekEnding, input.AsOf));

            llmCalls++;

            foreach (var batch in group.Recipients.Chunk(SendFanOutBatchSize))
            {
                /*  Schedule the whole batch FIRST, await afterwards - awaiting inside this loop
                    would serialise it again and quietly undo the fan-out.                       */
                var pending = new List<Task<SendDigestOutput>>(batch.Length);

                foreach (var recipient in batch)
                {
                    pending.Add(context.ScheduleWithRetry<SendDigestOutput>(
                        typeof(SendDigestActivity).Name, "1.0", retry,
                        new SendDigestInput(
                            input.TenantId, resolved.TenantName, recipient.UserId, recipient.Email, recipient.Name,
                            composed.Body, composed.Source, resolved.WeekEnding)));
                }

                var results = await Task.WhenAll(pending);

                /*  Counted from the completed results in order, never from inside the scheduled
                    tasks - incrementing as they complete would make the totals depend on
                    completion order, which replay does not preserve.                            */
                foreach (var result in results)
                {
                    if (result.Sent) sent++; else skipped++;
                }
            }
        }

        return new FreeDigestOrchestrationOutput(
            input.TenantId, resolved.TenantName, resolved.Decision, resolved.Reason,
            resolved.Groups.Count, llmCalls, sent, skipped, resolved.RecipientsWithoutScope);
    }
}
