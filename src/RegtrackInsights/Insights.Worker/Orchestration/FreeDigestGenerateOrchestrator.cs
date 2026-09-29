using DurableTask.Core;
using Insights.Worker.Orchestration.Activities;

namespace Insights.Worker.Orchestration;

public sealed record FreeDigestGenerateOrchestrationInput(int TenantId, string? AsOf);

public sealed record FreeDigestGenerateOrchestrationOutput(
    int TenantId, string TenantName, string Decision, string Reason,
    int ScopeGroups, int LlmCalls, int Generated, int AlreadyGenerated, int RecipientsWithoutScope,
    int TotalInputTokens, int TotalOutputTokens, int Refused = 0);

/// <summary>
/// SUNDAY half of the two-phase free digest (ADR-0001, 2026-09-10). Same gate + resolve + group
/// shape FreeDigestOrchestrator already used - reuses ResolveDigestRecipientsActivity and
/// ComposeDigestActivity UNCHANGED - but stops after the content is composed and stored. Sends
/// nothing. FreeDigestSendOrchestrator (Monday) is what actually mails anyone.
///
/// -- WHY THE CLAIM MOVES HERE, NOT TO SEND ---------------------------------------------------
/// Generation is now the expensive step (one LLM call per scope group). The artifact-slot claim
/// (ClaimDigestArtifactActivity) protects THAT spend - a re-tick this Sunday, or a retry after a
/// crash, must not re-compose a scope group that already has a complete artifact. The per-
/// recipient once-a-week SEND claim (InsightsFreeDigestLog) is unrelated and still lives entirely
/// in FreeDigestSendOrchestrator - it protects "did we already mail this person," a Monday
/// question, not a Sunday one.
/// </summary>
public sealed class FreeDigestGenerateOrchestrator : TaskOrchestration<FreeDigestGenerateOrchestrationOutput, FreeDigestGenerateOrchestrationInput>
{
    public const string Name = "FreeDigestGenerateOrchestrator";

    /*  Version history.

        1.0 - two-phase generate (ADR-0001, 2026-09-10). The RegTrack show-entitlements lookup
        (2026-09-27) was changed in place at 1.0.

        Bumped 1.0 -> 1.1 (2026-09-29): the show-entitlements lookup is REMOVED on the product
        owner's instruction. No more ResolveRecipientEntitlementsActivity chunks between resolve
        and claim; ResolveDigestRecipientsActivity groups recipients by their plain SQL scope
        signature again and returns the groups itself; ComposeDigestInput lost AllowedBranchIds
        (the slot procs no longer take @AllowedBranches). A real call-sequence and payload
        change. [VERIFY BEFORE DEPLOY] Only 1.1 is registered, so any in-flight 1.0 instance can
        no longer be dispatched - terminate/purge FreeDigestGenerateOrchestrator 1.0 instances
        first, and do not deploy between Sunday generate and Monday send (see
        FreeDigestSendOrchestrator's 1.1 note). DEPLOY ORDER vs the sql/34-41 change that drops
        @AllowedBranches: this worker first, or together - never the SQL first. A 1.0 worker still
        passes @AllowedBranches and fails every slot read (Msg 8144) against procs that no longer
        declare it; this worker omits it, which the 2026-09-27 procs accept (it defaulted to NULL). */
    public const string Version = "1.1";

    public override async Task<FreeDigestGenerateOrchestrationOutput> RunTask(OrchestrationContext context, FreeDigestGenerateOrchestrationInput input)
    {
        /*  Same retry policy on EVERY step, not just compose/persist - a transient SQL blip on the
            gate/resolve or the artifact-slot claim must not fault the whole tenant's Sunday run.  */
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
            return new FreeDigestGenerateOrchestrationOutput(
                input.TenantId, resolved.TenantName, resolved.Decision, resolved.Reason,
                ScopeGroups: 0, LlmCalls: 0, Generated: 0, AlreadyGenerated: 0, RecipientsWithoutScope: 0,
                TotalInputTokens: 0, TotalOutputTokens: 0);
        }

        var groups = resolved.Groups;

        var llmCalls = 0;
        var generated = 0;
        var alreadyGenerated = 0;
        var totalInputTokens = 0;
        var totalOutputTokens = 0;
        var refused = 0;

        foreach (var group in groups)
        {
            var claim = await context.ScheduleWithRetry<ClaimDigestArtifactOutput>(
                typeof(ClaimDigestArtifactActivity).Name, "1.0", retry,
                new ClaimDigestArtifactInput(input.TenantId, resolved.WeekEnding, group.ScopeSignature, group.RepresentativeUserId, resolved.TenantName));

            if (!claim.Claimed)
            {
                // Either a complete artifact already exists for this scope group this week (the
                // normal re-tick case), or another attempt currently holds the slot. Either way,
                // composing again would waste real LLM spend for nothing new.
                alreadyGenerated++;
                continue;
            }

            try
            {
                var composed = await context.ScheduleWithRetry<ComposeDigestOutput>(
                    typeof(ComposeDigestActivity).Name, "1.0", retry,
                    new ComposeDigestInput(input.TenantId, group.RepresentativeUserId, resolved.WeekEnding, input.AsOf));

                if (composed.Source == ComposeDigestActivity.RefusedSource)
                {
                    /*  Monthly edition only: the slot proc refused the data (fail closed). Store
                        nothing - no artifact means Monday's send finds nothing to dispatch for this
                        scope group - and hand the slot back so the slot table stays clean.

                        TERMINAL FOR THE WEEK, deliberately: the instance id is keyed on
                        (tenant, week), so no later tick re-runs this orchestration - a refusal
                        is a data problem that needs a person, not a retry. ComposeDigestActivity's
                        LogError (with the SQL code) is the signal. Never reached by weekly
                        history, so replay of in-flight runs is unchanged.                      */
                    await context.ScheduleTask<object?>(
                        typeof(ReleaseDigestArtifactActivity).Name, "1.0",
                        new ReleaseDigestArtifactInput(claim.ArtifactId!));
                    refused++;
                    continue;
                }

                llmCalls++;
                totalInputTokens += composed.InputTokens;
                totalOutputTokens += composed.OutputTokens;

                await context.ScheduleWithRetry<PersistDigestArtifactOutput>(
                    typeof(PersistDigestArtifactActivity).Name, "1.0", retry,
                    new PersistDigestArtifactInput(
                        input.TenantId, claim.ArtifactId!, composed.Body, composed.Source, resolved.TenantName, resolved.WeekEnding,
                        context.CurrentUtcDateTime, group.Recipients.Count));

                generated++;
            }
            catch
            {
                /*  Hand the slot back so a LATER tick can retry this scope group, same "release on
                    failure" shape SendDigestActivity already uses for the per-recipient send claim
                    - a crash here must not permanently orphan this scope group for the week.      */
                await context.ScheduleTask<object?>(
                    typeof(ReleaseDigestArtifactActivity).Name, "1.0",
                    new ReleaseDigestArtifactInput(claim.ArtifactId!));
                throw;
            }
        }

        return new FreeDigestGenerateOrchestrationOutput(
            input.TenantId, resolved.TenantName, resolved.Decision, resolved.Reason,
            groups.Count, llmCalls, generated, alreadyGenerated, resolved.RecipientsWithoutScope,
            totalInputTokens, totalOutputTokens, refused);
    }
}
