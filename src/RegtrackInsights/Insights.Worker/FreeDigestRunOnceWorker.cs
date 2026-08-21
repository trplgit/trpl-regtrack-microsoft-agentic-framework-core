using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// Runs the free digest ONCE from the host and then stops it, so the real DI-composed code path
/// can be exercised without an xUnit runner.
///
/// This is NOT the weekly scheduler (Phase 1c step 9, still to build). It is an on-demand runner:
/// nothing happens unless FreeDigest:RunOnce is true, so an ordinary `dotnet run` still starts an
/// idle host rather than mailing anybody.
///
///   dotnet run -- --FreeDigest:RunOnce=true --FreeDigest:CustomerId=1403   one tenant
///   dotnet run -- --FreeDigest:RunOnce=true                                every entitled tenant
///
/// Command-line switches are read by the host's own configuration provider, so they override
/// appsettings without editing it.
/// </summary>
public sealed class FreeDigestRunOnceWorker(
    IServiceProvider services,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<FreeDigestRunOnceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("FreeDigest:RunOnce", false))
        {
            logger.LogInformation(
                "FreeDigest:RunOnce is not set - host is idle. Pass --FreeDigest:RunOnce=true to run the digest once.");
            return;
        }

        /*  IFreeDigestService is SCOPED (its repositories are), and a BackgroundService is a
            singleton - resolving it straight from the root provider would throw under
            ValidateScopes, and silently pin one scope's state without it. Create a scope.      */
        using var scope = services.CreateScope();
        var digest = scope.ServiceProvider.GetRequiredService<IFreeDigestService>();

        try
        {
            var customerId = configuration.GetValue<int?>("FreeDigest:CustomerId");

            if (customerId is { } id)
            {
                logger.LogInformation("Running the free digest once for tenant {CustomerId}.", id);
                var result = await digest.RunForTenantAsync(id, cancellationToken: stoppingToken);
                LogTenant(result);
            }
            else
            {
                logger.LogInformation("Running the free digest once for EVERY entitled tenant.");
                var batch = await digest.RunWeeklyAsync(cancellationToken: stoppingToken);

                foreach (var tenant in batch.Tenants)
                    LogTenant(tenant);

                logger.LogInformation(
                    "Batch complete. Tenants {Processed}, skipped {Skipped}, emails sent {Sent}, recipients skipped {RecipientsSkipped}.",
                    batch.TenantsProcessed, batch.TenantsSkipped, batch.EmailsSent, batch.RecipientsSkipped);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /*  A refusal - dictionary gap, missing scope, bad connection - must exit LOUD and
                non-zero, not scroll past in a log nobody reads.                                */
            logger.LogError(ex, "Free digest run failed.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    private void LogTenant(Insights.Domain.FreeDigestTenantResult result)
    {
        logger.LogInformation(
            "Tenant {CustomerId} ({TenantName}): {Decision} - {Reason}. Sent {Sent}, skipped {Skipped}.",
            result.CustomerId, result.TenantName, result.Decision, result.Reason, result.SentCount, result.SkippedCount);

        foreach (var r in result.Recipients)
        {
            logger.LogInformation(
                "  user {UserId} -> {Email}: sent={Sent} source={Source} provider={Provider} reason={Reason}",
                r.UserId, r.Email, r.Sent, r.Source, r.ProviderUsed, r.Reason ?? "-");
        }
    }
}
