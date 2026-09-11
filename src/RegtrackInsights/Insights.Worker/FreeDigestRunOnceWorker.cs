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
/// Enqueues the SAME orchestrations the scheduler enqueues - there is no second code path. This
/// exists only because the scheduler fires on its own day/hour, and waiting until Monday 9am IST
/// to test tenant 23's send is not a workflow.
///
/// Does nothing unless FreeDigest:RunOnce=true, so a bare `dotnet run` starts an idle host:
///   dotnet run -- --FreeDigest:RunOnce=true --FreeDigest:CustomerId=23 --FreeDigest:Phase=generate
///   dotnet run -- --FreeDigest:RunOnce=true --FreeDigest:CustomerId=23 --FreeDigest:Phase=send
///
/// [ADDED - ADR-0001, 2026-09-10] --FreeDigest:Phase selects which half of the two-phase digest to
/// run: "generate" (default) enqueues FreeDigestGenerateOrchestrator, "send" enqueues
/// FreeDigestSendOrchestrator.
/// </summary>
public sealed class FreeDigestRunOnceWorker(
    IServiceProvider services,
    IConfiguration configuration,
    FreeDigestSettings settings,
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

            var phase = configuration["FreeDigest:Phase"] ?? "generate";

            /*  [ADDED, testing only] --FreeDigest:AsOf lets a manual GENERATE run target a week
                that has not already claimed this tenant's recipients (dbo.InsightsFreeDigestLog is
                shared across legacy/generate/send - a recipient already sent to this week via ANY
                path correctly blocks a re-generate for the SAME week). SEND ignores this - it is
                driven entirely by which artifact rows exist, never by a caller-supplied clock.    */
            var asOf = configuration["FreeDigest:AsOf"];

            var client = scope.ServiceProvider.GetRequiredService<TaskHubClient>();

            /*  Keyed on (phase, tenant, week), exactly as the scheduler keys it. Running this
                twice in one week attaches to - or is refused by - the existing instance rather
                than starting a parallel one, the same one-active-run-per-key rule 4.5 states for
                paid.                                                                            */
            var weekEnding = DigestWeek.EndingFor(string.IsNullOrWhiteSpace(asOf) ? DateTime.UtcNow : DateTime.Parse(asOf)).ToString("yyyy-MM-dd");

            OrchestrationInstance instance;
            string instanceId;

            switch (phase.ToLowerInvariant())
            {
                case "generate":
                    instanceId = $"freedigest-gen-{tenantId}-{weekEnding}";
                    logger.LogInformation("Enqueuing FreeDigestGenerateOrchestrator for tenant {TenantId} as {InstanceId}.", tenantId, instanceId);
                    instance = await client.CreateOrchestrationInstanceAsync(
                        FreeDigestGenerateOrchestrator.Name, FreeDigestGenerateOrchestrator.Version, instanceId,
                        new FreeDigestGenerateOrchestrationInput(tenantId, asOf));
                    break;

                case "send":
                    instanceId = $"freedigest-send-{tenantId}-{weekEnding}";
                    logger.LogInformation("Enqueuing FreeDigestSendOrchestrator for tenant {TenantId} as {InstanceId}.", tenantId, instanceId);
                    instance = await client.CreateOrchestrationInstanceAsync(
                        FreeDigestSendOrchestrator.Name, FreeDigestSendOrchestrator.Version, instanceId,
                        new FreeDigestSendOrchestrationInput(tenantId, settings.ArtifactFreshnessDays));
                    break;

                default:
                    logger.LogError("Unknown FreeDigest:Phase '{Phase}'. Expected generate or send.", phase);
                    Environment.ExitCode = 1;
                    return;
            }

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
