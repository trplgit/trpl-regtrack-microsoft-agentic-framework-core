using DurableTask.Core;
using Insights.Data;

namespace Insights.Worker.Orchestration.Activities;

public sealed record RecordTenantTokenUsageInput(int CustomerId, string RunId, long TotalTokens);
public sealed record RecordTenantTokenUsageOutput;

/// <summary>
/// The write half of design doc Sec.12.3's per-tenant monthly circuit breaker
/// (CheckTenantTokenBudgetActivity is the read half). Called from a `finally` around the whole
/// orchestrator body, so a run's actual spend is recorded whether it fully succeeded, refused at
/// the publish gate, or threw anywhere in between - Sec.12.3 is about real spend, not just
/// successful reports. Idempotent on RunId (SqlTenantTokenBudgetRepository) - a DTFx replay that
/// re-executes this activity must never double-count (CLAUDE.md 6).
/// </summary>
public sealed class RecordTenantTokenUsageActivity(ITenantTokenBudgetRepository repository)
    : AsyncTaskActivity<RecordTenantTokenUsageInput, RecordTenantTokenUsageOutput>
{
    protected override Task<RecordTenantTokenUsageOutput> ExecuteAsync(TaskContext context, RecordTenantTokenUsageInput input) =>
        RunAsync(input, context.OrchestrationInstance?.ExecutionId);

    /// <summary>
    /// [FIX 2026-09-28, found live] The run id is identical for every re-run of the same
    /// tenant/dimension/period, and the ledger is idempotent per key - so every re-run's real spend
    /// was dropped (one real 7-dimension request lost 235,714 tokens for Users + Act). The key is now
    /// per EXECUTION: a new run adds its own row, a DTFx redelivery of the same execution still hits
    /// the same key. Same approach as the report-id fix in PersistActivity. Done here, not in the
    /// orchestrator, because OrchestrationContext.OrchestrationInstance is not mockable there.
    /// </summary>
    internal async Task<RecordTenantTokenUsageOutput> RunAsync(RecordTenantTokenUsageInput input, string? executionId = null)
    {
        var key = string.IsNullOrEmpty(executionId) ? input.RunId : $"{input.RunId}|exec:{executionId}";
        if (input.TotalTokens > 0)
            await repository.RecordUsageAsync(input.CustomerId, key, input.TotalTokens, CancellationToken.None);

        return new RecordTenantTokenUsageOutput();
    }
}
