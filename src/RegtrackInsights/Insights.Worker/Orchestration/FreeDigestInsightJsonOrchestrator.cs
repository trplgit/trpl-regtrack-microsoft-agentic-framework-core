using DurableTask.Core;
using Insights.Worker.Orchestration.Activities;

namespace Insights.Worker.Orchestration;

public sealed record FreeDigestInsightJsonOrchestrationInput(int TenantId, string? AsOf);

public sealed record FreeDigestInsightJsonOrchestrationOutput(
    int TenantId, string TenantName, string Decision, string Reason,
    int ScopeGroups, int LlmCalls, int Posted, int Skipped, int RecipientsWithoutScope,
    int TotalInputTokens, int TotalOutputTokens);

/// <summary>
/// ADR-0002 (2026-09-11) - the weekly per-user "current insight" JSON, posted to an external API
/// instead of emailed. Sunday-only, SIBLING to FreeDigestGenerateOrchestrator: reuses the same
/// gate/resolve/group node (ResolveDigestRecipientsActivity, UNCHANGED), then composes ONE
/// narrative per scope group (ComposeInsightJsonActivity - one LLM call, same cost-saving unit as
/// the email digest), and POSTs one JSON envelope PER RECIPIENT in that group
/// (PostInsightJsonActivity) - the grain the external API wants, distinct from the email digest's
/// per-scope-group artifact.
///
/// Deliberately a SEPARATE orchestrator, not a step bolted onto FreeDigestGenerateOrchestrator: a
/// dead or unconfigured external API must never fault the existing HTML email lane. Inert overall
/// unless FreeDigest:InsightApi:Enabled is true (see FreeDigestScheduler).
/// </summary>
public sealed class FreeDigestInsightJsonOrchestrator : TaskOrchestration<FreeDigestInsightJsonOrchestrationOutput, FreeDigestInsightJsonOrchestrationInput>
{
    public const string Name = "FreeDigestInsightJsonOrchestrator";

    /*  Version history.

        1.0 - the weekly insight JSON lane (ADR-0002, 2026-09-11). The RegTrack show-entitlements
        lookup (2026-09-27) was changed in place at 1.0.

        Bumped 1.0 -> 1.1 (2026-09-29): the show-entitlements lookup is REMOVED on the product
        owner's instruction - same removal as FreeDigestGenerateOrchestrator 1.1. No more
        ResolveRecipientEntitlementsActivity chunks; groups come straight from
        ResolveDigestRecipientsActivity (plain SQL scope signature); ComposeInsightJsonInput lost
        AllowedBranchIds. A real call-sequence and payload change. [VERIFY BEFORE DEPLOY] Only 1.1
        is registered - terminate/purge any in-flight 1.0 instance before deploying. Deploy this
        worker before, or together with, the sql/34-41 change that drops @AllowedBranches - never
        the SQL first (see FreeDigestGenerateOrchestrator's 1.1 note). */
    public const string Version = "1.1";

    public override async Task<FreeDigestInsightJsonOrchestrationOutput> RunTask(OrchestrationContext context, FreeDigestInsightJsonOrchestrationInput input)
    {
        var retry = new RetryOptions(TimeSpan.FromSeconds(5), maxNumberOfAttempts: 3)
        {
            BackoffCoefficient = 2.0,
            MaxRetryInterval = TimeSpan.FromMinutes(2),
        };

        var resolved = await context.ScheduleWithRetry<ResolveDigestRecipientsOutput>(
            typeof(ResolveDigestRecipientsActivity).Name, "1.0", retry,
            new ResolveDigestRecipientsInput(input.TenantId, input.AsOf, DigestClaimDomain.InsightJson));

        if (!resolved.ShouldProceed)
        {
            return new FreeDigestInsightJsonOrchestrationOutput(
                input.TenantId, resolved.TenantName, resolved.Decision, resolved.Reason,
                ScopeGroups: 0, LlmCalls: 0, Posted: 0, Skipped: 0, RecipientsWithoutScope: 0,
                TotalInputTokens: 0, TotalOutputTokens: 0);
        }

        var groups = resolved.Groups;

        var llmCalls = 0;
        var posted = 0;
        var skipped = 0;
        var totalInputTokens = 0;
        var totalOutputTokens = 0;

        foreach (var group in groups)
        {
            var composed = await context.ScheduleWithRetry<ComposeInsightJsonOutput>(
                typeof(ComposeInsightJsonActivity).Name, "1.0", retry,
                new ComposeInsightJsonInput(input.TenantId, group.RepresentativeUserId, resolved.WeekEnding, input.AsOf));

            llmCalls++;
            totalInputTokens += composed.InputTokens;
            totalOutputTokens += composed.OutputTokens;

            // Fan the SAME group-level card out to every recipient sharing this scope - identical
            // numbers, identical text, differing only by UserId (the mapper re-stamps insight_id).
            // No further LLM spend per recipient, matching the email digest's own cost architecture.
            foreach (var recipient in group.Recipients)
            {
                var result = await context.ScheduleWithRetry<PostInsightJsonOutput>(
                    typeof(PostInsightJsonActivity).Name, "1.0", retry,
                    new PostInsightJsonInput(input.TenantId, recipient.UserId, resolved.WeekEnding, composed.Card));

                if (result.Posted)
                    posted++;
                else
                    skipped++;
            }
        }

        return new FreeDigestInsightJsonOrchestrationOutput(
            input.TenantId, resolved.TenantName, resolved.Decision, resolved.Reason,
            groups.Count, llmCalls, posted, skipped, resolved.RecipientsWithoutScope,
            totalInputTokens, totalOutputTokens);
    }
}
