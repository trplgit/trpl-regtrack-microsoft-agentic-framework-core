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
            var instance = await client.CreateOrchestrationInstanceAsync(
                InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, instanceId: null, input);

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
}
