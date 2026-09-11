using System.Globalization;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Insights.Data;
using Insights.Domain;

namespace Insights.Persistence;

/// <summary>
/// Writes an already-encrypted report to blob storage. Blob metadata shape matches the real DocAI
/// convention (ComplianceFileUploadService.cs), so a report blob looks the same to anyone inspecting
/// the storage account as an existing DocAI file blob.
///
/// [TRAP] Container is "insights-reports-temp" - the real name on the storage account today, not
/// "insights-reports" (what config/design doc name). Confirmed 2026-08-24: the account only has the
/// "-temp" one. One-line config change once ops provisions the real name; not blocking the write
/// path on a rename.
///
/// [PATH LAYOUT CHANGED 2026-09-10, deviates from design doc Sec.9's "opaque GUID" line - directed
/// by the product owner.] The blob now lands at
/// <c>&lt;tenantId&gt;/&lt;reportType-slug&gt;/&lt;yyyy&gt;/&lt;mm&gt;/&lt;reportId&gt;.html.enc</c>
/// instead of a flat <c>{guid}.dat</c> at the container root, so that:
///   - a lifecycle-management policy can match the <c>yyyy/mm</c> prefix (tier-to-Cool, 24-month
///     auto-purge per design doc Sec.9.4) without a SQL scan first;
///   - "delete everything for tenant X" (Sec.9.4 offboarding purge) is one prefix delete;
///   - the container is browsable/auditable per tenant in the portal.
/// The path carries NO PII: an integer tenant id, a fixed report-type slug, a random GUID - never a
/// tenant name, a user identifier, or any report content. Blob index tags (tenantId / reportType /
/// generatedAt) are set too, so ops can FindBlobsByTags without listing. The GeneratedReport SQL row
/// is still the authoritative index - this path is built FROM the row's values, never parsed back.
/// </summary>
public sealed partial class AzureReportBlobWriter(string storageConnectionString, string containerName) : IReportBlobWriter, IReportBlobReader
{
    /// <summary>Item 14's read half. Downloads the still-encrypted bytes exactly as WriteAsync left them - IV-prepended ciphertext, no decryption here.</summary>
    public async Task<byte[]> ReadAsync(BlobLocation location, CancellationToken cancellationToken = default)
    {
        var service = new BlobServiceClient(storageConnectionString);
        var blob = service.GetBlobContainerClient(location.Container).GetBlobClient(location.Path);
        var downloaded = await blob.DownloadContentAsync(cancellationToken);
        return downloaded.Value.Content.ToArray();
    }

    public async Task<BlobLocation> WriteAsync(EncryptedReportEnvelope envelope, BlobPathContext pathContext, CancellationToken cancellationToken = default)
    {
        var service = new BlobServiceClient(storageConnectionString);
        var container = service.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobPath = BuildBlobPath(pathContext);
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

        // Blob index tags - queryable via FindBlobsByTags without a container LIST, and usable as a
        // lifecycle-policy filter. Same non-PII values the path already carries; a tag search never
        // returns content, only the blob name.
        var tags = new Dictionary<string, string>
        {
            ["tenantId"] = pathContext.TenantId.ToString(CultureInfo.InvariantCulture),
            ["reportType"] = Slug(pathContext.ReportType),
            ["generatedAt"] = pathContext.GeneratedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        try
        {
            await blob.SetTagsAsync(tags, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException)
        {
            // Index tags are a nice-to-have for ops queries, not a correctness requirement - some
            // storage accounts / emulators do not support them. The blob + its metadata are already
            // written; never fail a persist over a tag call.
        }

        return new BlobLocation(containerName, blobPath);
    }

    /// <summary>
    /// <c>&lt;tenantId&gt;/&lt;reportType-slug&gt;/&lt;yyyy&gt;/&lt;MM&gt;/&lt;reportId:N&gt;.html.enc</c>.
    /// Pure - no I/O - so it is unit-testable on its own. yyyy/MM come from the report's own
    /// GeneratedAtUtc (UTC), zero-padded, so a lifecycle policy can match a whole month by prefix.
    /// </summary>
    internal static string BuildBlobPath(BlobPathContext ctx)
    {
        var when = ctx.GeneratedAtUtc.ToUniversalTime();
        return string.Create(CultureInfo.InvariantCulture,
            $"{ctx.TenantId}/{Slug(ctx.ReportType)}/{when:yyyy}/{when:MM}/{ctx.ReportId:N}.html.enc");
    }

    /// <summary>
    /// A path/tag-safe slug for a report-type string: lowercase, every run of non
    /// [a-z0-9] collapsed to a single '_', trimmed. "compliance_health" stays as-is;
    /// "dimension_selection:Licence" -> "dimension_selection_licence". Empty/degenerate input
    /// falls back to "report" so a path segment is never blank.
    /// </summary>
    internal static string Slug(string reportType)
    {
        if (string.IsNullOrWhiteSpace(reportType))
            return "report";

        var lowered = reportType.Trim().ToLowerInvariant();
        var slug = NonSlugChars().Replace(lowered, "_").Trim('_');
        return slug.Length == 0 ? "report" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();
}
