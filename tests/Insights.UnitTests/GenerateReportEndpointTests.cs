using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Insights.Api;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>POST /api/insights/reports (API_CONTRACTS.md §3).</summary>
public sealed class GenerateReportEndpointTests
{
    private const int Tenant = 1490;
    private const int Caller = 38;

    private static EligibleTenant Eligible(int tenantId) =>
        new(tenantId, "Acme Holdings", EntitlementTier.Paid, ScopeClass.TenantWide);

    private static GenerateReportRequest Request(int tenantId = Tenant) =>
        new(tenantId, "compliance_health", new InsightsScopeRequest("tenant", null), "FY2025-26");

    /// <summary>Same ordering rule as the stream endpoint: refuse before touching scope or the enqueuer.</summary>
    [Fact]
    public async Task Generate_RefusesATenantTheCallerIsNotEligibleFor()
    {
        var directory = new FakeTenantDirectory(Eligible(9999));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-fake");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer);

        var response = await client.PostAsJsonAsync("/api/insights/reports", Request());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorCodeAsync(response, "TENANT_NOT_ELIGIBLE");
        Assert.Empty(scope.Calls);
        Assert.Empty(enqueuer.Calls);
    }

    /// <summary>Entitled to the tenant, but nothing in scope: a distinct refusal, and never a silently empty report.</summary>
    [Fact]
    public async Task Generate_RefusesWhenTheCallerHasNoScopeInTheTenant()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 0);
        var enqueuer = new FakeRunEnqueuer("insights-1490-fake");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer);

        var response = await client.PostAsJsonAsync("/api/insights/reports", Request());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorCodeAsync(response, "SCOPE_DENIED");
        Assert.Empty(enqueuer.Calls);
    }

    [Fact]
    public async Task Generate_EnqueuesAndReturns202ForAnEligibleScopedCaller()
    {
        const string runId = "insights-1490-abc123";
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer(runId);

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer);

        var response = await client.PostAsJsonAsync("/api/insights/reports", Request());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(runId, json.RootElement.GetProperty("runId").GetString());
        Assert.Equal("queued", json.RootElement.GetProperty("status").GetString());
        Assert.Equal($"/api/insights/runs/{runId}/stream", json.RootElement.GetProperty("streamUrl").GetString());
    }

    /// <summary>
    /// The enqueued userId is the AUTHENTICATED caller, never anything from the request body - the
    /// contract's POST body has no userId field at all, so this is enforced by construction, but
    /// pin it anyway: a future body change must not accidentally reopen the IDOR the rest of this
    /// feature is built to close.
    /// </summary>
    [Fact]
    public async Task Generate_EnqueuesUsingTheAuthenticatedCallerAsTheUserId()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-abc123");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer);

        await client.PostAsJsonAsync("/api/insights/reports", Request());

        var call = Assert.Single(enqueuer.Calls);
        Assert.Equal(Caller, call.UserId);
        Assert.Equal(Tenant, call.TenantId);
        Assert.Equal("compliance_health", call.ReportType);
        Assert.Equal("FY2025-26", call.Period);
        // A human clicked Generate - this is the paid_interactive lane, always drains first
        // (design doc Sec.4.4). RunEndpoints.cs never sets this explicitly; it relies on
        // EnqueueAsync's default, which is exactly what this pins.
        Assert.Equal(Insights.Domain.LlmCallPriority.Interactive, call.Priority);
    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expected)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
