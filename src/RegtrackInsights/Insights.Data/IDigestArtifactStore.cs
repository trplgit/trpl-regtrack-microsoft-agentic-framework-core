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
    /// <param name="identity">
    /// Drives the blob's PATH: <c>&lt;customerId&gt;/free_digest/&lt;yyyy&gt;/&lt;mm&gt;/&lt;artifactId&gt;.html.enc</c>,
    /// the same shape the paid pipeline uses (see <see cref="Insights.Domain.BlobPathContext"/>), partitioned by
    /// the week the digest covers rather than generation time - see <see cref="Insights.Domain.DigestArtifactIdentity"/>.
    /// </param>
    Task<DigestArtifactContent> WriteAsync(string html, DigestArtifactIdentity identity, CancellationToken cancellationToken = default);

    Task<string> ReadAsync(FreeDigestArtifact artifact, CancellationToken cancellationToken = default);
}
