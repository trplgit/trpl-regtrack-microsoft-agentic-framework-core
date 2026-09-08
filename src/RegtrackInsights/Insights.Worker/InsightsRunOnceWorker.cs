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

            // [ADDED 2026-09-08] --Insights:Dimensions=Location,Nature,Act - only meaningful when
            // ReportType=dimension_selection (Insights.Domain.DimensionSelectionComposition). Comma-
            // separated, trimmed, empty entries dropped; null (not empty) when the flag is absent at
            // all, matching InsightsReportOrchestrationInput.RequestedDimensions' own "null means
            // every other ReportType's existing behaviour, unchanged" contract.
            var requestedDimensions = configuration["Insights:Dimensions"] is { Length: > 0 } dimensionsCsv
                ? dimensionsCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : null;

            var input = new InsightsReportOrchestrationInput(
                tenantId, reportType, new InsightsScopeRequest("tenant", null), period, userId,
                RequestedDimensions: requestedDimensions);

            logger.LogInformation("Starting InsightsReportOrchestrator for tenant {TenantId}, user {UserId}.", tenantId, userId);
            /*  The instance id is DERIVED from (tenant, scope, type, period), not left to DTFx.
                Two reasons, both load-bearing:

                  - it IS the one-active-run-per-key lock (spec Sec.4.5) - a second enqueue for
                    the same key attaches to the running instance instead of starting a duplicate;
                  - it carries the tenant, which is the only way
                    GET /api/insights/runs/{runId}/stream can re-derive eligibility, since that
                    URL has no tenantId. With a random GUID the endpoint refuses its own runs.  */
            var instanceId = InsightsRunId.For(tenantId, input.Scope.ToDescriptor(), reportType, period);

            var instance = await client.CreateOrchestrationInstanceAsync(
                InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, instanceId, input);

            logger.LogInformation("Instance {InstanceId} started. Waiting for completion.", instance.InstanceId);
            // [FIX] A single WaitForOrchestrationAsync(..., 5 minutes) used to sit here. On timeout
            // it throws, the finally below still runs, and lifetime.StopApplication() tears down
            // this ENTIRE host - including its own TaskHubWorker dequeue loop - stranding the
            // orchestration mid-run in SQL with nothing left to service it. A closed/killed CLI
            // orphaned the run exactly this way (confirmed live). Poll instead, matching
            // InsightsReportOrchestratorManualRunTests.PollUntilTerminalAsync's shape: a long
            // budget and tolerance for a transient SqlException on any single poll, since the
            // orchestration itself is durable against exactly that blip and the client watching it
            // should be too.
            var state = await PollUntilTerminalAsync(client, instance.InstanceId, TimeSpan.FromMinutes(30), stoppingToken);

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
    /// Polls for a terminal state rather than one blocking WaitForOrchestrationAsync call, so a
    /// single transient SqlException (connection blip against the task-hub DB) doesn't abort the
    /// whole wait - and doesn't StopApplication() this host, which would kill its own dequeue loop
    /// mid-run. Mirrors InsightsReportOrchestratorManualRunTests.PollUntilTerminalAsync exactly.
    /// </summary>
    private static async Task<OrchestrationState> PollUntilTerminalAsync(
        TaskHubClient client, string instanceId, TimeSpan budget, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(budget);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var state = await client.GetOrchestrationStateAsync(instanceId);
                if (state is not null && state.OrchestrationStatus is OrchestrationStatus.Completed or OrchestrationStatus.Failed)
                    return state;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Transient SqlException on a status poll - the orchestration keeps progressing
                // underneath regardless; retry rather than abort the wait on one hiccup.
            }
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
        }
        throw new TimeoutException($"Instance {instanceId} did not reach a terminal state within {budget}.");
    }
}
