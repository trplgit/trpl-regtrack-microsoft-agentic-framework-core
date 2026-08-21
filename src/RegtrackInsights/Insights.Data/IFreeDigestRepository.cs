using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Wraps the free weekly digest (sql/06) - product 18, RegInsights Basic.
///
/// CALL ORDER IS LOAD-BEARING: <see cref="EvaluateGateAsync"/> first, and only aggregate when it
/// returns <see cref="EntitlementDecision.Proceed"/>. The gate is cheapest-first precisely so an
/// unentitled tenant costs nothing - no aggregation, no LLM call, no email. Aggregating first and
/// checking afterwards spends the money the gate exists to save.
/// </summary>
public interface IFreeDigestRepository
{
    /// <summary>
    /// Entitled, not superseded, and has recipients? Evaluated at execution time, never cached.
    /// [TRAP] ProductMapping.IsActive is INVERTED - handled entirely in SQL.
    /// </summary>
    Task<FreeDigestGateResult> EvaluateGateAsync(int customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The fifteen integers that are the entire LLM input. Read the remarks on
    /// <see cref="FreeDigestAggregates"/> before deriving anything from them - several otherwise
    /// natural derivations are banned outright.
    ///
    /// Pass <paramref name="userId"/> to constrain to that recipient's authorised scope. Pass null
    /// for a tenant-wide digest ONLY when the recipient has been verified tenant-wide - a free
    /// recipient must never receive numbers from outside their scope.
    ///
    /// Throws <see cref="FreeDigestDictionaryGapException"/> if the Critical RiskType is
    /// unmapped. Send nothing in that case; do not fall back to a literal.
    /// </summary>
    Task<FreeDigestAggregates> GetAggregatesAsync(
        int customerId, int? userId = null, DateTime? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every tenant mapped-and-enabled for product 18, with its display name.
    ///
    /// Deliberately does NOT exclude tenants that also hold the paid product: entitlement is
    /// evaluated at execution time by the gate, which handles supersession. Filtering here too
    /// would put the inverted-IsActive rule in two places.
    /// </summary>
    Task<IReadOnlyList<FreeDigestTenant>> GetEntitledTenantsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The people who should receive this tenant's digest.
    ///
    /// Uses the SAME predicate usp_Insights_FreeDigestGate counts with, so the returned count
    /// agrees with the gate's RecipientCount. Users with no usable email address are excluded
    /// here rather than failing at send time.
    /// </summary>
    Task<IReadOnlyList<FreeDigestRecipient>> GetRecipientsAsync(int customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims this week's send for one recipient. Returns true to exactly ONE caller
    /// per (customer, user, week); everyone after gets false and must not send.
    ///
    /// Claim BEFORE sending, never after - a check-then-send races, and at 600 tenants a
    /// duplicate storm is an incident. The trade is deliberate: a crash between claiming and
    /// sending loses that week's digest rather than sending it twice.
    /// </summary>
    Task<bool> TryClaimSendAsync(int customerId, long userId, DateOnly weekEnding, CancellationToken cancellationToken = default);

    /// <summary>Records what happened, for metrics and for "did this tenant get last week's digest?".</summary>
    Task RecordOutcomeAsync(int customerId, long userId, DateOnly weekEnding, string outcome,
        string? source = null, string? providerUsed = null, string? detail = null,
        CancellationToken cancellationToken = default);

    /// <summary>Hands an unsent claim back so a retry is possible in the same week.</summary>
    Task ReleaseClaimAsync(int customerId, long userId, DateOnly weekEnding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Suppresses a recipient permanently - an unsubscribe request or a hard bounce.
    ///
    /// Idempotent, and the FIRST reason wins: a bounce arriving after someone unsubscribed must
    /// not rewrite the record into "the address was dead", which would justify re-subscribing
    /// them later. Called by the unsubscribe and bounce endpoints, which live in the RegTrack API
    /// because this worker has no ingress (CLAUDE.md 6).
    /// </summary>
    Task SuppressAsync(int customerId, long userId, DigestSuppressionReason reason,
        string? detail = null, CancellationToken cancellationToken = default);

    /// <summary>Who is suppressed and why - for "this person stopped getting the digest" support questions.</summary>
    Task<IReadOnlyList<DigestSuppression>> GetSuppressionsAsync(int? customerId = null, CancellationToken cancellationToken = default);
}
