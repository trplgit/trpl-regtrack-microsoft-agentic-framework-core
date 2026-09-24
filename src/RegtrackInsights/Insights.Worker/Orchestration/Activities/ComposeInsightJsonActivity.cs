using System.Globalization;
using DurableTask.Core;
using Insights.Domain;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ComposeInsightJsonInput(int TenantId, int RepresentativeUserId, string WeekEnding, string? AsOf);

public sealed record ComposeInsightJsonOutput(InsightCard Card, int InputTokens, int OutputTokens);

/// <summary>
/// ADR-0002 node 2, re-sourced by ADR-0004 (revised 2026-09-24): produce ONE insight card for a
/// whole scope group - same cost unit as ComposeDigestActivity, one LLM call per distinct scope,
/// never per recipient. The card is built from the ONE monthly subject procedure that this Sunday's
/// email also covers, so a reader never gets a card about one subject and an email about another;
/// <see cref="MonthlyDigestCalendar"/> decides which from the date alone. The orchestrator fans the
/// same card out to every recipient in the group; only <c>insight_id</c> and <c>user_id</c> differ
/// per recipient, and the mapper re-stamps those.
///
/// A SQL refusal propagates (fail closed). An LLM-side failure never blocks: the card ships with
/// deterministic text instead, and <c>InsightCardResult.Source</c> records that it did - the card
/// itself carries no provenance, so that log line is the only signal the text is degrading.
/// </summary>
public sealed class ComposeInsightJsonActivity(InsightCardComposer composer, ILogger<ComposeInsightJsonActivity> logger)
    : AsyncTaskActivity<ComposeInsightJsonInput, ComposeInsightJsonOutput>
{
    protected override Task<ComposeInsightJsonOutput> ExecuteAsync(TaskContext context, ComposeInsightJsonInput input) => RunAsync(input);

    internal async Task<ComposeInsightJsonOutput> RunAsync(ComposeInsightJsonInput input)
    {
        var weekEnding = DateOnly.ParseExact(input.WeekEnding, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var result = await composer.ComposeAsync(input.TenantId, input.RepresentativeUserId, weekEnding, input.AsOf);

        logger.LogInformation(
            "ComposeInsightJsonActivity: tenant {TenantId} user {RepresentativeUserId} - {Subject} card built ({Source}). Tokens: {InputTokens} in / {OutputTokens} out.",
            input.TenantId, input.RepresentativeUserId, result.Subject, result.Source, result.InputTokens, result.OutputTokens);

        return new ComposeInsightJsonOutput(result.Card, result.InputTokens, result.OutputTokens);
    }
}
