using System.Text.Json;
using Insights.Data;
using Insights.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Insights.Api;

/// <summary>
/// Generate (API_CONTRACTS.md §3) and run progress (§4) for a paid report. Mapped with one line:
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
        app.MapPost("/api/insights/reports", async (
            [FromBody] GenerateReportRequest request,
            [FromServices] IInsightsCaller caller,
            [FromServices] ITenantDirectoryRepository tenants,
            [FromServices] IScopeRepository scope,
            [FromServices] ICooldownRepository cooldown,
            [FromServices] IInsightsRunEnqueuer enqueuer,
            CancellationToken cancellationToken) =>
        {
            // Same ordering as the stream endpoint: authorise against the AUTHENTICATED caller
            // before touching anything scope- or run-related, never trust the client-supplied
            // tenantId on its own (API_CONTRACTS.md cross-cutting rule 1, the IDOR guard).
            var tenant = await tenants.IsEligibleAsync(caller.UserId, request.TenantId, cancellationToken);
            if (tenant is null)
                return InsightsResults.TenantNotEligible();

            var scopePairs = await scope.GetScopePairsAsync(caller.UserId, request.TenantId, cancellationToken);
            if (scopePairs.Count == 0)
            {
                // Entitled to the tenant, but nothing in scope - a distinct refusal from
                // TENANT_NOT_ELIGIBLE. Never silently generate an empty report for this case
                // (spec §11.2 - an empty report reads as "healthy", not "no data").
                return InsightsResults.Error(InsightsErrorCode.ScopeDenied, "No entities are currently in your Insights scope.");
            }

            // [TEMP WORKAROUND 2026-09-09, see ReportDimensionKey's own doc comment] - folds
            // RequestedDimensions into the period used for BOTH the cooldown check and the
            // enqueue below, so a dimension_selection request for Nature and one for Entity
            // against the identical caller-supplied period are treated as separate keys. This is
            // a stand-in for the real fix (a GeneratedReport.RequestedDimensions column,
            // sql/28_generated_report_dimension_key.sql, not yet deployed) - no-op for every
            // report type except dimension_selection.
            var effectivePeriod = ReportDimensionKey.ForCooldownAndRunId(request.Period, request.RequestedDimensions);

            // Step 3 (design doc Sec.2.4's 30-day cooldown) - keyed to (scope, reportType, period),
            // NOT to this caller, so a colleague at the same scope who generated it yesterday locks
            // this call too. [IMPLEMENTED 2026-09-08 - was a KNOWN LIMITATION pending build order
            // item 14 (GeneratedReport persistence); item 14 shipped, so there is now a real report
            // history to check this against.]
            var cooldownResult = await cooldown.CheckAsync(
                request.TenantId, request.ReportType, request.Scope.ToDescriptor(), effectivePeriod, cancellationToken);
            if (!cooldownResult.IsOpen)
                return InsightsResults.CooldownActive(cooldownResult.NextAvailableUtc!.Value);

            // Step 4 (the one-active-run-per-key lock) is free: EnqueueAsync derives the run id
            // from (tenant, scope, reportType, period), so a second call for the same key attaches
            // to the already-running instance instead of starting a duplicate. effectivePeriod
            // (not request.Period) is what makes that key correctly per-dimension - see above.
            var runId = await enqueuer.EnqueueAsync(
                request.TenantId, request.ReportType, request.Scope, effectivePeriod, caller.UserId, cancellationToken,
                requestedDimensions: request.RequestedDimensions);

            var streamUrl = $"/api/insights/runs/{runId}/stream";
            return Results.Accepted(streamUrl, new { runId, status = "queued", streamUrl });
        });

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

/// <summary>
/// The wire shape of API_CONTRACTS.md §3's POST body.
///
/// <paramref name="RequestedDimensions"/> [ADDED 2026-09-09] - trailing optional, same reasoning
/// as every other RequestedDimensions plumbing point in this codebase (InsightsRunOnceWorker's
/// CLI flag, InsightsReportOrchestrationInput): null/omitted for every ReportType except
/// "dimension_selection" (Insights.Domain.DimensionSelectionComposition.ReportType), where it
/// names exactly which of the fourteen dimensions this run scopes to. This is the API-side half
/// of the multi-dimension report feature - previously only reachable via
/// InsightsRunOnceWorker's --Insights:Dimensions CLI flag.
/// </summary>
public sealed record GenerateReportRequest(
    int TenantId, string ReportType, InsightsScopeRequest Scope, string Period,
    IReadOnlyList<string>? RequestedDimensions = null);
