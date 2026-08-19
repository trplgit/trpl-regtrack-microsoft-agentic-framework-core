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
}
