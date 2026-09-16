using Insights.Contracts;
using Insights.Data;
using Insights.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Insights.Api;

/// <summary>
/// The free weekly digest's HTTP surface.
///
/// [SPLIT 2026-09-16] Was one MapDigestEndpoints() mapping both routes together. Split into two
/// methods because they now need DIFFERENT treatment on the combined host:
///
///   MapDigestUnsubscribeEndpoint() - safe to map anonymously as-is. Its own HMAC token (over
///   customer+user, issued when the mail was sent) is a real, already-verified gate - see the
///   method's own comment.
///
///   MapDigestBounceEndpoint() - has NO gate at all today. Its own comment says "protect it the
///   way the host protects its other inbound webhooks", written when the only host was a local
///   dev harness with no other webhooks to imitate. On a publicly reachable host this is an
///   unauthenticated way to durably suppress digest email for any (customerId, userId) pair -
///   confirmed a real finding, not a hypothetical, during the combined-host design review.
///   Program.cs deliberately does NOT call this method yet. Map it only once a shared-secret
///   bearer gate exists for it (mirror the sibling RegTrack API's AiReportTokenMiddleware
///   pattern - a gate that fails CLOSED, unlike the health-check token, because this route
///   writes tenant-scoped data). Until then the mail provider's bounce webhook has nowhere to
///   post to; that is the deliberately safe default, not an oversight.
///
/// MapDigestEndpoints() is kept as a convenience that calls both, for the local dev harness
/// (tools/Insights.ApiDevHost) and integration tests, which are not exposed to the internet and
/// want the whole surface clickable/testable in one place.
/// </summary>
public static class DigestEndpoints
{
    public static IEndpointRouteBuilder MapDigestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDigestUnsubscribeEndpoint();
        app.MapDigestBounceEndpoint();
        return app;
    }

    public static IEndpointRouteBuilder MapDigestUnsubscribeEndpoint(this IEndpointRouteBuilder app)
    {
        /*  UNSUBSCRIBE - the link at the foot of every digest.

            GET, and reachable without a login, because it is clicked from an email client by
            someone who may not be signed in. Requiring a login to stop receiving mail is the
            pattern anti-spam rules exist to prevent, and email clients cannot POST from a link.

            The token is what makes it safe: an HMAC over (customer, user) issued when the mail
            was sent. Without it the URL is enumerable, and since suppression is durable
            (spec 5.4) an enumeration sweep would have to be undone row by row.

            AllowAnonymous is explicit and deliberate here - the combined host's fallback
            authorization policy (JwtAuthRegistration) requires an authenticated user on every
            endpoint by default, and this is one of the two named exceptions.                   */
        app.MapGet("/api/insights/digest/unsubscribe", async (
            int c, long u, string? t,
            IFreeDigestRepository repository,
            IConfiguration configuration,
            ILogger<DigestEndpointsLogCategory> logger) =>
        {
            if (c <= 0 || u <= 0)
                return Results.BadRequest(new { message = "Invalid customer id or user id." });

            string? signingKey = configuration["Email:UnsubscribeSigningKey"];
            if (string.IsNullOrWhiteSpace(signingKey))
            {
                logger.LogError("Email:UnsubscribeSigningKey is not configured; unsubscribe links cannot be verified.");
                return Results.Problem("Unsubscribe is temporarily unavailable.", statusCode: StatusCodes.Status500InternalServerError);
            }

            if (!UnsubscribeToken.Verify(signingKey, c, u, t))
            {
                logger.LogWarning("Unsubscribe rejected: invalid token for customer {CustomerId}, user {UserId}.", c, u);
                return Results.BadRequest(new { message = "This unsubscribe link is not valid." });
            }

            await repository.SuppressAsync(c, u, DigestSuppressionReason.Unsubscribed, "unsubscribed via email link");

            logger.LogInformation("Unsubscribed customer {CustomerId}, user {UserId}.", c, u);

            return Results.Ok(new { message = "You have been unsubscribed from RegTrack Insights weekly digests." });
        })
        .AllowAnonymous();

        return app;
    }

    public static IEndpointRouteBuilder MapDigestBounceEndpoint(this IEndpointRouteBuilder app)
    {
        /*  BOUNCE - posted by the mail provider when a digest permanently fails to deliver.

            Reachable without a login because the provider has no RegTrack account. STILL HAS NO
            GATE OF ITS OWN - see this file's class-level doc comment. Do not call this method from
            the deployed host until a shared-secret bearer gate exists for it. .AllowAnonymous() is
            applied here anyway so the dev harness and integration tests (the only current callers)
            keep working without also having to satisfy the combined host's fallback auth policy -
            that policy only exists once JwtAuthRegistration is wired in, which those hosts don't
            do today, but applying it consistently here costs nothing and avoids surprise later.  */
        app.MapPost("/api/insights/digest/bounce", async (
            DigestBounceRequest request,
            IFreeDigestRepository repository,
            ILogger<DigestEndpointsLogCategory> logger) =>
        {
            if (request.CustomerId <= 0 || request.UserId <= 0)
                return Results.BadRequest(new { message = "Invalid customer id or user id." });

            /*  ONLY HARD BOUNCES SUPPRESS. A soft bounce is a full mailbox or a temporary outage;
                suppressing on one would silently unsubscribe someone whose mail arrives fine next
                week, and suppression is durable.                                                 */
            if (!string.Equals(request.BounceType, "hard", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Soft bounce for customer {CustomerId}, user {UserId}; not suppressing.",
                    request.CustomerId, request.UserId);
                return Results.Ok(new { message = "Soft bounce recorded." });
            }

            await repository.SuppressAsync(request.CustomerId, request.UserId,
                DigestSuppressionReason.HardBounce, request.Reason);

            logger.LogWarning("Hard bounce: suppressed customer {CustomerId}, user {UserId}. Reason: {Reason}",
                request.CustomerId, request.UserId, request.Reason);

            return Results.Ok(new { message = "Recipient suppressed." });
        })
        .AllowAnonymous();

        return app;
    }

    /// <summary>Log category for these endpoints. Minimal APIs have no class to name a logger after.</summary>
    private sealed class DigestEndpointsLogCategory;
}
