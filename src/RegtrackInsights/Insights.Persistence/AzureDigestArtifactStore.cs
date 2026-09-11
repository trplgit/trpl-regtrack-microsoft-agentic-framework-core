using Insights.Data;
using Insights.Domain;

namespace Insights.Persistence;

/// <inheritdoc cref="IDigestArtifactStore"/>
public sealed class AzureDigestArtifactStore(
    IReportEncryptor encryptor, IReportDecryptor decryptor, AzureReportBlobWriter blobWriter) : IDigestArtifactStore
{
    /// <summary>
    /// The only spelling of the digest report-type slug - owned by the store, never a caller
    /// parameter, so a second spelling can never appear in a digest blob path.
    /// </summary>
    private const string DigestReportType = "free_digest";

    /// <summary>
    /// <paramref name="blobWriter"/> is a SEPARATE AzureReportBlobWriter instance bound to the
    /// digest container at construction (see FreeDigestRegistration) - the same class the paid
    /// pipeline uses, just pointed at a different container, so the write/read shape (opaque GUID
    /// name, IV-prepended ciphertext) matches exactly without a second implementation to maintain.
    /// The path also matches the paid shape - see <see cref="DigestReportType"/> and
    /// <see cref="Insights.Domain.DigestArtifactIdentity"/>.
    /// </summary>
    public async Task<DigestArtifactContent> WriteAsync(string html, DigestArtifactIdentity identity, CancellationToken cancellationToken = default)
    {
        var envelope = await encryptor.EncryptAsync(html, cancellationToken);
        var pathContext = new BlobPathContext(identity.CustomerId, DigestReportType, identity.WeekEnding, identity.ArtifactId);
        var location = await blobWriter.WriteAsync(envelope, pathContext, cancellationToken);

        return new DigestArtifactContent(
            location.Container, location.Path, envelope.EncryptedAesKey, envelope.KeyVaultObjectName, envelope.KeyVaultObjectVersion);
    }

    public async Task<string> ReadAsync(FreeDigestArtifact artifact, CancellationToken cancellationToken = default)
    {
        var encryptedContent = await blobWriter.ReadAsync(
            new BlobLocation(artifact.BlobContainer, artifact.BlobPath), cancellationToken);

        return await decryptor.DecryptAsync(
            encryptedContent, artifact.EncryptedAesKey, artifact.KeyVaultObjectVersion, cancellationToken);
    }
}
