using DurableTask.Core;
using Insights.Data;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record CheckTenantTokenBudgetInput(int CustomerId, DateTime AsOfUtc);
public sealed record CheckTenantTokenBudgetOutput(long MonthToDateTokens);

/// <summary>
/// Node 0 - design doc Sec.12.3's per-tenant monthly circuit breaker and its 80%-alert sibling.
/// Runs FIRST, before GatherScopeActivity - same "cheapest gate first" ordering CLAUDE.md 4
/// already established for the entitlement gate: a tenant already over its monthly ceiling costs
/// nothing further, not even a scope lookup.
///
/// Refuses via OrchestrationRefusedException, same shape and same user-facing message as the
/// per-run ceiling in InsightsReportOrchestrator's ChargeAndCheck - never leak WHY internally
/// (CLAUDE.md/Sec.11.3), only that generation could not proceed. ReasonCode differs
/// (MONTHLY_BUDGET_EXCEEDED vs BUDGET_EXCEEDED) so ops can tell "one huge run" from "a tenant
/// hammering the endpoint all month" apart in the internal diagnostics.
///
/// The 80% warning is a LOG line via ILogger, not an OTel metric - InsightsCostMetrics's own doc
/// comment states the rule this follows: "NO TENANT TAG, EVER... per-tenant spend belongs in a
/// log line or a SQL row." Grafana/Loki (not the metrics backend) is what alerts on it, matching
/// Sec.13's own split (LLM traces/metrics -> LangFuse; application logs -> Grafana/Loki).
/// </summary>
public sealed class CheckTenantTokenBudgetActivity(
    ITenantTokenBudgetRepository repository, TenantTokenBudgetSettings settings, ILogger<CheckTenantTokenBudgetActivity> logger)
    : AsyncTaskActivity<CheckTenantTokenBudgetInput, CheckTenantTokenBudgetOutput>
{
    protected override Task<CheckTenantTokenBudgetOutput> ExecuteAsync(TaskContext context, CheckTenantTokenBudgetInput input) => RunAsync(input);

    internal async Task<CheckTenantTokenBudgetOutput> RunAsync(CheckTenantTokenBudgetInput input)
    {
        var startOfMonth = new DateTime(input.AsOfUtc.Year, input.AsOfUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthToDate = await repository.GetTokensSinceAsync(input.CustomerId, startOfMonth, CancellationToken.None);

        if (monthToDate >= settings.MonthlyCeiling)
        {
            throw new OrchestrationRefusedException(
                "MONTHLY_BUDGET_EXCEEDED",
                "We couldn't generate this report to our accuracy standard. Our team has been notified.",
                internalDiagnostics: [$"Tenant {input.CustomerId} has used {monthToDate} tokens this month, ceiling is {settings.MonthlyCeiling}."]);
        }

        var pct = settings.MonthlyCeiling == 0 ? 100 : (int)(monthToDate * 100 / settings.MonthlyCeiling);
        if (pct >= settings.AlertAtPercent)
        {
            logger.LogWarning(
                "Tenant {CustomerId} is at {Pct}% of its monthly token budget ({MonthToDate}/{Ceiling}).",
                input.CustomerId, pct, monthToDate, settings.MonthlyCeiling);
        }

        return new CheckTenantTokenBudgetOutput(monthToDate);
    }
}
