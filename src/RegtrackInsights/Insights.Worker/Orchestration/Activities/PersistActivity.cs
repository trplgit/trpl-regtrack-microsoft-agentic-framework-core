using DurableTask.Core;
using Insights.Data;
using Insights.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Insights.Worker.Orchestration.Activities;

public sealed record PersistInput(
    string Html, int TenantId, string ReportType, string Period, string ScopeDescriptor, int UserId);

public sealed record PersistOutput(string ReportId);

/// <summary>
/// Node 12: build order item 14's write path. Encrypt -> blob -> SQL index row, replacing
/// PersistStubActivity. Read path (view-time re-auth, short-lived SAS, API_CONTRACTS.md Sec.5) is
/// NOT part of this - a report persisted here has nothing yet that can fetch it back out.
///
/// [BUG FOUND LIVE, 2026-09-07] Used to constructor-inject InsightsReportsDbContext directly, the
/// same way every other activity injects its (thin, stateless, connection-per-call) repository -
/// see WorkerRegistration.cs's ActivityCreator&lt;T&gt; doc comment: it resolves each activity from
/// a fresh DI scope and disposes that scope immediately once the activity instance is constructed,
/// on the documented assumption that "every current repository... opens its own connection per
/// call, so nothing is lost by not keeping the scope alive." That assumption is true for every
/// activity except this one: InsightsReportsDbContext is a real, stateful EF Core context (AddDbContext,
/// scoped), not a thin wrapper - it becomes unusable the instant its owning scope disposes, which
/// happens at CONSTRUCTION time, before this activity's own async RunAsync ever gets to call
/// SaveChangesAsync. Confirmed live: every other stage of a real tenant-29 run completed (7 of 7
/// stages, including the LLM/rendering pipeline) and only this final write step threw "Cannot access
/// a disposed context instance... Object name: 'InsightsReportsDbContext'." Fixed by injecting
/// IServiceScopeFactory instead and creating/disposing the DbContext's OWN scope INSIDE RunAsync,
/// where it is actually used - matching every other activity's "one connection per call" pattern
/// instead of trying to make PersistActivity fit ActivityCreator's construction-time-only scope.
/// </summary>
public sealed class PersistActivity(
    IReportEncryptor encryptor, IReportBlobWriter blobWriter, IServiceScopeFactory serviceScopeFactory)
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

        using var scope = serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InsightsReportsDbContext>();
        db.GeneratedReports.Add(report);
        await db.SaveChangesAsync();

        return new PersistOutput(report.Id.ToString());
    }
}
