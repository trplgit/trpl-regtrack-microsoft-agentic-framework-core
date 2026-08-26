using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Insights.Data;
using Insights.Domain;

namespace Insights.Persistence;

/// <summary>
/// Design doc Sec.9.3 steps 3-4. Writes DECRYPTED plaintext to a throwaway blob path, separate from
/// the permanent encrypted report AzureReportBlobWriter owns, and mints a short-lived SAS against
/// THAT copy only - the permanent artifact never gets a SAS minted against it, ever (Sec.9.3:
/// "No public URL. No long-lived SAS. Reports are served only through the application.").
///
/// [KNOWN LIMITATION, flagged rather than silently glossed over] "Single-use" (Sec.9.3) is
/// approximated here as "short-lived and freshly minted per request" (Reports:SasLifetimeMinutes,
/// a few minutes) - a real Azure Storage SAS has no native single-use revocation without a stored
/// access policy the app explicitly revokes after first use, which is real added infrastructure
/// this slice does not build. Two consequences worth tracking as fast-follows, not silently
/// accepted: (1) the same URL is technically replayable until it expires, not strictly one-shot;
/// (2) the decrypted plaintext blob this writes has no automatic cleanup after that window - it
/// needs a blob lifecycle-management rule on the container (ops-side config, not app code) to purge
/// the "views/" prefix after e.g. one hour, or decrypted PII sits in storage indefinitely.
/// </summary>
public sealed class AzureReportViewPublisher(string storageConnectionString, string containerName) : IReportViewPublisher
{
    public async Task<ReportViewLocation> PublishAsync(string plaintextHtml, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var service = new BlobServiceClient(storageConnectionString);
        var container = service.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        // "views/" prefix keeps these throwaway copies visibly separate from the permanent
        // encrypted artifacts AzureReportBlobWriter writes at the container root - so a lifecycle
        // rule (or a human browsing the container) can target exactly this prefix and nothing else.
        var blobPath = $"views/{Guid.NewGuid():N}.html";
        var blob = container.GetBlobClient(blobPath);

        using var contentStream = new MemoryStream(Encoding.UTF8.GetBytes(plaintextHtml));
        await blob.UploadAsync(
            contentStream,
            new BlobHttpHeaders { ContentType = "text/html; charset=utf-8" },
            cancellationToken: cancellationToken);

        var expiresUtc = DateTimeOffset.UtcNow.Add(ttl);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = expiresUtc,
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        if (!blob.CanGenerateSasUri)
            throw new InvalidOperationException(
                "The blob client cannot generate a SAS - Azure:BlobConnectionString must carry an account key (shared-key auth), not a SAS-only or Azure AD connection string.");

        var sasUri = blob.GenerateSasUri(sasBuilder);

        return new ReportViewLocation(sasUri, expiresUtc);
    }
}
