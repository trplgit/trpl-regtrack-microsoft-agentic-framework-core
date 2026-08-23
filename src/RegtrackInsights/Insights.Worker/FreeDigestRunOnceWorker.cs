using DurableTask.Core;
using Insights.Domain;
using Insights.Worker.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// On-demand runner for one tenant's digest, mirroring InsightsRunOnceWorker's shape.
///
/// Enqueues the SAME orchestration the scheduler enqueues on a tenant's anchor day - there is no
/// second code path. This exists only because the scheduler fires on the tenant's own day, and
/// waiting until Tuesday to test tenant 23 is not a workflow.
///
/// Does nothing unless FreeDigest:RunOnce=true, so a bare `dotnet run` starts an idle host:
///   dotnet run -- --FreeDigest:RunOnce=true --FreeDigest:CustomerId=23
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
                "FreeDigest:RunOnce is not set - host is idle. Pass --FreeDigest:RunOnce=true --FreeDigest:CustomerId=<id> to run one tenant's digest.");
            return;
        }

        using var scope = services.CreateScope();

        try
        {
            var customerId = configuration.GetValue<int?>("FreeDigest:CustomerId");
            if (customerId is not { } tenantId)
            {
                logger.LogError("FreeDigest:CustomerId is required - the digest orchestration runs one tenant at a time.");
                Environment.ExitCode = 1;
                return;
            }

            var client = scope.ServiceProvider.GetRequiredService<TaskHubClient>();

            /*  Keyed on (tenant, week), exactly as the scheduler keys it. Running this twice in one
                week attaches to - or is refused by - the existing instance rather than starting a
                parallel one, which is the same one-active-run-per-key rule 4.5 states for paid.  */
            var weekEnding = DigestWeek.EndingFor(DateTime.UtcNow).ToString("yyyy-MM-dd");
            var instanceId = $"freedigest-{tenantId}-{weekEnding}";

            logger.LogInformation("Enqueuing FreeDigestOrchestrator for tenant {TenantId} as {InstanceId}.", tenantId, instanceId);

            var instance = await client.CreateOrchestrationInstanceAsync(
                FreeDigestOrchestrator.Name, FreeDigestOrchestrator.Version, instanceId,
                new FreeDigestOrchestrationInput(tenantId, null));

            var state = await client.WaitForOrchestrationAsync(instance, TimeSpan.FromMinutes(30), stoppingToken);

            logger.LogInformation("Status: {Status}", state.OrchestrationStatus);

            if (state.OrchestrationStatus == OrchestrationStatus.Completed)
            {
                logger.LogInformation("Output: {Output}", state.Output);
            }
            else
            {
                logger.LogError("Run did not complete. Detail: {Output}", state.Output);
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /*  A refusal - dictionary gap, missing scope, bad connection - must exit LOUD and
                non-zero, not scroll past in a log nobody reads.                                 */
            logger.LogError(ex, "Free digest run failed.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }
}
