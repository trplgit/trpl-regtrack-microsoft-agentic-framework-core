using System.Threading.RateLimiting;

namespace Insights.Data.Email;

/// <summary>
/// Decorates whichever IEmailSender FreeDigestRegistration selected (ElasticEmail, SendGrid, or
/// the failover pair) with a steady, no-burst rate limit.
///
/// -- WHY A STEADY LIMIT, NOT A BURST-THEN-WAIT ONE -------------------------------------------
/// The ElasticEmail account is shared with other services outside this project. A limiter that
/// lets 5 requests through instantly and then waits a second is still "5 per second" on average,
/// but it is a burst the account's other users feel as a spike. TokenBucketRateLimiter with
/// TokenLimit = 1 never accumulates a bucket to spend at once - at most one request is ever
/// in flight from this limiter at a time, replenished every 200ms, which is the actual steady
/// 5-requests-per-second behaviour the account needs to stay a good citizen.
///
/// -- THIS IS A PROCESS-LOCAL LIMIT -----------------------------------------------------------
/// If this worker is ever scaled to more than one replica, each replica presents its own 5/sec to
/// the shared account - N replicas is N times the configured rate. Today the worker is effectively
/// single-replica (WorkerRegistration.cs pins MaxConcurrentActivities/MaxActiveOrchestrations to 1
/// for an unrelated duplicate-execution fix), so this is correct as configured, but it is a
/// deployment constraint to remember, not a property this class can enforce on its own.
/// </summary>
public sealed class RateLimitedEmailSender : IEmailSender, IDisposable
{
    private readonly IEmailSender _inner;
    private readonly RateLimiter _limiter;
    private readonly TimeSpan _acquireTimeout;

    public RateLimitedEmailSender(IEmailSender inner, int requestsPerSecond, TimeSpan acquireTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestsPerSecond, 0);

        _inner = inner;
        _acquireTimeout = acquireTimeout;
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1.0 / requestsPerSecond),
            AutoReplenishment = true,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = int.MaxValue,
        });
    }

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_acquireTimeout);

        using var lease = await _limiter.AcquireAsync(1, timeout.Token);
        if (!lease.IsAcquired)
            throw new TimeoutException($"Could not acquire an email send slot within {_acquireTimeout} - the rate limiter is wedged or overwhelmed.");

        return await _inner.SendAsync(message, cancellationToken);
    }

    public void Dispose() => _limiter.Dispose();
}
