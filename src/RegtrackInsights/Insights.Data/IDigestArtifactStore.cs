using Insights.Domain;

namespace Insights.Data;

/// <summary>Everything CompleteAsync needs to record an artifact once its content is safely in blob storage.</summary>
public sealed record DigestArtifactContent(
    string BlobContainer, string BlobPath, byte[] EncryptedAesKey, string KeyVaultObjectName, string KeyVaultObjectVersion);

/// <summary>
/// Encrypt-and-write / read-and-decrypt for the free digest's own blob container
/// ("insights-digests" - separate from the paid pipeline's "insights-reports", see sql/29's own
/// header for why). Reuses the SAME encryption story as the paid pipeline (IReportEncryptor/
/// IReportDecryptor) - one consistent scheme, never a second one invented for a "lower stakes"
/// artifact.
/// </summary>
public interface IDigestArtifactStore
{
    Task<DigestArtifactContent> WriteAsync(string html, CancellationToken cancellationToken = default);

    Task<string> ReadAsync(FreeDigestArtifact artifact, CancellationToken cancellationToken = default);
}
