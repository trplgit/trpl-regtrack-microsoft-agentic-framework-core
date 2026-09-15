using System.Net;
using System.Text.Json;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// GET /api/insights/requests/{reqId}/stream - the fan-out umbrella status (2026-09-11 feature).
/// One reqId groups the N runIds a dimension_selection POST fanned out; this endpoint rolls their
/// individual statuses into one combined value via RequestStatusAggregator's worst-first rule.
/// </summary>
public sealed class RequestStreamEndpointTests
{
    private const int Tenant = 1490;
    private const int Caller = 38;

    private static string RunIdFor(int tenantId, string period) =>
        InsightsRunId.For(tenantId, "tenant", "dimension_selection", period);

    private static EligibleTenant Eligible(int tenantId) =>
        new(tenantId, "Acme Holdings", EntitlementTier.Paid, ScopeClass.TenantWide);

    /// <summary>
    /// reqId is Guid.NewGuid() - not derived from anything guessable (unlike a runId), so unlike
    /// the single-run stream endpoint, existence is checked BEFORE eligibility here. Pin that a
    /// caller who is not even eligible for ANY tenant still gets the same 404, not a 403.
    /// </summary>
    [Fact]
    public async Task Stream_ReturnsNotFoundForAnUnknownReqId_WithoutCheckingEligibility()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        // Never actually called (the empty-runIds branch returns before touching IRunStatusReader)
        // but minimal API resolves every [FromServices] parameter for the matched endpoint up
        // front regardless of which early-exit branch the request takes - same trap
        // GenerateReportEndpointTests.OpenCooldown's own doc comment already flags.
        var client = await InsightsApiTestHost.StartAsync(Caller, directory, new FakeRunStatusReader(null));

        var response = await client.GetAsync($"/api/insights/requests/{Guid.NewGuid()}/stream");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorCodeAsync(response, "REPORT_NOT_VISIBLE");
        Assert.Empty(directory.Calls);
    }

    [Fact]
    public async Task Stream_RefusesAKnownReqIdBelongingToATenantTheCallerIsNotEligibleFor()
    {
        var reqId = Guid.NewGuid();
        var runId = RunIdFor(9999, "FY2025-26");
        var requests = new FakeReportRequestRepository();
        await requests.SaveAsync(reqId, [runId]);
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var runs = new FakeRunStatusReader(new InsightsRunStatus(runId, "running", "narrating", 3, 7, null));

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs, requests: requests);

        var response = await client.GetAsync($"/api/insights/requests/{reqId}/stream");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorCodeAsync(response, "TENANT_NOT_ELIGIBLE");
    }

    /// <summary>
    /// "queued" and "in_progress" are NON-terminal (RunEndpoints.IsTerminalRequestStatus) - the
    /// server keeps the connection open polling for the next change, so a plain GetAsync (which
    /// waits for the response to finish) would hang until MaxStreamDuration. ReadFirstFrameAsync
    /// reads just the one frame and aborts the connection itself instead - the same "client
    /// disconnected mid-stream" case StreamRequestAsync's own try/catch already handles.
    /// </summary>
    [Fact]
    public async Task Stream_AllSubRunsQueued_ReportsQueued()
    {
        var reqId = Guid.NewGuid();
        var runIds = new[] { RunIdFor(Tenant, "Location"), RunIdFor(Tenant, "Act") };
        var requests = new FakeReportRequestRepository();
        await requests.SaveAsync(reqId, runIds);
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var runs = FakeRunStatusReader.PerRunId(runId => new InsightsRunStatus(runId, "queued", null, 0, 7, null));

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs, requests: requests);

        var frame = await ReadFirstFrameAsync(client, $"/api/insights/requests/{reqId}/stream");

        Assert.Equal(reqId.ToString(), frame.GetProperty("reqId").GetString());
        Assert.Equal("queued", frame.GetProperty("status").GetString());
    }

    /// <summary>Worst-first: one sub-run actually running beats the rest still sitting queued.</summary>
    [Fact]
    public async Task Stream_OneSubRunRunning_ReportsInProgress_EvenWithOthersStillQueued()
    {
        var reqId = Guid.NewGuid();
        var runningRunId = RunIdFor(Tenant, "Location");
        var queuedRunId = RunIdFor(Tenant, "Act");
        var requests = new FakeReportRequestRepository();
        await requests.SaveAsync(reqId, [runningRunId, queuedRunId]);
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var runs = FakeRunStatusReader.PerRunId(runId => runId == runningRunId
            ? new InsightsRunStatus(runId, "running", "narrating", 3, 7, null)
            : new InsightsRunStatus(runId, "queued", null, 0, 7, null));

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs, requests: requests);

        var frame = await ReadFirstFrameAsync(client, $"/api/insights/requests/{reqId}/stream");

        Assert.Equal("in_progress", frame.GetProperty("status").GetString());
    }

    /// <summary>Worst-first: one failure surfaces as error immediately, even while another sub-run is still running.</summary>
    [Fact]
    public async Task Stream_OneSubRunFailed_ReportsError_EvenWithAnotherStillRunning()
    {
        var reqId = Guid.NewGuid();
        var failedRunId = RunIdFor(Tenant, "Location");
        var runningRunId = RunIdFor(Tenant, "Act");
        var requests = new FakeReportRequestRepository();
        await requests.SaveAsync(reqId, [failedRunId, runningRunId]);
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var runs = FakeRunStatusReader.PerRunId(runId => runId == failedRunId
            ? new InsightsRunStatus(runId, "failed", "verifying", 5, 7, "Report generation failed. Please try again.")
            : new InsightsRunStatus(runId, "running", "narrating", 3, 7, null));

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs, requests: requests);

        var response = await client.GetAsync($"/api/insights/requests/{reqId}/stream");
        var frame = SingleFrame(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("error", frame.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Stream_AllSubRunsComplete_ReportsCompleted()
    {
        var reqId = Guid.NewGuid();
        var runIds = new[] { RunIdFor(Tenant, "Location"), RunIdFor(Tenant, "Act") };
        var requests = new FakeReportRequestRepository();
        await requests.SaveAsync(reqId, runIds);
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var runs = FakeRunStatusReader.PerRunId(runId => new InsightsRunStatus(runId, "complete", "complete", 7, 7, null));

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs, requests: requests);

        var frame = SingleFrame(await (await client.GetAsync($"/api/insights/requests/{reqId}/stream")).Content.ReadAsStringAsync());

        Assert.Equal("completed", frame.GetProperty("status").GetString());
    }

    private static JsonElement SingleFrame(string body)
    {
        var frames = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var frame = Assert.Single(frames);
        using var json = JsonDocument.Parse(frame.Substring("data: ".Length));
        return json.RootElement.Clone();
    }

    /// <summary>Reads exactly one SSE frame, then aborts the connection - see the doc comment on the two callers above.</summary>
    private static async Task<JsonElement> ReadFirstFrameAsync(HttpClient client, string url)
    {
        using var cts = new CancellationTokenSource();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        var dataLine = await reader.ReadLineAsync(cts.Token)
            ?? throw new InvalidOperationException("Stream closed before emitting a frame.");

        cts.Cancel();

        using var json = JsonDocument.Parse(dataLine["data: ".Length..]);
        return json.RootElement.Clone();
    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expected)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
