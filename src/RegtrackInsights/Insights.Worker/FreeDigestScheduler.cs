using Insights.Domain;
using DurableTask.Core;
using DurableTask.Core.Exceptions;
using Insights.Worker.Orchestration;
﻿using Insights.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// The free digest schedule - ADR-0001 (2026-09-10): every entitled tenant GENERATES on
/// FreeDigestSettings.GenerateDay (default Sunday) and SENDS on FreeDigestSettings.SendDay at
/// FreeDigestSettings.SendHourLocal in FreeDigestSettings.ScheduleTimeZone (default Monday, 9am
/// IST) - a deliberate product decision to move away from the legacy per-tenant staggered anchor
/// day (hash(tenant_id) % 7). See ADR-0001 D10 for the tradeoff this accepts: Sunday's compose
/// load and Monday's mail volume both concentrate onto one day instead of spreading across the
/// week.
///
/// Off unless FreeDigest:Schedule:Enabled is true, so a worker started for any other reason does
/// not begin mailing customers.
///
/// SAFE TO TICK OFTEN. Neither phase tracks what it has already done in memory: the GENERATE side
/// is guarded by the artifact-slot claim (sql/29), the SEND side by the per-recipient claim
/// (sql/15). A restart, an overlapping tick, or two instances running at once all converge on the
/// same outcome - one artifact per scope group per week, one email per recipient per week.
/// </summary>
public sealed class FreeDigestScheduler(
    IServiceProvider services,
    FreeDigestSettings settings,
    ILogger<FreeDigestScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.ScheduleEnabled)
        {
            logger.LogInformation(
                "Free digest schedule is disabled. Set FreeDigest:Schedule:Enabled=true to run the weekly lane.");
            return;
        }

        logger.LogInformation(
            "Free digest schedule enabled. Checking every {Interval} - generate on {GenerateDay}, send from {SendHour:00}:00 {TimeZone} on {SendDay}.",
            settings.ScheduleCheckInterval, settings.GenerateDay, settings.SendHourLocal, settings.ScheduleTimeZone.Id, settings.SendDay);

        using var timer = new PeriodicTimer(settings.ScheduleCheckInterval);

        do
        {
            try
            {
                var utcNow = DateTime.UtcNow;
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, settings.ScheduleTimeZone);

                if (localNow.DayOfWeek == settings.GenerateDay)
                    await RunDueTenantsAsync(GeneratePhase, utcNow, stoppingToken);

                if (localNow.DayOfWeek == settings.SendDay && localNow.Hour >= settings.SendHourLocal)
                    await RunDueTenantsAsync(SendPhase, utcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A bad tick must not kill the lane - the next one may well succeed.
                logger.LogError(ex, "Free digest schedule tick failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunDueTenantsAsync(Func<TaskHubClient, FreeDigestTenant, DateTime, CancellationToken, Task> phase, DateTime utcNow, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFreeDigestRepository>();
        var taskHubClient = scope.ServiceProvider.GetRequiredService<TaskHubClient>();

        var tenants = await repository.GetEntitledTenantsAsync(cancellationToken);
        if (tenants.Count == 0)
            return;

        logger.LogInformation("Free digest lane: {TotalCount} entitled tenant(s) due for {Phase}.", tenants.Count, phase.Method.Name);

        foreach (var tenant in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await phase(taskHubClient, tenant, utcNow, cancellationToken);
            }
            catch (OrchestrationAlreadyExistsException)
            {
                // Already running or already run for this week - exactly what the keyed id is for.
                logger.LogInformation("Tenant {CustomerId}: already enqueued for this week; skipping.", tenant.CustomerId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Free digest could not be enqueued for tenant {CustomerId}; continuing.", tenant.CustomerId);
            }

            /*  LOWEST-PRIORITY LANE (10.3). A deliberate pause between enqueues so a 600-tenant
                batch does not arrive as one burst. The durable queue is what actually bounds
                execution; this just spreads the arrival.                                        */
            await Task.Delay(settings.SchedulePerTenantDelay, cancellationToken);
        }
    }

    /*  ENQUEUE, DO NOT RUN INLINE (design doc 10.2, 4.4). The scheduler's job is to decide WHICH
        tenants are due; the durable queue decides when they actually execute. The instance id is
        keyed on (phase, tenant, week) - a second tick on the same day attaches to the existing run
        rather than starting a parallel one (the same "one active run per key" rule 4.5 states for
        paid).                                                                                    */
    private async Task GeneratePhase(TaskHubClient client, FreeDigestTenant tenant, DateTime utcNow, CancellationToken cancellationToken)
    {
        var weekEnding = DigestWeek.EndingFor(utcNow).ToString("yyyy-MM-dd");
        var instanceId = $"freedigest-gen-{tenant.CustomerId}-{weekEnding}";

        await client.CreateOrchestrationInstanceAsync(
            FreeDigestGenerateOrchestrator.Name, FreeDigestGenerateOrchestrator.Version, instanceId,
            new FreeDigestGenerateOrchestrationInput(tenant.CustomerId, null));

        logger.LogInformation("Tenant {CustomerId}: digest generation enqueued as {InstanceId}.", tenant.CustomerId, instanceId);
    }

    private async Task SendPhase(TaskHubClient client, FreeDigestTenant tenant, DateTime utcNow, CancellationToken cancellationToken)
    {
        /*  The SEND instance is keyed on the week that CLOSES the current send day - Monday still
            belongs to the week that Sunday's generation labelled with itself (DigestWeek.EndingFor
            treats Sunday as closing its OWN week - ADR-0001 D2), so this must reuse the SAME
            WeekEnding computation the generate phase used, not recompute forward from Monday.     */
        var weekEnding = DigestWeek.EndingFor(utcNow).ToString("yyyy-MM-dd");
        var instanceId = $"freedigest-send-{tenant.CustomerId}-{weekEnding}";

        await client.CreateOrchestrationInstanceAsync(
            FreeDigestSendOrchestrator.Name, FreeDigestSendOrchestrator.Version, instanceId,
            new FreeDigestSendOrchestrationInput(tenant.CustomerId, settings.ArtifactFreshnessDays));

        logger.LogInformation("Tenant {CustomerId}: digest send enqueued as {InstanceId}.", tenant.CustomerId, instanceId);
    }
}

