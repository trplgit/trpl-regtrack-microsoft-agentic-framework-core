using System.Net;
using System.Text.Json;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// GET /api/insights/tenants (API_CONTRACTS.md 1) - the IDOR guard's public face.
/// </summary>
public sealed class TenantEndpointsTests
{
    [Fact]
    public async Task Tenants_ReturnsTheContractShape()
    {
        var directory = new FakeTenantDirectory(
            new EligibleTenant(1490, "Acme Holdings", EntitlementTier.Paid, ScopeClass.TenantWide),
            new EligibleTenant(2684, "Beta Foods", EntitlementTier.Free, ScopeClass.Functional));

        var client = await InsightsApiTestHost.StartAsync(callerUserId: 38, directory);

        var response = await client.GetAsync("/api/insights/tenants");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var tenants = json.RootElement.GetProperty("tenants");

        Assert.Equal(2, tenants.GetArrayLength());

        var first = tenants[0];
        Assert.Equal(1490, first.GetProperty("tenantId").GetInt32());
        Assert.Equal("Acme Holdings", first.GetProperty("name").GetString());
        Assert.Equal("pro", first.GetProperty("tier").GetString());
        Assert.Equal("tenant_wide", first.GetProperty("scopeClass").GetString());

        var second = tenants[1];
        Assert.Equal("basic", second.GetProperty("tier").GetString());
        Assert.Equal("functional", second.GetProperty("scopeClass").GetString());
    }

    /// <summary>
    /// The eligible set must be derived from the AUTHENTICATED user, never from anything on the
    /// request. This pins that the endpoint asks about the caller and not, say, a query value.
    /// </summary>
    [Fact]
    public async Task Tenants_AsksAboutTheAuthenticatedCaller()
    {
        var directory = new FakeTenantDirectory();
        var client = await InsightsApiTestHost.StartAsync(callerUserId: 38, directory);

        await client.GetAsync("/api/insights/tenants?userId=99&customerId=1490");

        var call = Assert.Single(directory.Calls);
        Assert.Equal(38, call.UserId);
    }

    /// <summary>
    /// Nothing eligible is a 200 with an empty array - the client secure-deny state (5.6.3).
    /// A 403 here would make "no Insights yet" indistinguishable from "something broke", and the
    /// user would be told they were denied when in fact they were never granted anything.
    /// </summary>
    [Fact]
    public async Task Tenants_ReturnsAnEmptyListRatherThanAnError()
    {
        var client = await InsightsApiTestHost.StartAsync(callerUserId: 38, new FakeTenantDirectory());

        var response = await client.GetAsync("/api/insights/tenants");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, json.RootElement.GetProperty("tenants").GetArrayLength());
    }
}
