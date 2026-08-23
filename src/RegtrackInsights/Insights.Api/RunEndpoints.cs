using System.Text.Json;
using Insights.Data;
using Insights.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Insights.Api;

/// <summary>
/// Run progress for a paid report (API_CONTRACTS.md §4). Mapped with one line:
///
///     app.MapInsightsRunEndpoints();
///
/// Server-Sent Events rather than SignalR: the contract allows either, this is one-way
/// server-to-client with no fan-out and no group membership, and SSE needs no hub, no client
/// library and no extra dependency in the RegTrack API. If a SignalR hub is wanted later the
/// payload below is already the right shape to push through it.
/// </summary>
public static class RunEndpoints
{
    /// <summary>
    /// How often the instance store is polled. Two seconds is well under the shortest stage and
    /// far cheaper than the LLM calls it is reporting on, so the poll is never the bottleneck.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A hard ceiling on one connection's lifetime.
    ///
    /// Not a timeout on the RUN - the run continues regardless; this only closes the stream so a
    /// forgotten browser tab cannot pin a request thread indefinitely. The client reconnects and
    /// picks the run up again, which is safe precisely because progress lives in the instance
    /// store rather than in this connection.
    /// </summary>
    private static readonly TimeSpan MaxStreamDuration = TimeSpan.FromMinutes(15);

    public static IEndpointRouteBuilder MapInsightsRunEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/insights/runs/{runId}/stream", async (
            string runId,
            // [FromServices] on every injected dependency, explicitly. Minimal APIs otherwise INFER
            // the source, and an interface the host has not registered is inferred as a request
            // BODY - so a missing registration surfaces as "Body was inferred but the method does
            // not allow inferred body parameters" on a GET, which points nowhere near the cause.
            HttpContext http,
            [FromServices] IInsightsCaller caller,
            [FromServices] ITenantDirectoryRepository tenants,
            [FromServices] IRunStatusReader runs,
            CancellationToken cancellationToken) =>
        {
            /*  AUTHORISATION FIRST, and derived from the runId rather than trusted from it.

                The contract's URL carries no tenantId, so the tenant comes out of the run id
                itself. That id is derived and therefore guessable - anyone can construct
                "insights-{someTenant}-{hash}". So the parsed tenant is treated as a CLAIM to be
                verified, never as a grant: it goes straight into the same eligibility query every
                other endpoint uses, against the AUTHENTICATED caller, on this request.

                Skip this and the endpoint leaks another customer's generation progress - stage
                names, timing, and the existence of the report itself.                           */
            if (!InsightsRunId.TryParse(runId, out var tenantId))
                return InsightsResults.TenantNotEligible();

            var tenant = await tenants.IsEligibleAsync(caller.UserId, tenantId, cancellationToken);
            if (tenant is null)
                return InsightsResults.TenantNotEligible();

            var initial = await runs.GetStatusAsync(runId, cancellationToken);
            if (initial is null)
            {
                /*  Eligible tenant, no such run. 404 is safe here BECAUSE eligibility already
                    passed - the caller is entitled to know their own runs do or do not exist. A
                    404 before the eligibility check would have been the oracle.                 */
                return InsightsResults.Error(InsightsErrorCode.ReportNotVisible, "No such report run.");
            }

            await StreamAsync(http, runs, runId, initial, cancellationToken);

            /*  Nothing further to write - StreamAsync owns the response body from here.          */
            return Results.Empty;
        });

        return app;
    }

    private static async Task StreamAsync(
        HttpContext http,
        IRunStatusReader runs,
        string runId,
        InsightsRunStatus initial,
        CancellationToken cancellationToken)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        // Tells nginx and friends not to buffer, which would otherwise hold every event back
        // until the stream closed and defeat the entire point of streaming progress.
        http.Response.Headers["X-Accel-Buffering"] = "no";

        await WriteEventAsync(http, initial, cancellationToken);

        if (initial.IsTerminal)
            return;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MaxStreamDuration);

        var previous = initial;

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, deadline.Token);

                var current = await runs.GetStatusAsync(runId, deadline.Token);
                if (current is null)
                    return;

                /*  Only emit on CHANGE. A seven-stage run that takes four minutes would otherwise
                    push ~120 identical frames, every one of which the client has to diff.        */
                if (current != previous)
                {
                    await WriteEventAsync(http, current, deadline.Token);
                    previous = current;
                }

                if (current.IsTerminal)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            /*  Two very different endings arrive here as the same exception, and they must NOT be
                treated the same:

                  - the CLIENT disconnected     -> nobody is listening; write nothing.
                  - MaxStreamDuration elapsed   -> the client IS listening, and the run is still
                                                   going. Closing silently here is the dangerous
                                                   case: a client that reads stream-close as "done"
                                                   would show a finished report that never
                                                   finished. So it gets one final NAMED event
                                                   saying the stream ended and the run did not.
                                                                                                */
            if (!cancellationToken.IsCancellationRequested)
                await TryWriteFinalFrameAsync(http, previous);
        }
    }

    /// <summary>
    /// Best-effort last frame when the connection deadline expires mid-run. Wrapped because the
    /// socket may already be gone - and a failure to write a courtesy frame must not surface as a
    /// 500 on a response whose headers were sent minutes ago.
    /// </summary>
    private static async Task TryWriteFinalFrameAsync(HttpContext http, InsightsRunStatus last)
    {
        try
        {
            /*  A NAMED event, not another status frame. Re-sending the last status would be a
                duplicate the client cannot distinguish from a real update; "reconnect" says the
                one thing that is actually new - this stream ended without the run ending.      */
            await WriteFrameAsync(http, eventName: "reconnect", last, CancellationToken.None);
        }
        catch (Exception)
        {
            // The client is gone. Nothing to report and nowhere to report it.
        }
    }

    private static Task WriteEventAsync(HttpContext http, InsightsRunStatus status, CancellationToken cancellationToken) =>
        WriteFrameAsync(http, eventName: null, status, cancellationToken);

    /// <summary>
    /// Writes one SSE frame. A null eventName leaves it as the default "message" type that a
    /// plain onmessage handler receives.
    /// </summary>
    private static async Task WriteFrameAsync(
        HttpContext http, string? eventName, InsightsRunStatus status, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            runId = status.RunId,
            status = status.Status,
            stage = status.Stage,
            stagesComplete = status.StagesComplete,
            stagesTotal = status.StagesTotal,
            // Present only on failure, and user-safe by construction - see InsightsRunStatus.
            message = status.Message,
        });

        var frame = eventName is null
            ? "data: " + payload + "\n\n"
            : "event: " + eventName + "\ndata: " + payload + "\n\n";

        await http.Response.WriteAsync(frame, cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
    }
}
