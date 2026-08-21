using Insights.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// The weekly digest schedule (design doc 10.3): weekly, staggered by hash(tenant_id) % 7,
/// batch lane, lowest priority - it must never delay a paying user's on-demand generation.
///
/// Off unless FreeDigest:Schedule:Enabled is true, so a worker started for any other reason does
/// not begin mailing customers.
///
/// SAFE TO TICK OFTEN. It does not track what it has already done: the atomic per-recipient
/// claim in sql/15 is the once-per-week guarantee. A restart, an overlapping tick, or two
/// instances running at once all converge on one email per recipient per week. That is why the
/// claim exists rather than in-memory state, which a pod restart would lose.
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

        var interval = settings.ScheduleCheckInterval;
        var sendHourUtc = settings.ScheduleSendHourUtc;

        logger.LogInformation(
            "Free digest schedule enabled. Checking every {Interval}, sending from {Hour:00}:00 UTC on each tenant's anchor day.",
            interval, sendHourUtc);

        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await RunDueTenantsAsync(sendHourUtc, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A bad tick must not kill the lane - the next one may well succeed.
                logger.LogError(ex, "Free digest schedule tick failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunDueTenantsAsync(int sendHourUtc, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (now.Hour < sendHourUtc)
            return;

        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFreeDigestRepository>();
        var digest = scope.ServiceProvider.GetRequiredService<IFreeDigestService>();

        var tenants = await repository.GetEntitledTenantsAsync(cancellationToken);
        var due = tenants.Where(t => AnchorDayFor(t.CustomerId) == now.DayOfWeek).ToList();

        if (due.Count == 0)
            return;

        logger.LogInformation("Free digest lane: {DueCount} of {TotalCount} tenant(s) anchored to {Day}.",
            due.Count, tenants.Count, now.DayOfWeek);

        foreach (var tenant in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await digest.RunForTenantAsync(tenant.CustomerId, cancellationToken: cancellationToken);

                if (result.SentCount > 0 || result.SkippedCount > 0)
                {
                    logger.LogInformation("Tenant {CustomerId}: sent {Sent}, skipped {Skipped}.",
                        tenant.CustomerId, result.SentCount, result.SkippedCount);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Free digest failed for tenant {CustomerId}; continuing.", tenant.CustomerId);
            }

            /*  LOWEST-PRIORITY LANE (10.3). A deliberate pause between tenants so a 600-tenant
                batch does not saturate the database, the LLM quota or the mail provider while a
                paying user is waiting on an on-demand report. Correctness does not depend on it;
                being a good neighbour does.                                                     */
            await Task.Delay(settings.SchedulePerTenantDelay, cancellationToken);
        }
    }

    /// <summary>
    /// The day of the week this tenant's digest goes out - design doc 10.3's hash(tenant_id) % 7.
    ///
    /// [TRAP] NOT String.GetHashCode. .NET string hashing is RANDOMISED PER PROCESS, so a tenant
    /// would land on a different day after every restart - and with the claim keyed on the week,
    /// a tenant could be mailed on Monday under one seed and again on Thursday under another.
    /// The id is already an integer; modulo it directly and the answer is stable forever.
    /// </summary>
    internal static DayOfWeek AnchorDayFor(int customerId) => (DayOfWeek)(Math.Abs(customerId) % 7);
}

