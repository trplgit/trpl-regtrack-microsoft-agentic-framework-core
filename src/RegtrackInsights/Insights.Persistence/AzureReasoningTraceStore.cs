using System.Globalization;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Insights.Data;
using Insights.Domain;

namespace Insights.Persistence;

/// <inheritdoc cref="IReasoningTraceStore"/>
public sealed class AzureReasoningTraceStore(string storageConnectionString, string containerName) : IReasoningTraceStore
{
    public async Task<BlobLocation> WriteAsync(string markdown, BlobPathContext pathContext, CancellationToken cancellationToken = default)
    {
        var service = new BlobServiceClient(storageConnectionString);
        var container = service.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobPath = BuildBlobPath(pathContext);
        var blob = container.GetBlobClient(blobPath);

        using var contentStream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        await blob.UploadAsync(
            contentStream,
            // [2026-09-29] Plain text since prompt 08 v3. The blob path keeps its old "-reasoning.md" leaf
            // on purpose - every report generated before today is found at that same path.
            new BlobHttpHeaders { ContentType = "text/plain; charset=utf-8" },
            cancellationToken: cancellationToken);

        // Same non-PII tag shape as AzureReportBlobWriter's own report blob - a lifecycle policy or
        // ops query can find both the report and its trace by the same tenantId/reportType filter.
        var tags = new Dictionary<string, string>
        {
            ["tenantId"] = pathContext.TenantId.ToString(CultureInfo.InvariantCulture),
            ["reportType"] = AzureReportBlobWriter.Slug(pathContext.ReportType),
            ["partitionDate"] = pathContext.PartitionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["artifactKind"] = "reasoning_trace",
        };
        try
        {
            await blob.SetTagsAsync(tags, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException)
        {
            // Same "nice-to-have, never fail the write over it" stance AzureReportBlobWriter already takes.
        }

        return new BlobLocation(containerName, blobPath);
    }

    public async Task<string?> ReadIfExistsAsync(BlobLocation location, CancellationToken cancellationToken = default)
    {
        var service = new BlobServiceClient(storageConnectionString);
        var blob = service.GetBlobContainerClient(location.Container).GetBlobClient(location.Path);

        if (!await blob.ExistsAsync(cancellationToken))
            return null;

        var downloaded = await blob.DownloadContentAsync(cancellationToken: cancellationToken);
        return downloaded.Value.Content.ToString();
    }

    /// <summary>
    /// Sibling of AzureReportBlobWriter.BuildBlobPath - SAME tenantId/reportType-slug/yyyy/mm
    /// segments (so both artifacts sit in the same "folder" and a prefix-delete purges both
    /// together), different leaf: "-reasoning.md" instead of ".html.enc". Pure - no I/O.
    /// </summary>
    internal static string BuildBlobPath(BlobPathContext ctx) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{ctx.TenantId}/{AzureReportBlobWriter.Slug(ctx.ReportType)}/{ctx.PartitionDate:yyyy}/{ctx.PartitionDate:MM}/{ctx.ReportId:N}-reasoning.md");
}
