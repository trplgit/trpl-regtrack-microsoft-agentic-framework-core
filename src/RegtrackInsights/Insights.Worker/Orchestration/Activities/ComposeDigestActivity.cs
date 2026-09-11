using DurableTask.Core;
using Insights.Agents;
using Insights.Data;
using Insights.Presentation;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ComposeDigestInput(int TenantId, int RepresentativeUserId, string WeekEnding, string? AsOf);

/// <param name="InputTokens">
/// Real tokens billed for this scope group's LLM call - present even when <paramref name="Source"/>
/// is "Fallback", because a validator rejection or an over-budget/truncated response still means
/// the call happened and was charged. Only genuinely zero when the LLM was never called at all
/// (there is no such path today - draft.SkippedReason still comes from an attempted call), which
/// is exactly why this is not optional: every token that was actually spent must be accounted for,
/// whether or not the output shipped.
/// </param>
public sealed record ComposeDigestOutput(string Body, string Source, string? Reason, int InputTokens, int OutputTokens);

/// <summary>
/// Node 2: produce ONE digest body for a whole scope group.
///
/// -- THIS IS THE COST CONTROL ----------------------------------------------------------------
/// One LLM call per distinct SCOPE, not per recipient. Everyone in the group holds identical
/// (branch, category) pairs, so sql/06 returns them identical aggregates and the model would
/// write the same words for each. Design doc 10.5's budget - ~1,500 tokens an email, ~30M tokens
/// a year across ~600 tenants - only holds if this is the granularity.
///
/// -- THE EMAIL NEVER FAILS TO GO OUT (10.5) --------------------------------------------------
/// Over budget, a truncated response, or a validator rejection all fall back to the deterministic
/// template. This activity never throws for those; it returns Source = "Fallback" with the reason
/// attached so the run stays observable.
/// </summary>
public sealed class ComposeDigestActivity(
    IFreeDigestRepository repository,
    FreeDigestWriter writer,
    FreeDigestEmailRenderer renderer,
    FreeDigestSettings settings,
    ILogger<ComposeDigestActivity> logger)
    : AsyncTaskActivity<ComposeDigestInput, ComposeDigestOutput>
{
    protected override Task<ComposeDigestOutput> ExecuteAsync(TaskContext context, ComposeDigestInput input) => RunAsync(input);

    internal async Task<ComposeDigestOutput> RunAsync(ComposeDigestInput input)
    {
        var asOf = string.IsNullOrWhiteSpace(input.AsOf) ? (DateTime?)null : DateTime.Parse(input.AsOf);
        var weekEnding = DateOnly.ParseExact(input.WeekEnding, "yyyy-MM-dd").ToDateTime(TimeOnly.MinValue);

        var aggregates = await repository.GetAggregatesAsync(input.TenantId, input.RepresentativeUserId, asOf);

        var draft = await writer.WriteAsync(aggregates, settings.TokenCap);

        if (draft.Source == Insights.Domain.FreeDigestSource.Llm)
        {
            var validation = FreeDigestValidator.Validate(draft.Body, aggregates);
            if (validation.IsValid)
            {
                logger.LogInformation(
                    "ComposeDigestActivity: tenant {TenantId} user {RepresentativeUserId} - LLM body accepted. Tokens: {InputTokens} in / {OutputTokens} out.",
                    input.TenantId, input.RepresentativeUserId, draft.InputTokens, draft.OutputTokens);
                return new ComposeDigestOutput(draft.Body, "Llm", null, draft.InputTokens, draft.OutputTokens);
            }

            var reason = "validator rejected the LLM body: " + string.Join("; ", validation.FailedChecks);

            // The whole point of falling back is "the email must still go out" - never let this
            // failure surface as a warning that could be mistaken for an operational problem. It
            // IS one worth knowing about (every rejection here is silent token spend for nothing),
            // just not at a severity that pages anyone.
            logger.LogWarning(
                "ComposeDigestActivity: tenant {TenantId} user {RepresentativeUserId} - LLM body REJECTED, falling back. Tokens spent anyway: {InputTokens} in / {OutputTokens} out. {Reason}\nRejected body was:\n{Body}",
                input.TenantId, input.RepresentativeUserId, draft.InputTokens, draft.OutputTokens, reason, draft.Body);

            var rejected = await renderer.RenderFallbackBodyAsync(aggregates, recipientName: null, weekEnding);
            return new ComposeDigestOutput(rejected, "Fallback", reason, draft.InputTokens, draft.OutputTokens);
        }

        logger.LogWarning(
            "ComposeDigestActivity: tenant {TenantId} user {RepresentativeUserId} - LLM SKIPPED, falling back. Tokens spent anyway: {InputTokens} in / {OutputTokens} out. {Reason}",
            input.TenantId, input.RepresentativeUserId, draft.InputTokens, draft.OutputTokens, draft.SkippedReason);

        /*  The greeting is rendered WITHOUT a recipient name, because this body is shared across
            everyone in the group. The LLM path has the same property - the model is given only
            numbers and never a name - so both paths address the reader generically. Personalising
            here would mean one render per recipient, which is the cost this activity exists to
            avoid.                                                                                */
        var body = await renderer.RenderFallbackBodyAsync(aggregates, recipientName: null, weekEnding);
        return new ComposeDigestOutput(body, "Fallback", draft.SkippedReason, draft.InputTokens, draft.OutputTokens);
    }
}
