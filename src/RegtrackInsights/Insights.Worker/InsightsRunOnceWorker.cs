using DurableTask.Core;
using Insights.Domain;
using Insights.Worker.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// On-demand runner for the paid orchestrator, mirroring FreeDigestRunOnceWorker's shape exactly.
/// Does nothing unless Insights:RunOnce=true, so a bare `dotnet run` starts an idle host:
///   dotnet run -- --Insights:RunOnce=true --Insights:TenantId=29 --Insights:UserId=38 --Insights:ReportType=compliance_health
/// This is the only way to start a paid report until build order item 15 (hub UI, not this slice)
/// gives the real API endpoint something to enqueue against - see API_CONTRACTS.md 3.
/// </summary>
public sealed class InsightsRunOnceWorker(
    IServiceProvider services,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<InsightsRunOnceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Insights:RunOnce", false))
        {
            logger.LogInformation(
                "Insights:RunOnce is not set - host is idle. Pass --Insights:RunOnce=true to run one paid report.");
            return;
        }

        using var scope = services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<TaskHubClient>();

        try
        {
            var tenantId = configuration.GetValue<int>("Insights:TenantId");
            var userId = configuration.GetValue<int>("Insights:UserId");
            var reportType = configuration["Insights:ReportType"] ?? "compliance_health";
            var period = configuration["Insights:Period"] ?? "FY2025-26";

            var input = new InsightsReportOrchestrationInput(tenantId, reportType, new InsightsScopeRequest("tenant", null), period, userId);

            logger.LogInformation("Starting InsightsReportOrchestrator for tenant {TenantId}, user {UserId}.", tenantId, userId);
            /*  The instance id is DERIVED from (tenant, scope, type, period), not left to DTFx.
                Two reasons, both load-bearing:

                  - it IS the one-active-run-per-key lock (spec Sec.4.5) - a second enqueue for
                    the same key attaches to the running instance instead of starting a duplicate;
                  - it carries the tenant, which is the only way
                    GET /api/insights/runs/{runId}/stream can re-derive eligibility, since that
                    URL has no tenantId. With a random GUID the endpoint refuses its own runs.  */
            var instanceId = InsightsRunId.For(tenantId, ScopeDescriptorFor(input.Scope), reportType, period);

            var instance = await client.CreateOrchestrationInstanceAsync(
                InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, instanceId, input);

            logger.LogInformation("Instance {InstanceId} started. Waiting for completion.", instance.InstanceId);
            var state = await client.WaitForOrchestrationAsync(instance, TimeSpan.FromMinutes(5), stoppingToken);

            logger.LogInformation("Status: {Status}. Final stage/status: {CustomStatus}.", state.OrchestrationStatus, state.Status);

            if (state.OrchestrationStatus == OrchestrationStatus.Completed)
            {
                logger.LogInformation("Output: {Output}", state.Output);
            }
            else if (state.OrchestrationStatus == OrchestrationStatus.Failed)
            {
                logger.LogError("Run failed. Detail: {Output}", state.Output);
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Insights run failed.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    /// <summary>
    /// The canonical scope descriptor for a request, matching the API contract spelling
    /// ("tenant" or "entity:{id}"). Kept in one place because it is half of the cooldown key -
    /// two spellings of the same scope would become two separate 30-day buckets.
    /// </summary>
    private static string ScopeDescriptorFor(InsightsScopeRequest scope) =>
        scope.EntityId is int entityId ? $"entity:{entityId}" : "tenant";
}
