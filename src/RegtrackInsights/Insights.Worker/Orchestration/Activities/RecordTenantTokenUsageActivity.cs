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
    protected override Task<RecordTenantTokenUsageOutput> ExecuteAsync(TaskContext context, RecordTenantTokenUsageInput input) => RunAsync(input);

    internal async Task<RecordTenantTokenUsageOutput> RunAsync(RecordTenantTokenUsageInput input)
    {
        if (input.TotalTokens > 0)
            await repository.RecordUsageAsync(input.CustomerId, input.RunId, input.TotalTokens, CancellationToken.None);

        return new RecordTenantTokenUsageOutput();
    }
}
