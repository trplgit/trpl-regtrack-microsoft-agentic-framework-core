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
        Assert.Equal(7, json.RootElement.GetProperty("stagesComplete").GetInt32());
        Assert.Equal(7, json.RootElement.GetProperty("stagesTotal").GetInt32());
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

        var body = await (await client.GetAsync($"/api/insights/runs/{runId}/stream")).Content.ReadAsStringAsync();

        Assert.Contains("failed", body);
        Assert.DoesNotContain("reconciliation", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("variance", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expected)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
