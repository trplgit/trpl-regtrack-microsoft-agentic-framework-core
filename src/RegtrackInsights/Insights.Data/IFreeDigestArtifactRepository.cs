using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Wraps sql/29 - the index between Sunday's GENERATE phase and Monday's SEND phase. See that
/// file's own header for why this is a separate table from GeneratedReport.
/// </summary>
public interface IFreeDigestArtifactRepository
{
    /// <summary>
    /// Atomically reserves the (customer, week, scope) slot before the expensive compose step
    /// runs. Also reclaims a stale pending row left behind by a crashed attempt. Call BEFORE the
    /// LLM/fallback compose - claiming after would defeat the point of a claim.
    /// </summary>
    Task<FreeDigestArtifactClaimResult> ClaimAsync(
        int customerId, DateOnly weekEnding, string scopeSignature, int representativeUserId, string tenantName,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a claimed slot complete once the content is encrypted and written to blob. Fails closed without a real blob location (sql/29, error 51212).</summary>
    Task CompleteAsync(
        Guid artifactId, DateTime asOfUtc, string source, int recipientCount,
        string blobContainer, string blobPath, byte[] encryptedAesKey, string keyVaultObjectName, string keyVaultObjectVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Generation failed - hands the slot back so a later tick can retry this scope group.</summary>
    Task ReleaseAsync(Guid artifactId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every COMPLETE, not-yet-dispatched artifact for this tenant generated within
    /// <paramref name="maxAgeDays"/>. An artifact older than that is never returned - see sql/29's
    /// own comment: a stale digest is a wrong digest, never mailed regardless of how it got stale.
    /// </summary>
    Task<IReadOnlyList<FreeDigestArtifact>> GetForDispatchAsync(
        int customerId, int maxAgeDays, CancellationToken cancellationToken = default);

    /// <summary>Informational stamp only - see FreeDigestArtifact's own doc comment on why this is never an idempotency guard.</summary>
    Task MarkDispatchedAsync(Guid artifactId, CancellationToken cancellationToken = default);

    /// <summary>Artifacts old enough to purge, for the retention sweep (ADR-0001 D7).</summary>
    Task<IReadOnlyList<(Guid ArtifactId, string BlobContainer, string BlobPath)>> GetForPurgeAsync(
        int retentionDays, CancellationToken cancellationToken = default);

    /// <summary>Deletes one artifact's index row. Call AFTER the blob itself is deleted - an orphaned blob wastes storage; an orphaned row is a dangling reference.</summary>
    Task DeleteAsync(Guid artifactId, int retentionDays, CancellationToken cancellationToken = default);
}
