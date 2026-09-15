namespace Insights.Data;

/// <summary>
/// Design doc Sec.12.3's per-tenant monthly circuit breaker and its 80%-alert sibling. Backed by
/// sql/20_tenant_token_usage.sql - an append-only ledger, one row per run, written REGARDLESS of
/// whether that run succeeded, refused, or threw (Sec.12.3 is about actual spend, not just
/// successful reports).
/// </summary>
public interface ITenantTokenBudgetRepository
{
    /// <summary>Sum of TotalTokens for this tenant since <paramref name="sinceUtc"/> (inclusive) - the caller decides what "this month" means, this just sums.</summary>
    Task<long> GetTokensSinceAsync(int customerId, DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>Appends one ledger row. Never throws on a duplicate RunId - a replay of the same activity must not double-count (CLAUDE.md 6: LLM activities must be idempotent).</summary>
    Task RecordUsageAsync(int customerId, string runId, long totalTokens, CancellationToken cancellationToken = default);
}
