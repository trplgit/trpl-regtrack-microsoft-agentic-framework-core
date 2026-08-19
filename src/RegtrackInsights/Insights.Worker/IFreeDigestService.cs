using Insights.Domain;

namespace Insights.Worker;

/// <summary>
/// The free weekly digest, as one callable unit. THIS IS THE INTEGRATION SURFACE - the RegTrack
/// API calls into this; nothing else needs to know about gates, aggregates, prompts, templates,
/// recipients or providers.
///
/// Deliberately NOT an HTTP endpoint. This worker is private and has no ingress; the endpoint
/// lives in the existing RegTrack API where auth already lives (CLAUDE.md 6).
///
/// Neither method throws for an ordinary refusal - an unentitled tenant, a tenant with no
/// recipients, or a recipient with no authorised scope all come back as a recorded outcome. A
/// batch over ~600 tenants must not abort because one is misconfigured. Genuine faults (a bad
/// connection string, a dictionary gap found in pre-flight) still throw.
/// </summary>
public interface IFreeDigestService
{
    /// <summary>
    /// Runs the digest for one tenant: gate, then one email per entitled recipient.
    /// This is what an on-demand RegTrack endpoint calls.
    /// </summary>
    Task<FreeDigestTenantResult> RunForTenantAsync(int customerId, DateTime? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the digest for every tenant mapped-and-enabled for product 18. This is what the
    /// weekly scheduler calls.
    ///
    /// Asserts dictionary coverage ONCE up front rather than per tenant - a dictionary gap is a
    /// property of the deployment, so discovering it 600 times is 599 wasted failures.
    /// </summary>
    Task<FreeDigestBatchResult> RunWeeklyAsync(DateTime? asOf = null, CancellationToken cancellationToken = default);
}
