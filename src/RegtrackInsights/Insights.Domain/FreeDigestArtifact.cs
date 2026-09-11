namespace Insights.Domain;

/// <summary>
/// One scope group's saved digest content - the Sunday GENERATE phase's output, and what the
/// Monday SEND phase reads back. Mirrors GeneratedReport's shape (metadata only, content lives
/// encrypted in blob storage) but is otherwise unrelated - see sql/29's own header for why this is
/// a separate table rather than an extension of GeneratedReport.
/// </summary>
public sealed record FreeDigestArtifact(
    Guid ArtifactId,
    int CustomerId,
    DateOnly WeekEnding,
    string ScopeSignature,
    int RepresentativeUserId,
    string TenantName,
    DateTime AsOfUtc,
    DateTime GeneratedAtUtc,
    string Source,
    string BlobContainer,
    string BlobPath,
    byte[] EncryptedAesKey,
    string KeyVaultObjectName,
    string KeyVaultObjectVersion);

/// <summary>The result of a claim attempt - see ICooldownRepository/CooldownResult for the same "record, don't throw, on an expected outcome" shape.</summary>
public sealed record FreeDigestArtifactClaimResult(bool Claimed, Guid? ArtifactId);
