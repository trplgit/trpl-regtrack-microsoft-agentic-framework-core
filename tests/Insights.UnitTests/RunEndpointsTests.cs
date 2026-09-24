using System.Net;
using System.Text.Json;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// GET /api/insights/runs/{runId}/stream (API_CONTRACTS.md 4).
///
/// The URL carries no tenantId, so the tenant is parsed out of the run id - and a derived run id
/// is guessable. These tests exist mostly to pin that the parsed tenant is treated as a claim to
/// be checked and never as a grant.
/// </summary>
public sealed class RunEndpointsTests
{
    private const int Tenant = 1490;
    private const int Caller = 38;

    private static string RunIdFor(int tenantId) =>
        InsightsRunId.For(tenantId, "tenant", "compliance_health", "FY2025-26");

    private static EligibleTenant Eligible(int tenantId) =>
        new(tenantId, "Acme Holdings", EntitlementTier.Paid, ScopeClass.TenantWide);

    private static InsightsRunStatus Running(string runId) =>
        new(runId, "running", "composing", 3, 7, null);

    /// <summary>
    /// THE ONE THAT MATTERS. A well-formed run id for a tenant the caller has no access to must
    /// be refused - the id being constructible is not permission to read it.
    /// </summary>
    [Fact]
    public async Task Stream_RefusesARunBelongingToAnotherTenant()
    {
        var otherTenantRun = RunIdFor(9999);
        var runs = new FakeRunStatusReader(Running(otherTenantRun));
        var directory = new FakeTenantDirectory(Eligible(Tenant));

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs);

        var response = await client.GetAsync($"/api/insights/runs/{otherTenantRun}/stream");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorCodeAsync(response, "TENANT_NOT_ELIGIBLE");

        // And it must refuse WITHOUT reading the run. Fetching first and filtering after would
        // still return 403, so the status code alone cannot tell the two apart - only this can.
        Assert.Equal(0, runs.CallCount);
    }

    [Fact]
    public async Task Stream_RefusesAMalformedRunIdWithoutTouchingTheDirectory()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var runs = new FakeRunStatusReader(null);

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs);

        var response = await client.GetAsync("/api/insights/runs/not-a-run-id/stream");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(directory.Calls);
        Assert.Equal(0, runs.CallCount);
    }

    /// <summary>
    /// Eligible tenant, no such run: 404. Safe only BECAUSE eligibility already passed - the
    /// caller is entitled to know whether their own run exists.
    /// </summary>
    [Fact]
    public async Task Stream_ReturnsNotFoundForAnUnknownRunOnAnEligibleTenant()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var client = await InsightsApiTestHost.StartAsync(Caller, directory, new FakeRunStatusReader(null));

        var response = await client.GetAsync($"/api/insights/runs/{RunIdFor(Tenant)}/stream");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorCodeAsync(response, "REPORT_NOT_VISIBLE");
    }

    /// <summary>Eligibility is re-derived per request, for the authenticated caller and that exact tenant.</summary>
    [Fact]
    public async Task Stream_ChecksEligibilityForTheTenantEncodedInTheRunId()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var client = await InsightsApiTestHost.StartAsync(Caller, directory, new FakeRunStatusReader(null));

        await client.GetAsync($"/api/insights/runs/{RunIdFor(Tenant)}/stream");

        var call = Assert.Single(directory.Calls);
        Assert.Equal(Caller, call.UserId);
        Assert.Equal(Tenant, call.CustomerId);
    }

    /// <summary>A terminal run emits one frame and closes, rather than polling for fifteen minutes.</summary>
    [Fact]
    public async Task Stream_EmitsOneEventAndClosesOnATerminalRun()
    {
        var runId = RunIdFor(Tenant);
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var runs = new FakeRunStatusReader(new InsightsRunStatus(runId, "complete", "complete", 7, 7, null));

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs);

        var response = await client.GetAsync($"/api/insights/runs/{runId}/stream");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var frames = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var frame = Assert.Single(frames);

        using var json = JsonDocument.Parse(frame.Substring("data: ".Length));
        Assert.Equal("complete", json.RootElement.GetProperty("status").GetString());

        // [CHANGED 2026-09-14, PRODUCT DECISION] The detailed stage breakdown is deliberately not
        // sent on the wire anymore - status alone is the whole external contract now.
        Assert.False(json.RootElement.TryGetProperty("stage", out _));
        Assert.False(json.RootElement.TryGetProperty("stagesComplete", out _));
        Assert.False(json.RootElement.TryGetProperty("stagesTotal", out _));
    }

    /// <summary>
    /// [ADDED 2026-09-16] Closes the real gap found live: without reportId on the wire, a client
    /// watching "complete" had no id to call API_CONTRACTS.md §5's content endpoint with.
    /// </summary>
    [Fact]
    public async Task Stream_CompleteRunIncludesReportId()
    {
        var runId = RunIdFor(Tenant);
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var runs = new FakeRunStatusReader(
            new InsightsRunStatus(runId, "complete", "complete", 7, 7, null, ReportId: "b3f6c1a2-0000-4000-8000-000000000001"));

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs);

        var body = await (await client.GetAsync($"/api/insights/runs/{runId}/stream")).Content.ReadAsStringAsync();
        var frame = Assert.Single(body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries));

        using var json = JsonDocument.Parse(frame.Substring("data: ".Length));
        Assert.Equal("b3f6c1a2-0000-4000-8000-000000000001", json.RootElement.GetProperty("reportId").GetString());
    }

    /// <summary>
    /// A failed run carries a user-safe message and nothing else. The gate real diagnostics -
    /// "reconciliation variance of 3 on branch X" - are exactly what 11.3 keeps off the wire.
    /// </summary>
    [Fact]
    public async Task Stream_ReportsFailureWithoutInternalDetail()
    {
        var runId = RunIdFor(Tenant);
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var runs = new FakeRunStatusReader(
            new InsightsRunStatus(runId, "failed", "verifying", 5, 7, "Report generation failed. Please try again."));

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs);

        var response = await client.GetAsync($"/api/insights/runs/{runId}/stream");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("failed", body);
        Assert.DoesNotContain("reconciliation", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("variance", body, StringComparison.OrdinalIgnoreCase);

        // [ADDED 2026-09-16] reportId must never carry a value on a failed run -
        // InsightsRunStatus.ReportId is only ever populated when Status is "complete" (see its own
        // doc comment); this pins that on the wire, not just in the type. The property itself is
        // still present (JsonSerializer includes nulls by default here), just null-valued.
        var frame = Assert.Single(body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries));
        using var json = JsonDocument.Parse(frame.Substring("data: ".Length));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("reportId").ValueKind);
    }

    /// <summary>POST /api/insights/runs/{runId}/cancel - same IDOR guard as the stream endpoint.</summary>
    [Fact]
    public async Task Cancel_RefusesARunBelongingToAnotherTenant()
    {
        var otherTenantRun = RunIdFor(9999);
        var runs = new FakeRunStatusReader(Running(otherTenantRun));
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var canceller = new FakeRunCanceller();

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs, canceller: canceller);

        var response = await client.PostAsync($"/api/insights/runs/{otherTenantRun}/cancel", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorCodeAsync(response, "TENANT_NOT_ELIGIBLE");
        Assert.Empty(canceller.Calls);
    }

    [Fact]
    public async Task Cancel_ReturnsNotFoundForAnUnknownRunOnAnEligibleTenant()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var canceller = new FakeRunCanceller();
        var client = await InsightsApiTestHost.StartAsync(Caller, directory, new FakeRunStatusReader(null), canceller: canceller);

        var response = await client.PostAsync($"/api/insights/runs/{RunIdFor(Tenant)}/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorCodeAsync(response, "REPORT_NOT_VISIBLE");
        Assert.Empty(canceller.Calls);
    }

    /// <summary>Already-finished run: a no-op, not an error - the canceller is never even called.</summary>
    [Fact]
    public async Task Cancel_IsANoOpOnAnAlreadyTerminalRun()
    {
        var runId = RunIdFor(Tenant);
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var runs = new FakeRunStatusReader(new InsightsRunStatus(runId, "complete", "complete", 7, 7, null));
        var canceller = new FakeRunCanceller();

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs, canceller: canceller);
        var response = await client.PostAsync($"/api/insights/runs/{runId}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.Empty(canceller.Calls);
    }

    /// <summary>A running run: the canceller is invoked for exactly this runId, and success is reported.</summary>
    [Fact]
    public async Task Cancel_CallsTheCancellerForARunningRun()
    {
        var runId = RunIdFor(Tenant);
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var runs = new FakeRunStatusReader(Running(runId));
        var canceller = new FakeRunCanceller(cancelledResult: true);

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs, canceller: canceller);
        var response = await client.PostAsync($"/api/insights/runs/{runId}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("cancelled").GetBoolean());

        var call = Assert.Single(canceller.Calls);
        Assert.Equal(runId, call.RunId);
    }

    /// <summary>
    /// [ADDED 2026-09-24] GET /api/insights/requests/{reqId}/stream now carries real per-dimension
    /// detail, not just the aggregate rollup - a caller polling one reqId can learn every sibling
    /// dimension's runId/reportId without keeping its own separate mapping from the original POST
    /// response. Two real sibling runs, both already complete, each its own dimension/reportId.
    /// </summary>
    [Fact]
    public async Task RequestStream_ReturnsRealPerDimensionDetail_ForEveryRunUnderTheReqId()
    {
        var reqId = Guid.NewGuid();
        var riskRunId = InsightsRunId.For(Tenant, "tenant", "dimension_selection", "period-risk");
        var natureRunId = InsightsRunId.For(Tenant, "tenant", "dimension_selection", "period-nature");

        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var requests = new FakeReportRequestRepository();
        await requests.SaveAsync(reqId, [riskRunId, natureRunId]);

        var runs = FakeRunStatusReader.PerRunId(runId => runId switch
        {
            _ when runId == riskRunId => new InsightsRunStatus(riskRunId, "complete", "complete", 7, 7, null, ReportId: "report-risk", Dimension: "Risk"),
            _ when runId == natureRunId => new InsightsRunStatus(natureRunId, "complete", "complete", 7, 7, null, ReportId: "report-nature", Dimension: "Nature"),
            _ => null,
        });

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs, requests: requests);

        var response = await client.GetAsync($"/api/insights/requests/{reqId}/stream");
        var body = await response.Content.ReadAsStringAsync();
        var frame = Assert.Single(body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries));

        using var json = JsonDocument.Parse(frame.Substring("data: ".Length));
        Assert.Equal("completed", json.RootElement.GetProperty("status").GetString());

        var reports = json.RootElement.GetProperty("reports").EnumerateArray().ToList();
        Assert.Equal(2, reports.Count);

        var risk = reports.Single(r => r.GetProperty("runId").GetString() == riskRunId);
        Assert.Equal("Risk", risk.GetProperty("dimension").GetString());
        Assert.Equal("complete", risk.GetProperty("status").GetString());
        Assert.Equal("report-risk", risk.GetProperty("reportId").GetString());

        var nature = reports.Single(r => r.GetProperty("runId").GetString() == natureRunId);
        Assert.Equal("Nature", nature.GetProperty("dimension").GetString());
        Assert.Equal("report-nature", nature.GetProperty("reportId").GetString());
    }

    /// <summary>
    /// The aggregate can sit on "in_progress" for its whole lifetime while individual siblings
    /// finish underneath it - real gap this session fixed (was comparing only the bare aggregate
    /// string for "emit on change"). While Nature is still running, the FIRST frame must already
    /// show Risk's real reportId even though the rollup status itself is not terminal yet (the
    /// stream keeps polling - it can only close once the whole batch reaches a terminal state, so
    /// this fake has Nature complete on its second read, matching the real 2-second poll tick).
    /// </summary>
    [Fact]
    public async Task RequestStream_StaysInProgressOverall_ButStillReportsTheSiblingThatFinished()
    {
        var reqId = Guid.NewGuid();
        var riskRunId = InsightsRunId.For(Tenant, "tenant", "dimension_selection", "period-risk-2");
        var natureRunId = InsightsRunId.For(Tenant, "tenant", "dimension_selection", "period-nature-2");

        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var requests = new FakeReportRequestRepository();
        await requests.SaveAsync(reqId, [riskRunId, natureRunId]);

        var natureReads = 0;
        var runs = FakeRunStatusReader.PerRunId(runId =>
        {
            if (runId == riskRunId)
                return new InsightsRunStatus(riskRunId, "complete", "complete", 7, 7, null, ReportId: "report-risk", Dimension: "Risk");
            if (runId == natureRunId)
            {
                natureReads++;
                // First read (the initial frame): still running. Every read after (the real
                // poll loop, 2s ticks) - complete, so the stream reaches a terminal aggregate
                // and actually closes instead of hanging for MaxStreamDuration.
                return natureReads == 1
                    ? new InsightsRunStatus(natureRunId, "running", "narrating", 4, 7, null, Dimension: "Nature")
                    : new InsightsRunStatus(natureRunId, "complete", "complete", 7, 7, null, ReportId: "report-nature", Dimension: "Nature");
            }
            return null;
        });

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, runs, requests: requests);

        var response = await client.GetAsync($"/api/insights/requests/{reqId}/stream");
        var body = await response.Content.ReadAsStringAsync();
        var frames = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(frames.Length >= 2, $"Expected at least 2 frames (initial in_progress + final completed), got {frames.Length}");

        using var initial = JsonDocument.Parse(frames[0].Substring("data: ".Length));
        Assert.Equal("in_progress", initial.RootElement.GetProperty("status").GetString());
        var risk = initial.RootElement.GetProperty("reports").EnumerateArray().Single(r => r.GetProperty("runId").GetString() == riskRunId);
        Assert.Equal("complete", risk.GetProperty("status").GetString());
        Assert.Equal("report-risk", risk.GetProperty("reportId").GetString());

        using var final = JsonDocument.Parse(frames[^1].Substring("data: ".Length));
        Assert.Equal("completed", final.RootElement.GetProperty("status").GetString());
        var nature = final.RootElement.GetProperty("reports").EnumerateArray().Single(r => r.GetProperty("runId").GetString() == natureRunId);
        Assert.Equal("report-nature", nature.GetProperty("reportId").GetString());
    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expected)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
