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

/// <summary>Why one recipient did or did not receive a digest - the raw material for insights.digest.sent_total / skipped_total{reason}.</summary>
public sealed record FreeDigestRecipientOutcome(
    long UserId,
    string Email,
    bool Sent,
    FreeDigestSource? Source,
    string? Reason,
    string? ProviderUsed);

/// <summary>
/// Outcome of one tenant's digest run. A tenant the gate refused still returns a result -
/// with <see cref="Decision"/> explaining why and no recipients - rather than throwing, so a
/// batch over 600 tenants records refusals instead of aborting on the first one.
/// </summary>
public sealed record FreeDigestTenantResult(
    int CustomerId,
    string TenantName,
    EntitlementDecision Decision,
    string Reason,
    IReadOnlyList<FreeDigestRecipientOutcome> Recipients)
{
    public int SentCount => Recipients.Count(r => r.Sent);
    public int SkippedCount => Recipients.Count(r => !r.Sent);
}

/// <summary>Outcome of a whole weekly run.</summary>
public sealed record FreeDigestBatchResult(IReadOnlyList<FreeDigestTenantResult> Tenants)
{
    public int TenantsProcessed => Tenants.Count;
    public int TenantsSkipped => Tenants.Count(t => t.Decision != EntitlementDecision.Proceed);
    public int EmailsSent => Tenants.Sum(t => t.SentCount);
    public int RecipientsSkipped => Tenants.Sum(t => t.SkippedCount);
}

