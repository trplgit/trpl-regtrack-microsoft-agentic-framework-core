namespace Insights.Domain;

/// <summary>
/// A tenant mapped-and-enabled for the free product. Sourced from ProductMapping, never from
/// UserCustomerMapping - that table is not a reliable user-to-tenant link (checklist nuance 5).
///
/// Tenants superseded by the paid product are deliberately NOT filtered out here. Entitlement is
/// evaluated at JOB EXECUTION TIME by usp_Insights_FreeDigestGate, which already handles
/// supersession; duplicating that rule would give two places to get the IsActive inversion wrong.
/// </summary>
public sealed record FreeDigestTenant(int CustomerId, string TenantName);

/// <summary>
/// One person who should receive the digest for a tenant.
///
/// The enumeration predicate MIRRORS the one usp_Insights_FreeDigestGate counts with
/// (ProductID = 18, UserCustomerMapping.IsActive = 0, User.IsDeleted = 0). If the two ever
/// diverge, the gate's RecipientCount stops agreeing with the list and EXIT_NO_RECIPIENTS
/// becomes unreliable - two sources of truth for the same question.
/// </summary>
public sealed record FreeDigestRecipient(long UserId, string Email, string? Name);
