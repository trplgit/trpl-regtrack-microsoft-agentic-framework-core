using DurableTask.Core.Exceptions;
using Insights.Data;
using Insights.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// The paid_batch keep-warm lane (design doc Sec.4.2-4.5): re-runs a (scope, reportType, period)
/// key a paying tenant has both generated and actually VIEWED RECENTLY, staggered by
/// hash(tenantId) % Schedule:PaidAnchorModulo across the month. Enqueues at
/// <see cref="LlmCallPriority.Batch"/> - LlmConcurrencyGate always drains paid_interactive first,
/// so a burst of Generate clicks never waits behind this lane, only the reverse.
///
/// Off unless Schedule:PaidKeepWarm:Enabled is true, so a worker started for any other reason
/// does not begin quietly regenerating (and re-billing tokens for) every paying tenant's reports.
///
/// SAFE TO TICK OFTEN, same reasoning as FreeDigestScheduler: the derived instance id
/// (InsightsRunId.For, keyed on tenant/scope/reportType/period) IS the one-active-run-per-key
/// lock (Sec.4.5) - a duplicate tick attaches to the running instance rather than starting a
/// second one. Confirmed against DurableTask.SqlServer's CreateInstance stored procedure: the
/// default @DedupeStatuses ('Pending,Running') blocks only an ACTIVE duplicate; a COMPLETED prior
/// run under the same id is purged and a fresh instance created - which is exactly what a repeat
/// keep-warm cycle for the same key needs to keep working, cycle after cycle.
/// </summary>
public sealed class PaidKeepWarmScheduler(
    IServiceProvider services,
    PaidKeepWarmSettings settings,
    ILogger<PaidKeepWarmScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.ScheduleEnabled)
        {
            logger.LogInformation(
                "Paid keep-warm schedule is disabled. Set Schedule:PaidKeepWarm:Enabled=true to run the batch lane.");
            return;
        }

        logger.LogInformation(
            "Paid keep-warm schedule enabled. Checking every {Interval}, running from {Hour:00}:00 UTC on each tenant's anchor day (modulo {Modulo}).",
            settings.ScheduleCheckInterval, settings.ScheduleRunHourUtc, settings.AnchorModulo);

        using var timer = new PeriodicTimer(settings.ScheduleCheckInterval);

        do
        {
            try
            {
                await RunDueTenantsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A bad tick must not kill the lane - the next one may well succeed.
                logger.LogError(ex, "Paid keep-warm schedule tick failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunDueTenantsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (now.Hour < settings.ScheduleRunHourUtc)
            return;

        using var scope = services.CreateScope();
        var tenantRepository = scope.ServiceProvider.GetRequiredService<IPaidTenantRepository>();
        var db = scope.ServiceProvider.GetRequiredService<InsightsReportsDbContext>();
        var enqueuer = scope.ServiceProvider.GetRequiredService<IInsightsRunEnqueuer>();

        var tenants = await tenantRepository.GetEntitledTenantsAsync(cancellationToken);
        var due = tenants.Where(t => AnchorDayOfMonthFor(t.CustomerId, settings.AnchorModulo) == now.Day).ToList();

        if (due.Count == 0)
            return;

        logger.LogInformation("Paid keep-warm lane: {DueCount} of {TotalCount} tenant(s) anchored to day {Day}.",
            due.Count, tenants.Count, now.Day);

        var viewedSinceUtc = now.AddDays(-settings.KeepWarmWindowDays);
        var cooldownBeforeUtc = now.AddDays(-settings.CooldownDays);

        foreach (var tenant in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<GeneratedReport> candidates;
            try
            {
                candidates = await GetKeepWarmCandidatesAsync(db, tenant.CustomerId, viewedSinceUtc, cooldownBeforeUtc, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Paid keep-warm: candidate lookup failed for tenant {CustomerId}; continuing.", tenant.CustomerId);
                continue;
            }

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    /*  ENQUEUE, DO NOT RUN INLINE (Sec.4.4) - same reasoning as FreeDigestScheduler.
                        The scheduler decides WHICH keys are due; the durable queue and the LLM
                        concurrency gate decide when they actually execute. The re-triggering user
                        is the ORIGINAL generator (GeneratedByUserId), not a system sentinel -
                        GatherScopeActivity re-checks THAT user's current scope/entitlement at run
                        time, so a keep-warm refresh correctly refuses if the person who asked for
                        this report has since lost access, exactly as any other trigger path would. */
                    var scopeRequest = InsightsScopeRequest.Parse(candidate.ScopeDescriptor);
                    var runId = await enqueuer.EnqueueAsync(
                        candidate.CustomerId, candidate.ReportType, scopeRequest, candidate.Period,
                        candidate.GeneratedByUserId, cancellationToken, priority: LlmCallPriority.Batch);

                    logger.LogInformation(
                        "Tenant {CustomerId}: keep-warm refresh enqueued as {RunId} for {ScopeDescriptor}/{ReportType}/{Period}.",
                        candidate.CustomerId, runId, candidate.ScopeDescriptor, candidate.ReportType, candidate.Period);
                }
                catch (OrchestrationAlreadyExistsException)
                {
                    // Already running - a paid_interactive request or a previous tick beat this one to the key.
                    logger.LogInformation(
                        "Tenant {CustomerId}: keep-warm refresh for {ScopeDescriptor}/{ReportType}/{Period} already running; skipping.",
                        candidate.CustomerId, candidate.ScopeDescriptor, candidate.ReportType, candidate.Period);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Paid keep-warm could not be enqueued for tenant {CustomerId}; continuing.", candidate.CustomerId);
                }
            }

            /*  Spreads arrival across tenants, same reasoning as FreeDigestScheduler's per-tenant
                delay (Sec.4.4) - the durable queue and the concurrency gate are what actually
                bound execution; this only paces how fast the batch lane hands off work to them.  */
            await Task.Delay(settings.SchedulePerTenantDelay, cancellationToken);
        }
    }

    /// <summary>
    /// The latest GeneratedReport row per distinct (scope, reportType, period) key for this
    /// tenant, filtered to keys viewed within the keep-warm window and past cooldown (Sec.4.3,
    /// 4.5). Filters to CustomerId in SQL (translates trivially) and does the per-key "latest
    /// row" grouping in memory rather than via EF's GroupBy+First translation to SQL Server,
    /// which is not reliable across shapes - GeneratedReport is a metadata-only index table, at
    /// most a few dozen rows per tenant, so pulling one tenant's rows client-side costs nothing
    /// and removes the translation risk entirely. internal for PaidKeepWarmSchedulerTests.
    /// </summary>
    internal static async Task<IReadOnlyList<GeneratedReport>> GetKeepWarmCandidatesAsync(
        InsightsReportsDbContext db, int customerId, DateTime viewedSinceUtc, DateTime cooldownBeforeUtc,
        CancellationToken cancellationToken)
    {
        var rows = await db.GeneratedReports
            .Where(r => r.CustomerId == customerId)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => (r.ScopeDescriptor, r.ReportType, r.Period))
            .Select(g => g.OrderByDescending(r => r.GeneratedAtUtc).First())
            .Where(r => r.LastViewedUtc is not null && r.LastViewedUtc >= viewedSinceUtc && r.GeneratedAtUtc <= cooldownBeforeUtc)
            .ToList();
    }

    /// <summary>
    /// The calendar day this tenant's keep-warm cycle runs on - design doc Sec.4.2's
    /// hash(tenant_id) % 28 (here, % Schedule:PaidAnchorModulo). 1-based directly against
    /// DateTime.Day, same [TRAP] FreeDigestScheduler.AnchorDayFor already documents: NOT
    /// String.GetHashCode (randomised per process) - the id is already an integer, modulo it
    /// directly. Every calendar month has at least 28 days, so a 0..(modulo-1) spread mapped to
    /// day 1..modulo is always a real day in every month, no February edge case to handle.
    /// </summary>
    internal static int AnchorDayOfMonthFor(int customerId, int modulo) => Math.Abs(customerId) % modulo + 1;
}
