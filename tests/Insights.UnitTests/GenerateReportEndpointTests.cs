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

    /// <summary>Every test below reaches step 3 (or later), so every test needs a cooldown fake -
    /// minimal API resolves every [FromServices] parameter for the matched handler up front,
    /// regardless of which early-exit branch the request actually takes. Open by default; the
    /// cooldown-specific tests below override it.</summary>
    private static FakeCooldownRepository OpenCooldown() => new(new CooldownResult(true, null));

    /// <summary>Same ordering rule as the stream endpoint: refuse before touching scope or the enqueuer.</summary>
    [Fact]
    public async Task Generate_RefusesATenantTheCallerIsNotEligibleFor()
    {
        var directory = new FakeTenantDirectory(Eligible(9999));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-fake");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

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
        var cooldown = OpenCooldown();

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: cooldown);

        var response = await client.PostAsJsonAsync("/api/insights/reports", Request());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertErrorCodeAsync(response, "SCOPE_DENIED");
        Assert.Empty(enqueuer.Calls);
        // Step 3 (cooldown) never runs for a request step 2 (scope) already refused.
        Assert.Empty(cooldown.Calls);
    }

    [Fact]
    public async Task Generate_EnqueuesAndReturns202ForAnEligibleScopedCaller()
    {
        const string runId = "insights-1490-abc123";
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer(runId);

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

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

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

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

    /// <summary>
    /// [ADDED 2026-09-09] The multi-dimension report feature's API-side plumbing -
    /// RequestedDimensions on the POST body forwards verbatim to the enqueuer, so a
    /// "dimension_selection" request over the API behaves identically to the CLI's
    /// --Insights:Dimensions flag. Previously this was CLI-only.
    /// </summary>
    [Fact]
    public async Task Generate_ForwardsRequestedDimensionsToTheEnqueuer()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-dims");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

        var request = new GenerateReportRequest(
            Tenant, "dimension_selection", new InsightsScopeRequest("tenant", null), "FY2025-26",
            RequestedDimensions: ["Nature", "Entity"]);

        var response = await client.PostAsJsonAsync("/api/insights/reports", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var call = Assert.Single(enqueuer.Calls);
        Assert.Equal("dimension_selection", call.ReportType);
        Assert.NotNull(call.RequestedDimensions);
        Assert.Equal(["Nature", "Entity"], call.RequestedDimensions);
    }

    /// <summary>
    /// Every other ReportType keeps working exactly as before this field existed - omitting
    /// RequestedDimensions from the request body must forward null, not an empty list (null means
    /// "fetch all fourteen", per InsightsReportOrchestrationInput's own contract).
    /// </summary>
    [Fact]
    public async Task Generate_OmittedRequestedDimensions_ForwardsNull()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-abc123");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

        await client.PostAsJsonAsync("/api/insights/reports", Request());

        var call = Assert.Single(enqueuer.Calls);
        Assert.Null(call.RequestedDimensions);
    }

    /// <summary>
    /// [ADDED 2026-09-09, TEMP WORKAROUND - see ReportDimensionKey] Reproduces, and proves fixed,
    /// the exact complaint: generating Nature must not block Entity for the same tenant/period.
    /// Confirms the effective period (not the caller's literal Period) is what reaches both the
    /// cooldown check and the enqueuer, and that it differs per dimension selection.
    /// </summary>
    [Fact]
    public async Task Generate_DifferentDimensionSelections_UseDifferentEffectivePeriods()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var cooldown = OpenCooldown();
        var enqueuer = new FakeRunEnqueuer("insights-1490-dims");
        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: cooldown);

        var natureRequest = new GenerateReportRequest(
            Tenant, "dimension_selection", new InsightsScopeRequest("tenant", null), "FY2025-26",
            RequestedDimensions: ["Nature"]);
        var entityRequest = new GenerateReportRequest(
            Tenant, "dimension_selection", new InsightsScopeRequest("tenant", null), "FY2025-26",
            RequestedDimensions: ["Entity"]);

        await client.PostAsJsonAsync("/api/insights/reports", natureRequest);
        await client.PostAsJsonAsync("/api/insights/reports", entityRequest);

        Assert.Equal(2, cooldown.Calls.Count);
        Assert.Equal(2, enqueuer.Calls.Count);
        // Same caller-supplied period ("FY2025-26") for both - but the effective period actually
        // used for the cooldown key and the enqueue must differ, which is the whole fix.
        Assert.NotEqual(cooldown.Calls[0].Period, cooldown.Calls[1].Period);
        Assert.NotEqual(enqueuer.Calls[0].Period, enqueuer.Calls[1].Period);
        Assert.Contains("nature", cooldown.Calls[0].Period, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("entity", cooldown.Calls[1].Period, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// API_CONTRACTS.md §3 step 3 / design doc Sec.2.4's 30-day cooldown.
    /// [ADDED 2026-09-08] The check itself was a [KNOWN LIMITATION] until build order item 14
    /// (GeneratedReport persistence) shipped; these are its first tests. Keyed to
    /// (scope, reportType, period), NOT to the caller.
    /// </summary>
    [Fact]
    public async Task Generate_OpenCooldown_ChecksTheExactKeyAndThenEnqueues()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var cooldown = OpenCooldown();
        var enqueuer = new FakeRunEnqueuer("insights-1490-abc123");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: cooldown);

        var response = await client.PostAsJsonAsync("/api/insights/reports", Request());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var call = Assert.Single(cooldown.Calls);
        Assert.Equal(Tenant, call.CustomerId);
        Assert.Equal("compliance_health", call.ReportType);
        Assert.Equal("tenant", call.ScopeDescriptor);
        Assert.Equal("FY2025-26", call.Period);
        Assert.Single(enqueuer.Calls);
    }

    /// <summary>
    /// Closed cooldown => 409 COOLDOWN_ACTIVE with nextAvailableUtc, and the run is NEVER
    /// enqueued - the whole point of the check is to skip the LLM spend, not just warn about it.
    /// </summary>
    [Fact]
    public async Task Generate_ClosedCooldown_RefusesWithoutEnqueueing()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var nextAvailable = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        var cooldown = new FakeCooldownRepository(new CooldownResult(false, nextAvailable));
        var enqueuer = new FakeRunEnqueuer("should-not-be-used");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: cooldown);

        var response = await client.PostAsJsonAsync("/api/insights/reports", Request());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("COOLDOWN_ACTIVE", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(nextAvailable, json.RootElement.GetProperty("nextAvailableUtc").GetDateTime());
        Assert.Empty(enqueuer.Calls);
    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expected)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
