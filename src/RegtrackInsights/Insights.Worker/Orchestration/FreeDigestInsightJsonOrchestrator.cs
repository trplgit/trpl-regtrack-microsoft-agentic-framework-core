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
    public const string Version = "1.0";

    public override async Task<FreeDigestInsightJsonOrchestrationOutput> RunTask(OrchestrationContext context, FreeDigestInsightJsonOrchestrationInput input)
    {
        var retry = new RetryOptions(TimeSpan.FromSeconds(5), maxNumberOfAttempts: 3)
        {
            BackoffCoefficient = 2.0,
            MaxRetryInterval = TimeSpan.FromMinutes(2),
        };

        var resolved = await context.ScheduleWithRetry<ResolveDigestRecipientsOutput>(
            typeof(ResolveDigestRecipientsActivity).Name, "1.0", retry,
            new ResolveDigestRecipientsInput(input.TenantId, input.AsOf));

        if (!resolved.ShouldProceed)
        {
            return new FreeDigestInsightJsonOrchestrationOutput(
                input.TenantId, resolved.TenantName, resolved.Decision, resolved.Reason,
                ScopeGroups: 0, LlmCalls: 0, Posted: 0, Skipped: 0, resolved.RecipientsWithoutScope,
                TotalInputTokens: 0, TotalOutputTokens: 0);
        }

        var llmCalls = 0;
        var posted = 0;
        var skipped = 0;
        var totalInputTokens = 0;
        var totalOutputTokens = 0;

        foreach (var group in resolved.Groups)
        {
            var composed = await context.ScheduleWithRetry<ComposeInsightJsonOutput>(
                typeof(ComposeInsightJsonActivity).Name, "1.0", retry,
                new ComposeInsightJsonInput(input.TenantId, group.RepresentativeUserId, resolved.WeekEnding, input.AsOf));

            llmCalls++;
            totalInputTokens += composed.InputTokens;
            totalOutputTokens += composed.OutputTokens;

            // Fan the SAME group-level aggregates + narrative out to every recipient sharing this
            // scope - identical numbers, identical narrative, differing only by UserId. No further
            // LLM spend per recipient, matching the email digest's own cost architecture.
            foreach (var recipient in group.Recipients)
            {
                var result = await context.ScheduleWithRetry<PostInsightJsonOutput>(
                    typeof(PostInsightJsonActivity).Name, "1.0", retry,
                    new PostInsightJsonInput(input.TenantId, recipient.UserId, resolved.WeekEnding, composed.Aggregates, composed.Narrative));

                if (result.Posted)
                    posted++;
                else
                    skipped++;
            }
        }

        return new FreeDigestInsightJsonOrchestrationOutput(
            input.TenantId, resolved.TenantName, resolved.Decision, resolved.Reason,
            resolved.Groups.Count, llmCalls, posted, skipped, resolved.RecipientsWithoutScope,
            totalInputTokens, totalOutputTokens);
    }
}
