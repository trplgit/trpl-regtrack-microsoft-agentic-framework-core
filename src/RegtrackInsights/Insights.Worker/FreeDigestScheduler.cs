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
/// FreeDigestSettings.GenerateDay from GenerateHourLocal (default Sunday 00:00) and SENDS on
/// FreeDigestSettings.SendDay from SendHourLocal in FreeDigestSettings.ScheduleTimeZone (default
/// Monday 08:00 IST, so ~5,000 recipients are in inboxes before 9am) - a deliberate product decision to move away from the legacy per-tenant staggered anchor
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
            "Free digest schedule enabled - generate from {GenerateHour:00}:00 on {GenerateDay}, send from {SendHour:00}:00 on {SendDay} ({TimeZone}); re-checks at most every {Interval}.",
            settings.GenerateHourLocal, settings.GenerateDay, settings.SendHourLocal, settings.SendDay, settings.ScheduleTimeZone.Id, settings.ScheduleCheckInterval);

        // Clock-aligned wakes, not a boot-relative PeriodicTimer - see FreeDigestScheduleClock.
        // The first check runs immediately, so a worker that was down at a phase start catches up.
        while (true)
        {
            try
            {
                var utcNow = DateTime.UtcNow;
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, settings.ScheduleTimeZone);

                if (FreeDigestScheduleClock.IsGenerateDue(localNow, settings))
                {
                    await RunDueTenantsAsync(GeneratePhase, utcNow, stoppingToken);

                    // ADR-0002 (2026-09-11) - separately gated: this lane must not enqueue a
                    // single instance while the destination endpoint is unconfigured.
                    if (settings.InsightApiEnabled)
                        await RunDueTenantsAsync(InsightJsonPhase, utcNow, stoppingToken);
                }

                if (FreeDigestScheduleClock.IsSendDue(localNow, settings))
                    await RunDueTenantsAsync(SendPhase, utcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A bad tick must not kill the lane - the next one may well succeed.
                logger.LogError(ex, "Free digest schedule tick failed; will retry on the next wake.");
            }

            // Measured from AFTER the tick: a send phase spends minutes enqueuing tenants.
            var delay = FreeDigestScheduleClock.NextWakeDelay(DateTime.UtcNow, settings);
            logger.LogDebug("Free digest schedule: next check in {Delay}.", delay);
            await Task.Delay(delay, stoppingToken);
        }
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

    /// <summary>ADR-0002 (2026-09-11). Same enqueue-not-run-inline, keyed-instance shape as GeneratePhase - a second tick the same Sunday attaches to the existing run rather than starting a parallel one.</summary>
    private async Task InsightJsonPhase(TaskHubClient client, FreeDigestTenant tenant, DateTime utcNow, CancellationToken cancellationToken)
    {
        var weekEnding = DigestWeek.EndingFor(utcNow).ToString("yyyy-MM-dd");
        var instanceId = $"freedigest-insight-{tenant.CustomerId}-{weekEnding}";

        await client.CreateOrchestrationInstanceAsync(
            FreeDigestInsightJsonOrchestrator.Name, FreeDigestInsightJsonOrchestrator.Version, instanceId,
            new FreeDigestInsightJsonOrchestrationInput(tenant.CustomerId, null));

        logger.LogInformation("Tenant {CustomerId}: insight JSON generation enqueued as {InstanceId}.", tenant.CustomerId, instanceId);
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

