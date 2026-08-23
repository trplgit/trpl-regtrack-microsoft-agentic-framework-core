using DurableTask.Core;
using Insights.Agents;
using Insights.Data;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ComposeDigestInput(int TenantId, int RepresentativeUserId, string WeekEnding, string? AsOf);

public sealed record ComposeDigestOutput(string Body, string Source, string? Reason);

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
    FreeDigestSettings settings)
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
                return new ComposeDigestOutput(draft.Body, "Llm", null);

            var rejected = await renderer.RenderFallbackBodyAsync(aggregates, recipientName: null, weekEnding);
            return new ComposeDigestOutput(rejected, "Fallback",
                "validator rejected the LLM body: " + string.Join("; ", validation.FailedChecks));
        }

        /*  The greeting is rendered WITHOUT a recipient name, because this body is shared across
            everyone in the group. The LLM path has the same property - the model is given only
            numbers and never a name - so both paths address the reader generically. Personalising
            here would mean one render per recipient, which is the cost this activity exists to
            avoid.                                                                                */
        var body = await renderer.RenderFallbackBodyAsync(aggregates, recipientName: null, weekEnding);
        return new ComposeDigestOutput(body, "Fallback", draft.SkippedReason);
    }
}
