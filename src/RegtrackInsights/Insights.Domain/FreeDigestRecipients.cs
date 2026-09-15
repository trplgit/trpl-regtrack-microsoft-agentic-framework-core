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
/// Sourced from dbo.tvfInsightsManagementUsers, NOT UserCustomerMapping - that table's
/// ProductID/IsActive columns cannot answer "who is a recipient" in production (all 65 rows
/// carry ProductID = NULL, IsActive = 1; see sql/01). The gate (usp_Insights_FreeDigestGate)
/// uses the same function, but this list is narrower and authoritative: the gate is an
/// upper-bound cost pre-filter (no email/suppression check on its own count as of the fix
/// that made both subtract opt-outs, 2026-09-10), while this list also excludes blank emails.
/// The two counts CAN legitimately disagree - see SqlFreeDigestRepository.GetRecipientsAsync's
/// doc comment for why that is safe rather than a bug to chase.
/// </summary>
public sealed record FreeDigestRecipient(long UserId, string Email, string? Name);
