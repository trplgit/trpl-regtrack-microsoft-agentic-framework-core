using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Wraps the scope resolution service (sql/03) - the security boundary. Deterministic,
/// no LLM anywhere near it. Scope is two-dimensional (BranchID x CategoryId); every
/// method here resolves or checks BOTH axes, never branch alone.
/// </summary>
public interface IScopeRepository
{
    /// <summary>
    /// A user's authorised (BranchID, CategoryId) pairs for one tenant. Empty means
    /// DENY - callers must refuse, never treat an empty scope as unrestricted access.
    /// </summary>
    Task<IReadOnlyList<ScopePair>> GetScopePairsAsync(int userId, int customerId, CancellationToken cancellationToken = default);

    /// <summary>Classifies a user's scope: DENY / tenant_wide / functional / entity_scoped.</summary>
    Task<ScopeClassification> ClassifyScopeAsync(int userId, int customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Post-flight audit: re-verifies every scoped row against the scope pair set on
    /// both axes. Throws <see cref="ScopeAuditFailedException"/> if any violation is
    /// found - the caller must treat that as an unconditional refusal to publish, not
    /// a warning to log and continue past.
    /// </summary>
    Task<ScopeAuditResult> AuditScopeAsync(int userId, int customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Provisioning-time check: users mapped to RegInsights (18/19) with no scope rows
    /// configured. Run from a provisioning runbook or an ops alert, not per-report.
    /// </summary>
    Task<IReadOnlyList<ScopelessUser>> FindScopelessUsersAsync(CancellationToken cancellationToken = default);
}
