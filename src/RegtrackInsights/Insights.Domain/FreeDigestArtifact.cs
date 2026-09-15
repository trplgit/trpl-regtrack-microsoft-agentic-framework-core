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

/// <summary>
/// What <see cref="Insights.Data.IDigestArtifactStore"/> needs to build the digest blob's PATH -
/// mirrors <see cref="BlobPathContext"/>'s role in the paid pipeline, but keyed on the slot's
/// natural key (<see cref="CustomerId"/>, <see cref="WeekEnding"/>, <see cref="ArtifactId"/>)
/// rather than a generation timestamp, since <c>GeneratedAtUtc</c> is minted and re-stamped by SQL
/// (usp_Insights_FreeDigestArtifactClaim) and is never returned to the activity. Deliberately has
/// no string member - <c>TenantName</c> (PII) is structurally unable to reach the blob path through
/// this type.
/// </summary>
public sealed record DigestArtifactIdentity(int CustomerId, DateOnly WeekEnding, Guid ArtifactId);
