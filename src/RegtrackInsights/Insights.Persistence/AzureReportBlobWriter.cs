using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Insights.Data;
using Insights.Domain;

namespace Insights.Persistence;

/// <summary>
/// Writes an already-encrypted report to blob storage. Container name and blob-metadata shape both
/// match the real DocAI convention (ComplianceFileUploadService.cs), so a report blob looks the same
/// to anyone inspecting the storage account as an existing DocAI file blob.
///
/// [TRAP] Container is "insights-reports-temp" - the real name on the storage account today, not
/// "insights-reports" (what config/design doc name). Confirmed 2026-08-24: the account only has the
/// "-temp" one. One-line config change once ops provisions the real name; not blocking the write
/// path on a rename.
/// </summary>
public sealed class AzureReportBlobWriter(string storageConnectionString, string containerName) : IReportBlobWriter, IReportBlobReader
{
    /// <summary>Item 14's read half. Downloads the still-encrypted bytes exactly as WriteAsync left them - IV-prepended ciphertext, no decryption here.</summary>
    public async Task<byte[]> ReadAsync(BlobLocation location, CancellationToken cancellationToken = default)
    {
        var service = new BlobServiceClient(storageConnectionString);
        var blob = service.GetBlobContainerClient(location.Container).GetBlobClient(location.Path);
        var downloaded = await blob.DownloadContentAsync(cancellationToken);
        return downloaded.Value.Content.ToArray();
    }

    public async Task<BlobLocation> WriteAsync(EncryptedReportEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var service = new BlobServiceClient(storageConnectionString);
        var container = service.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        // Opaque GUID name, same convention as DocAI - the blob path itself must not leak
        // tenant/report identity; that lives in the GeneratedReport SQL row instead.
        var blobPath = $"{Guid.NewGuid():N}.dat";
        var blob = container.GetBlobClient(blobPath);

        using var contentStream = new MemoryStream(envelope.Content, writable: false);
        await blob.UploadAsync(contentStream, overwrite: true, cancellationToken);

        var metadata = new Dictionary<string, string>
        {
            ["keyvault_object_name"] = envelope.KeyVaultObjectName,
            ["keyvault_object_type"] = "key",
            ["keyvault_object_version"] = envelope.KeyVaultObjectVersion,
            // Matches the real DocAI metadata shape byte-for-byte - see GeneratedReport.KeyVaultObjectSalt.
            ["keyvault_object_salt"] = "0",
            ["product_name"] = "insights",
        };
        await blob.SetMetadataAsync(metadata, cancellationToken: cancellationToken);

        return new BlobLocation(containerName, blobPath);
    }
}
