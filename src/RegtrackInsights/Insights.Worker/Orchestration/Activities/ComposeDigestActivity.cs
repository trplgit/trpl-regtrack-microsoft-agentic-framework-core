using DurableTask.Core;
using Insights.Domain;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ComposeDigestInput(int TenantId, int RepresentativeUserId, string WeekEnding, string? AsOf);

/// <param name="InputTokens">
/// Real tokens billed for this scope group's LLM call - present even when <paramref name="Source"/>
/// is "Fallback", because a validator rejection or an over-budget/truncated response still means
/// the call happened and was charged. Only genuinely zero when the LLM was never called at all
/// (a SQL refusal), which is exactly why this is not optional: every token that was actually spent
/// must be accounted for, whether or not the output shipped.
/// </param>
public sealed record ComposeDigestOutput(string Body, string Source, string? Reason, int InputTokens, int OutputTokens);

/// <summary>
/// Node 2: produce ONE digest body for a whole scope group.
///
/// -- WHAT THE EMAIL SAYS (spec 2026-09-18) ---------------------------------------------------
/// The week's Sunday decides which email it is - overview, users, location, act or licence
/// (MonthlyDigestCalendar) - and FreeMonthlyDigestComposer builds it from that slot's proc
/// (sql/36-41) and versioned prompt (prompts/06a-06e). The decision uses WeekEnding only, which
/// is fixed before this activity runs, so FreeDigestGenerateOrchestrator's replay is unaffected.
///
/// The previous weekly content (sql/06's 13 counts + the 06_freetier_digest.md prompt, both
/// replaced 2026-09-19) is gone from the email. sql/06 and FreeDigestValidator stay: the insight
/// JSON lane still reads them.
///
/// -- THIS IS THE COST CONTROL ----------------------------------------------------------------
/// One LLM call per distinct SCOPE, not per recipient. Everyone in the group holds identical
/// (branch, category) pairs, so the slot proc returns them identical data and the model would
/// write the same words for each.
///
/// -- THE EMAIL NEVER FAILS TO GO OUT (10.5) - WHEN THE DATA IS GOOD ---------------------------
/// Over budget, a truncated response, or a validator rejection all fall back to the deterministic
/// body built from the same facts; Source = "Fallback" with the reason attached.
///
/// A SQL REFUSAL is the other failure class: the proc THROWs (51230-51309) because the data itself
/// failed a check. Then Source = "Refused" with an empty body, and the orchestrator releases the
/// slot and persists nothing - never the fallback, which exists for a bad draft on good data.
/// </summary>
public sealed class ComposeDigestActivity(
    FreeMonthlyDigestComposer composer,
    ILogger<ComposeDigestActivity> logger)
    : AsyncTaskActivity<ComposeDigestInput, ComposeDigestOutput>
{
    public const string RefusedSource = "Refused";

    protected override Task<ComposeDigestOutput> ExecuteAsync(TaskContext context, ComposeDigestInput input) => RunAsync(input);

    internal async Task<ComposeDigestOutput> RunAsync(ComposeDigestInput input)
    {
        var edition = MonthlyDigestCalendar.For(DateOnly.ParseExact(input.WeekEnding, "yyyy-MM-dd"));

        try
        {
            return await composer.ComposeAsync(input.TenantId, input.RepresentativeUserId, edition, input.AsOf);
        }
        catch (FreeMonthlyDigestRefusedException ex)
        {
            // Fail closed and loud (CLAUDE.md non-negotiable 2): refuse this scope group, log the
            // code, send nothing.
            logger.LogError(ex,
                "ComposeDigestActivity: tenant {TenantId} user {RepresentativeUserId} {Slot} - REFUSED by SQL error {SqlError}. Nothing will be sent to this scope group this week.",
                input.TenantId, input.RepresentativeUserId, edition.Slot, ex.SqlErrorNumber);
            return new ComposeDigestOutput(string.Empty, RefusedSource, ex.Message, 0, 0);
        }
    }
}
