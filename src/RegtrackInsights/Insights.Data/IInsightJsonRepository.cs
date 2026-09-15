namespace Insights.Data;

/// <summary>
/// Wraps sql/31 - the per-user claim/outcome log for the weekly insight JSON POST (ADR-0002,
/// 2026-09-11). Same "claim before acting, atomic under a PK" shape as
/// IFreeDigestRepository.TryClaimSendAsync (sql/15) - a Durable Task replay must not double-POST.
/// </summary>
public interface IInsightJsonRepository
{
    /// <summary>Atomically claims this week's POST for one recipient. Returns true to exactly ONE caller per (customer, user, week); everyone after gets false and must not POST.</summary>
    Task<bool> TryClaimPostAsync(int customerId, long userId, DateOnly weekEnding, CancellationToken cancellationToken = default);

    /// <summary>Records what happened - "posted" or "failed" - for observability.</summary>
    Task RecordOutcomeAsync(int customerId, long userId, DateOnly weekEnding, string outcome, string? detail = null, CancellationToken cancellationToken = default);

    /// <summary>Hands an unposted claim back so a retry is possible in the same week.</summary>
    Task ReleaseClaimAsync(int customerId, long userId, DateOnly weekEnding, CancellationToken cancellationToken = default);
}
