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
/// The free weekly digest's HTTP surface. Three endpoints, mapped with one line:
///
///     app.MapDigestEndpoints();
///
/// No authentication here on purpose - RegTrack already has it, and these are meant to slot into
/// that pipeline rather than bring a second one. Apply the host's usual auth to the trigger; the
/// unsubscribe and bounce endpoints must stay reachable without a login (see each).
/// </summary>
public static class DigestEndpoints
{
    public static IEndpointRouteBuilder MapDigestEndpoints(this IEndpointRouteBuilder app)
    {
        /*  UNSUBSCRIBE - the link at the foot of every digest.

            GET, and reachable without a login, because it is clicked from an email client by
            someone who may not be signed in. Requiring a login to stop receiving mail is the
            pattern anti-spam rules exist to prevent, and email clients cannot POST from a link.

            The token is what makes it safe: an HMAC over (customer, user) issued when the mail
            was sent. Without it the URL is enumerable, and since suppression is durable
            (spec 5.4) an enumeration sweep would have to be undone row by row.                 */
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
        });

        /*  BOUNCE - posted by the mail provider when a digest permanently fails to deliver.

            Reachable without a login because the provider has no RegTrack account. Protect it the
            way the host protects its other inbound webhooks.                                    */
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
        });

        return app;
    }

    /// <summary>Log category for these endpoints. Minimal APIs have no class to name a logger after.</summary>
    private sealed class DigestEndpointsLogCategory;
}
