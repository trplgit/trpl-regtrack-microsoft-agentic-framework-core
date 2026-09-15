using System.Net;
using System.Text.Json;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// GET /api/insights/reports/{reportId}/content?tenantId={id} (API_CONTRACTS.md §5). The real
/// re-authorisation logic lives in ReportContentServiceTests - these pin that the ENDPOINT wires
/// authorisation in the right order (eligibility before ever touching the content service) and
/// maps the service's null/non-null result to the right status codes, matching RunEndpointsTests'
/// reasoning for the /stream endpoint.
/// </summary>
public sealed class ReportContentEndpointTests
{
    private const int Tenant = 1490;
    private const int Caller = 38;

    private static EligibleTenant Eligible(int tenantId) =>
        new(tenantId, "Acme Holdings", EntitlementTier.Paid, ScopeClass.TenantWide);

    private static ReportContentResult Visible() =>
        new(new Uri("https://blob.example/views/abc.html?sv=sig"), DateTimeOffset.UtcNow.AddMinutes(10));

    [Fact]
    public async Task Content_RefusesATenantTheCallerIsNotEligibleFor_WithoutTouchingTheContentService()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var content = new FakeReportContentService(Visible());
        var client = await InsightsApiTestHost.StartAsync(Caller, directory, content: content);

        var response = await client.GetAsync($"/api/insights/reports/{Guid.NewGuid()}/content?tenantId=9999");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorCodeAsync(response, "TENANT_NOT_ELIGIBLE");
        Assert.Empty(content.Calls);
    }

    [Fact]
    public async Task Content_ReturnsNotFound_WhenTheContentServiceRefuses()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var content = new FakeReportContentService(result: null);
        var client = await InsightsApiTestHost.StartAsync(Caller, directory, content: content);

        var response = await client.GetAsync($"/api/insights/reports/{Guid.NewGuid()}/content?tenantId={Tenant}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorCodeAsync(response, "REPORT_NOT_VISIBLE");
    }

    [Fact]
    public async Task Content_ReturnsTheContractShape_WhenEligibleAndVisible()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var visible = Visible();
        var content = new FakeReportContentService(visible);
        var client = await InsightsApiTestHost.StartAsync(Caller, directory, content: content);

        var response = await client.GetAsync($"/api/insights/reports/{Guid.NewGuid()}/content?tenantId={Tenant}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(visible.ContentUrl.ToString(), json.RootElement.GetProperty("contentUrl").GetString());
        Assert.True(json.RootElement.GetProperty("sandboxRequired").GetBoolean());
        Assert.True(json.RootElement.TryGetProperty("expiresUtc", out _));
    }

    /// <summary>The viewer identity passed to the content service must be the AUTHENTICATED caller, never anything the client supplies.</summary>
    [Fact]
    public async Task Content_PassesTheAuthenticatedCallerAsTheViewer()
    {
        var reportId = Guid.NewGuid();
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var content = new FakeReportContentService(Visible());
        var client = await InsightsApiTestHost.StartAsync(Caller, directory, content: content);

        await client.GetAsync($"/api/insights/reports/{reportId}/content?tenantId={Tenant}");

        var call = Assert.Single(content.Calls);
        Assert.Equal(reportId, call.ReportId);
        Assert.Equal(Tenant, call.TenantId);
        Assert.Equal(Caller, call.ViewerUserId);
    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expected)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
