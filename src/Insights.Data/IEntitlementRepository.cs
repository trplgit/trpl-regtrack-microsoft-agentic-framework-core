using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Wraps the entitlement gate (sql/04 Part 2). Cheapest-first: an unentitled tenant
/// costs literally nothing - no aggregation, no LLM call, no email.
/// </summary>
public interface IEntitlementRepository
{
    /// <summary>
    /// Evaluates whether a tenant is entitled, not superseded, and has recipients,
    /// for the given tier. [TRAP] ProductMapping.IsActive is INVERTED - the SQL
    /// already accounts for this; callers never touch IsActive directly.
    /// </summary>
    Task<EntitlementGateResult> EvaluateGateAsync(int customerId, EntitlementTier tier, CancellationToken cancellationToken = default);
}
