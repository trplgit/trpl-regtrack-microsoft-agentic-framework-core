using DurableTask.Core;
using Insights.Data;
using Insights.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record PersistInput(
    string Html, int TenantId, string ReportType, string Period, string ScopeDescriptor, int UserId);

/// <param name="LocalFilePath">
/// [ADDED 2026-09-12] Set only when Reports:LocalFallbackDirectory is configured - see
/// PersistActivity's own doc comment. Null on every normal (encrypt/blob/SQL) persist.
/// </param>
public sealed record PersistOutput(string ReportId, string? LocalFilePath = null);

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
///
/// [ADDED 2026-09-12, TEMPORARY] localFallbackDirectory - a stopgap for a real, ongoing Key Vault
/// access failure (KeyVaultErrorException "Forbidden", confirmed live via ProbeKeyVaultEncryptionAsync
/// with VPN both on and off - not an IP/network issue, a real RBAC/access-policy denial on the
/// shared DocAI vault). Every dimension of a 7-dimension Minda batch reached this exact step after
/// narrate+render had ALREADY billed real tokens, then lost the entire output at the last line.
/// When set (Reports:LocalFallbackDirectory), this activity skips encrypt/blob/SQL entirely and
/// writes plaintext HTML straight to disk - deliberately NOT wired into GeneratedReport or the
/// /content API, since a report that bypassed real encryption has no business posing as one that
/// went through the normal pipeline. Revert by clearing that config key once Key Vault access is
/// fixed - do not leave this on longer than the outage that justified it.
/// </summary>
public sealed class PersistActivity(
    IReportEncryptor encryptor, IReportBlobWriter blobWriter, IServiceScopeFactory serviceScopeFactory,
    ILogger<PersistActivity> logger, string? localFallbackDirectory = null)
    : AsyncTaskActivity<PersistInput, PersistOutput>
{
    protected override Task<PersistOutput> ExecuteAsync(TaskContext context, PersistInput input) => RunAsync(input);

    internal async Task<PersistOutput> RunAsync(PersistInput input)
    {
        // The report id and its generation time are fixed HERE, before the blob write, because the
        // blob PATH is built from them (<tenantId>/<reportType>/<yyyy>/<mm>/<reportId>.html.enc).
        // EF's NEWID() / SYSUTCDATETIME() column defaults yield to a client-set value, so the SQL
        // row carries the exact same id and timestamp the blob path encodes.
        var reportId = Guid.NewGuid();
        var generatedAtUtc = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(localFallbackDirectory))
        {
            Directory.CreateDirectory(localFallbackDirectory);
            var safePeriod = string.Join("_", input.Period.Split(Path.GetInvalidFileNameChars()));
            var localPath = Path.Combine(localFallbackDirectory, $"{input.TenantId}-{input.ReportType}-{safePeriod}-{reportId}.html");
            await File.WriteAllTextAsync(localPath, input.Html);
            return new PersistOutput(reportId.ToString(), LocalFilePath: localPath);
        }

        try
        {
            var envelope = await encryptor.EncryptAsync(input.Html);
            var location = await blobWriter.WriteAsync(
                envelope, new BlobPathContext(input.TenantId, input.ReportType, DateOnly.FromDateTime(generatedAtUtc), reportId));

            var report = new GeneratedReport
            {
                Id = reportId,
                CustomerId = input.TenantId,
                ScopeDescriptor = input.ScopeDescriptor,
                ReportType = input.ReportType,
                Period = input.Period,
                GeneratedAtUtc = generatedAtUtc,
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
        catch (Exception ex)
        {
            // [ADDED 2026-09-18] Found live: DTFx's TaskFailed history event only ever persists
            // ex.Message, never ex.InnerException - "An error occurred while saving the entity
            // changes. See the inner exception for details." is EF's own DbUpdateException.Message,
            // and the actual inner exception (the real SQL error - deadlock, timeout, whatever it
            // turns out to be) was completely unrecoverable after the fact. Logging the FULL
            // exception (ILogger's Exception overload captures ex.ToString(), inner exceptions
            // included) here, before this still propagates and fails the activity exactly as
            // before, is the only way this is diagnosable without bypassing the app next time.
            logger.LogError(ex,
                "PersistActivity failed for tenant {CustomerId}, reportType {ReportType}, period {Period}, reportId {ReportId}.",
                input.TenantId, input.ReportType, input.Period, reportId);
            throw;
        }
    }
}
