using Insights.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// DIAGNOSTIC ONLY - not part of the pipeline, never runs unless asked. Fetches and decrypts
/// every COMPLETE, undispatched artifact a tenant has on record and writes each to a local
/// .html file, so a generated digest can be inspected (or handed to someone) without going
/// through email.
///
/// Reuses IFreeDigestArtifactRepository/IDigestArtifactStore exactly as
/// ResolveDigestDispatchActivity/FetchDigestArtifactActivity do - same Key Vault decrypt path
/// production uses (see Insights.Persistence.AdalKeyVaultReportEncryptor), nothing hand-rolled.
/// Read-only: no claim taken, no send, no state change.
///
/// Does nothing unless FreeDigest:DumpOnce=true:
///   dotnet run -- --FreeDigest:DumpOnce=true --FreeDigest:CustomerId=29
/// </summary>
public sealed class FreeDigestArtifactDumpWorker(
    IServiceProvider services,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<FreeDigestArtifactDumpWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("FreeDigest:DumpOnce", false))
        {
            logger.LogInformation(
                "FreeDigest:DumpOnce is not set - host is idle. Pass --FreeDigest:DumpOnce=true --FreeDigest:CustomerId=<id> to dump that tenant's stored artifacts.");
            return;
        }

        using var scope = services.CreateScope();

        try
        {
            var customerId = configuration.GetValue<int?>("FreeDigest:CustomerId");
            if (customerId is not { } tenantId)
            {
                logger.LogError("FreeDigest:CustomerId is required.");
                Environment.ExitCode = 1;
                return;
            }

            // Wide by default - this is a diagnostic read, not the SEND-phase freshness gate
            // (sql/29's "a stale digest is a wrong digest" rule only applies to what gets mailed).
            var maxAgeDays = configuration.GetValue("FreeDigest:DumpMaxAgeDays", 30);
            var outputDirectory = configuration["FreeDigest:DumpOutputDirectory"] ?? Directory.GetCurrentDirectory();

            var artifactRepository = scope.ServiceProvider.GetRequiredService<IFreeDigestArtifactRepository>();
            var artifactStore = scope.ServiceProvider.GetRequiredService<IDigestArtifactStore>();

            var artifacts = await artifactRepository.GetForDispatchAsync(tenantId, maxAgeDays, stoppingToken);

            if (artifacts.Count == 0)
            {
                logger.LogInformation(
                    "No complete, undispatched artifact found for tenant {TenantId} within {MaxAgeDays} day(s). " +
                    "Run --FreeDigest:Phase=generate first.", tenantId, maxAgeDays);
                return;
            }

            Directory.CreateDirectory(outputDirectory);

            foreach (var artifact in artifacts)
            {
                var html = await artifactStore.ReadAsync(artifact, stoppingToken);

                var fileName = $"digest-{artifact.CustomerId}-{artifact.WeekEnding:yyyy-MM-dd}-{artifact.ArtifactId}.html";
                var path = Path.Combine(outputDirectory, fileName);
                await File.WriteAllTextAsync(path, html, stoppingToken);

                logger.LogInformation(
                    "Wrote {Path} (week ending {WeekEnding}, source {Source}, scope {ScopeSignature}, blob {BlobContainer}/{BlobPath}). " +
                    "NOTE: the unsubscribe link is still the sentinel placeholder - it is only substituted per-recipient at send time.",
                    path, artifact.WeekEnding, artifact.Source, artifact.ScopeSignature, artifact.BlobContainer, artifact.BlobPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Artifact dump failed.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
