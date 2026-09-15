using DurableTask.Core;
using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ComposeInsightJsonInput(int TenantId, int RepresentativeUserId, string WeekEnding, string? AsOf);

public sealed record ComposeInsightJsonOutput(FreeDigestAggregates Aggregates, InsightNarrative Narrative, int InputTokens, int OutputTokens);

/// <summary>
/// ADR-0002 (2026-09-11) node 2: produce ONE "current insight" narrative for a whole scope group -
/// same cost-saving unit as ComposeDigestActivity (one LLM call per distinct scope, never per
/// recipient; see that class's own doc comment). The orchestrator fans the returned
/// aggregates+narrative out to every recipient in the group unchanged, differing only by UserId.
///
/// Same "this must never block the run" shape as ComposeDigestActivity: over budget, truncated, or
/// a validator rejection all fall back to the deterministic InsightFallbackNarrative rather than
/// throwing.
/// </summary>
public sealed class ComposeInsightJsonActivity(
    IFreeDigestRepository repository,
    InsightNarrativeWriter writer,
    FreeDigestSettings settings,
    ILogger<ComposeInsightJsonActivity> logger)
    : AsyncTaskActivity<ComposeInsightJsonInput, ComposeInsightJsonOutput>
{
    protected override Task<ComposeInsightJsonOutput> ExecuteAsync(TaskContext context, ComposeInsightJsonInput input) => RunAsync(input);

    internal async Task<ComposeInsightJsonOutput> RunAsync(ComposeInsightJsonInput input)
    {
        var asOf = string.IsNullOrWhiteSpace(input.AsOf) ? (DateTime?)null : DateTime.Parse(input.AsOf);
        var aggregates = await repository.GetAggregatesAsync(input.TenantId, input.RepresentativeUserId, asOf);

        // Chosen deterministically BEFORE the LLM is ever called - see InsightFocus's own doc
        // comment (CLAUDE.md non-negotiable 5).
        var focus = InsightFocus.SelectFor(aggregates);

        var draft = await writer.WriteAsync(aggregates, focus, settings.InsightJsonTokenCap);

        if (draft.Source == FreeDigestSource.Llm)
        {
            // Remainder is truthful arithmetic handed to the LLM (Denominator - Value, computed
            // before the LLM is ever called - see InsightFocus), not a number it invented, but it
            // does not literally appear as an aggregate field - see FreeDigestValidator.Validate's
            // own doc comment on why this must be passed explicitly.
            var extraAllowedNumbers = focus.Remainder is { } remainder ? [remainder] : Array.Empty<int>();
            var validation = FreeDigestValidator.Validate($"{draft.Headline}\n{draft.Explanation}", aggregates, extraAllowedNumbers);
            if (validation.IsValid)
            {
                logger.LogInformation(
                    "ComposeInsightJsonActivity: tenant {TenantId} user {RepresentativeUserId} - LLM narrative accepted. Tokens: {InputTokens} in / {OutputTokens} out.",
                    input.TenantId, input.RepresentativeUserId, draft.InputTokens, draft.OutputTokens);
                return new ComposeInsightJsonOutput(
                    aggregates, new InsightNarrative(focus.SeverityBand, draft.Headline, draft.Explanation, "Llm"),
                    draft.InputTokens, draft.OutputTokens);
            }

            var reason = "validator rejected the LLM narrative: " + string.Join("; ", validation.FailedChecks);
            logger.LogWarning(
                "ComposeInsightJsonActivity: tenant {TenantId} user {RepresentativeUserId} - LLM narrative REJECTED, falling back. Tokens spent anyway: {InputTokens} in / {OutputTokens} out. {Reason}",
                input.TenantId, input.RepresentativeUserId, draft.InputTokens, draft.OutputTokens, reason);

            var (rejectedHeadline, rejectedExplanation) = InsightFallbackNarrative.Build(focus);
            return new ComposeInsightJsonOutput(
                aggregates, new InsightNarrative(focus.SeverityBand, rejectedHeadline, rejectedExplanation, "Fallback"),
                draft.InputTokens, draft.OutputTokens);
        }

        logger.LogWarning(
            "ComposeInsightJsonActivity: tenant {TenantId} user {RepresentativeUserId} - LLM SKIPPED, falling back. Tokens spent anyway: {InputTokens} in / {OutputTokens} out. {Reason}",
            input.TenantId, input.RepresentativeUserId, draft.InputTokens, draft.OutputTokens, draft.SkippedReason);

        var (headline, explanation) = InsightFallbackNarrative.Build(focus);
        return new ComposeInsightJsonOutput(
            aggregates, new InsightNarrative(focus.SeverityBand, headline, explanation, "Fallback"),
            draft.InputTokens, draft.OutputTokens);
    }
}
