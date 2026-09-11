using Insights.Data;
using Insights.Domain;

namespace Insights.Persistence;

/// <inheritdoc cref="IDigestArtifactStore"/>
public sealed class AzureDigestArtifactStore(
    IReportEncryptor encryptor, IReportDecryptor decryptor, AzureReportBlobWriter blobWriter) : IDigestArtifactStore
{
    /// <summary>
    /// <paramref name="blobWriter"/> is a SEPARATE AzureReportBlobWriter instance bound to the
    /// digest container at construction (see FreeDigestRegistration) - the same class the paid
    /// pipeline uses, just pointed at a different container, so the write/read shape (opaque GUID
    /// name, IV-prepended ciphertext) matches exactly without a second implementation to maintain.
    /// </summary>
    public async Task<DigestArtifactContent> WriteAsync(string html, CancellationToken cancellationToken = default)
    {
        var envelope = await encryptor.EncryptAsync(html, cancellationToken);

        // [MERGE FIX, 2026-09-11 - FLAG FOR TANVI] AzureReportBlobWriter.WriteAsync now requires a
        // BlobPathContext (tenant/type/year/month path layout, added on developer-charannagarj
        // this same week) - this call predated that and passed only the envelope, matching the old
        // flat opaque-GUID scheme this class's own doc comment above still describes. No real
        // TenantId reaches this method - PersistDigestArtifactActivity's input carries only a
        // display TenantName, never a numeric id - so TenantId is a 0 SENTINEL here, not a real
        // tenant. This changes a digest blob's path from a flat "{guid}.html.enc" to
        // "0/digest/{yyyy}/{mm}/{guid}.html.enc". If digests should stay fully flat/tenant-agnostic
        // instead, AzureReportBlobWriter needs a path-building option that does not assume a real
        // tenant - please confirm which is intended before this ships.
        var pathContext = new BlobPathContext(TenantId: 0, ReportType: "digest", GeneratedAtUtc: DateTime.UtcNow, ReportId: Guid.NewGuid());
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
