using DurableTask.Core;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record PersistInput(
    string Html, int TenantId, string ReportType, string Period, string ScopeDescriptor, int UserId);

public sealed record PersistOutput(string ReportId);

/// <summary>
/// Node 12: build order item 14's write path. Encrypt -> blob -> SQL index row, replacing
/// PersistStubActivity. Read path (view-time re-auth, short-lived SAS, API_CONTRACTS.md Sec.5) is
/// NOT part of this - a report persisted here has nothing yet that can fetch it back out.
/// </summary>
public sealed class PersistActivity(
    IReportEncryptor encryptor, IReportBlobWriter blobWriter, InsightsReportsDbContext db)
    : AsyncTaskActivity<PersistInput, PersistOutput>
{
    protected override Task<PersistOutput> ExecuteAsync(TaskContext context, PersistInput input) => RunAsync(input);

    internal async Task<PersistOutput> RunAsync(PersistInput input)
    {
        var envelope = await encryptor.EncryptAsync(input.Html);
        var location = await blobWriter.WriteAsync(envelope);

        var report = new GeneratedReport
        {
            CustomerId = input.TenantId,
            ScopeDescriptor = input.ScopeDescriptor,
            ReportType = input.ReportType,
            Period = input.Period,
            GeneratedByUserId = input.UserId,
            BlobContainer = location.Container,
            BlobPath = location.Path,
            Status = "complete",
            EncryptedAesKey = envelope.EncryptedAesKey,
            KeyVaultObjectName = envelope.KeyVaultObjectName,
            KeyVaultObjectVersion = envelope.KeyVaultObjectVersion,
        };

        db.GeneratedReports.Add(report);
        await db.SaveChangesAsync();

        return new PersistOutput(report.Id.ToString());
    }
}
