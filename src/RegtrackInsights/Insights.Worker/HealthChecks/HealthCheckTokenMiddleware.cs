using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Insights.Worker.HealthChecks;

/// <summary>
/// Requires a static shared secret on the /health endpoints, mirroring the sibling RegTrack API's
/// HealthCheckTokenMiddleware exactly - same header name, same fail-open reasoning, so ops has one
/// mental model across both services.
/// </summary>
public sealed class HealthCheckTokenMiddleware
{
    /// <summary>
    /// Request header callers must carry the shared secret in. Kubernetes probes send the same
    /// header from their exec command; see the deployment manifest wherever it ends up living.
    /// </summary>
    public const string HeaderName = "X-Health-Token";

    private readonly RequestDelegate _next;
    private readonly byte[]? _expectedToken;

    /// <summary>
    /// Reads the expected token once at construction. A missing, blank or whitespace-only token
    /// leaves <see cref="_expectedToken"/> null, which disables the gate - see
    /// <see cref="InvokeAsync"/> for why that is deliberate.
    /// </summary>
    public HealthCheckTokenMiddleware(RequestDelegate next, IOptions<HealthCheckAuthConfig> config)
    {
        _next = next;
        _expectedToken = SharedSecretTokenComparer.Encode(config.Value?.Token);
    }

    /// <summary>
    /// Passes the request on when the header matches the configured token, and short-circuits with
    /// 401 when it does not. When no token is configured at all, every request passes through
    /// untouched.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // Fail OPEN when no token is configured. These endpoints back the Kubernetes readiness,
        // liveness and startup probes. Rejecting every probe in an environment with no Secret
        // configured yet would pull the pod from service and then CrashLoopBackOff it - an
        // unauthenticated health endpoint is a leak, a CrashLoopBackOff is an outage, and outages
        // lose. Program.cs logs the gap loudly at startup instead.
        if (_expectedToken is null)
        {
            await _next(context);
            return;
        }

        if (!IsAuthorized(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"status\":\"Unauthorized\"}");
            return;
        }

        await _next(context);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderName, out var supplied))
            return false;

        return SharedSecretTokenComparer.Matches(_expectedToken, supplied.ToString());
    }
}
