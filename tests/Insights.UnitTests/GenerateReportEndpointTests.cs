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

    /// <summary>
    /// [UPDATED 2026-09-11] The response is now ALWAYS <c>{ "reports": [...] }</c>, even for a
    /// plain single-report request like this one (product decision: one wire shape, a client never
    /// special-cases "was this a list or a single object" - see RunEndpoints.cs's own doc comment
    /// on the fan-out).
    /// </summary>
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
        var reports = json.RootElement.GetProperty("reports");
        Assert.Equal(1, reports.GetArrayLength());
        var report = reports[0];
        Assert.Equal(runId, report.GetProperty("runId").GetString());
        Assert.Equal("queued", report.GetProperty("status").GetString());
        Assert.Equal($"/api/insights/runs/{runId}/stream", report.GetProperty("streamUrl").GetString());
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
    /// [REWRITTEN 2026-09-11] Product decision: picking several dimensions no longer produces one
    /// combined multi-section report - it produces one INDEPENDENT report PER dimension, fanned
    /// out in RunEndpoints.cs before anything is enqueued (see that file's own doc comment). What
    /// used to be a single enqueue call carrying RequestedDimensions=["Nature","Act"] is now TWO
    /// separate enqueue calls, one per dimension, each with a single-element RequestedDimensions
    /// list - proving the fan-out actually happens, not just that the list is threaded through.
    /// </summary>
    [Fact]
    public async Task Generate_ForwardsRequestedDimensionsToTheEnqueuer()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-dims");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

        // "Entity" deliberately excluded here - see ReportTypeRouterTests and
        // Generate_EntityRequested_RoutesToFixedHolistic below for that redirect.
        var request = new GenerateReportRequest(
            Tenant, "dimension_selection", new InsightsScopeRequest("tenant", null), "FY2025-26",
            RequestedDimensions: ["Nature", "Act"]);

        var response = await client.PostAsJsonAsync("/api/insights/reports", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, enqueuer.Calls.Count);
        Assert.All(enqueuer.Calls, call => Assert.Equal("dimension_selection", call.ReportType));
        Assert.Contains(enqueuer.Calls, call => call.RequestedDimensions is ["Nature"]);
        Assert.Contains(enqueuer.Calls, call => call.RequestedDimensions is ["Act"]);
    }

    /// <summary>
    /// [ADDED 2026-09-11] Product rule: requesting Entity redirects the whole run to
    /// fixed_holistic - Entity has no finalized dimension_selection template of its own, and the
    /// full 6-tab dashboard is what the designer wants for it instead. See ReportTypeRouter.
    /// </summary>
    [Fact]
    public async Task Generate_EntityRequested_RoutesToFixedHolistic()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-entity");
        var cooldown = OpenCooldown();

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: cooldown);

        var request = new GenerateReportRequest(
            Tenant, "dimension_selection", new InsightsScopeRequest("tenant", null), "FY2025-26",
            RequestedDimensions: ["Entity"]);

        var response = await client.PostAsJsonAsync("/api/insights/reports", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var call = Assert.Single(enqueuer.Calls);
        Assert.Equal("fixed_holistic", call.ReportType);
        Assert.Null(call.RequestedDimensions);
        // The cooldown check must agree with what actually got enqueued, not the caller's
        // original dimension_selection request.
        Assert.Equal("fixed_holistic", Assert.Single(cooldown.Calls).ReportType);
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

        // "Entity" deliberately excluded here (it now redirects to fixed_holistic - see
        // Generate_EntityRequested_RoutesToFixedHolistic) - "Act" exercises the same
        // different-dimensions-different-periods behaviour without tangling with that redirect.
        var natureRequest = new GenerateReportRequest(
            Tenant, "dimension_selection", new InsightsScopeRequest("tenant", null), "FY2025-26",
            RequestedDimensions: ["Nature"]);
        var actRequest = new GenerateReportRequest(
            Tenant, "dimension_selection", new InsightsScopeRequest("tenant", null), "FY2025-26",
            RequestedDimensions: ["Act"]);

        await client.PostAsJsonAsync("/api/insights/reports", natureRequest);
        await client.PostAsJsonAsync("/api/insights/reports", actRequest);

        Assert.Equal(2, cooldown.Calls.Count);
        Assert.Equal(2, enqueuer.Calls.Count);
        // Same caller-supplied period ("FY2025-26") for both - but the effective period actually
        // used for the cooldown key and the enqueue must differ, which is the whole fix.
        Assert.NotEqual(cooldown.Calls[0].Period, cooldown.Calls[1].Period);
        Assert.NotEqual(enqueuer.Calls[0].Period, enqueuer.Calls[1].Period);
        Assert.Contains("nature", cooldown.Calls[0].Period, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("act", cooldown.Calls[1].Period, StringComparison.OrdinalIgnoreCase);
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
    /// [REWRITTEN 2026-09-11] Closed cooldown is now a PER-REPORT status, not a whole-response
    /// refusal - product decision: one dimension on cooldown must not block a caller's OTHER
    /// picks (best-effort, not all-or-nothing - see RunEndpoints.cs's own doc comment). Even for
    /// this single-report request, the response is still 202 Accepted with a one-element
    /// "reports" array whose entry reports "cooldown" - the run is still NEVER enqueued, which
    /// remains the actual point of the check (skip the LLM spend, not just warn about it).
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

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var reports = json.RootElement.GetProperty("reports");
        Assert.Equal(1, reports.GetArrayLength());
        var report = reports[0];
        Assert.Equal("cooldown", report.GetProperty("status").GetString());
        Assert.Equal(nextAvailable, report.GetProperty("nextAvailableUtc").GetDateTime());
        Assert.Empty(enqueuer.Calls);
    }

    /// <summary>
    /// [ADDED 2026-09-11] The core fan-out promise: picking 3 dimensions where one is already on
    /// cooldown must still generate the other 2 - best-effort, not all-or-nothing (explicit
    /// product decision). One 202 response, one array entry per requested dimension, each with
    /// its own independent status.
    /// </summary>
    [Fact]
    public async Task Generate_ThreeDimensionsOneOnCooldown_GeneratesTheOtherTwoAnyway()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var nextAvailable = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        // "Act"'s effective period carries "::dim=act" (ReportDimensionKey) - only that one is closed.
        var cooldown = new FakeCooldownRepository(period =>
            period.Contains("act", StringComparison.OrdinalIgnoreCase)
                ? new CooldownResult(false, nextAvailable)
                : new CooldownResult(true, null));
        var enqueuer = new FakeRunEnqueuer("insights-1490-fanout");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: cooldown);

        var request = new GenerateReportRequest(
            Tenant, "dimension_selection", new InsightsScopeRequest("tenant", null), "FY2025-26",
            RequestedDimensions: ["Location", "Nature", "Act"]);

        var response = await client.PostAsJsonAsync("/api/insights/reports", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var reports = json.RootElement.GetProperty("reports");
        Assert.Equal(3, reports.GetArrayLength());

        var byDimension = reports.EnumerateArray().ToDictionary(r => r.GetProperty("dimension").GetString()!);
        Assert.Equal("queued", byDimension["Location"].GetProperty("status").GetString());
        Assert.Equal("queued", byDimension["Nature"].GetProperty("status").GetString());
        Assert.Equal("cooldown", byDimension["Act"].GetProperty("status").GetString());
        Assert.Equal(nextAvailable, byDimension["Act"].GetProperty("nextAvailableUtc").GetDateTime());

        // Only the 2 clear dimensions actually enqueued - the cooled-down one never did.
        Assert.Equal(2, enqueuer.Calls.Count);
        Assert.Contains(enqueuer.Calls, call => call.RequestedDimensions is ["Location"]);
        Assert.Contains(enqueuer.Calls, call => call.RequestedDimensions is ["Nature"]);
        Assert.DoesNotContain(enqueuer.Calls, call => call.RequestedDimensions is ["Act"]);
    }

    /// <summary>
    /// [ADDED 2026-09-11] The fan-out needs at least one dimension to loop over - an empty list is
    /// now caught HERE, before anything is enqueued, rather than only deep inside the orchestrator
    /// after an enqueue already happened (CLAUDE.md non-negotiable #2 - fail closed, fail loud).
    /// </summary>
    [Fact]
    public async Task Generate_DimensionSelectionWithNoDimensions_RefusesBeforeEnqueueing()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var cooldown = OpenCooldown();
        var enqueuer = new FakeRunEnqueuer("should-not-be-used");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: cooldown);

        var request = new GenerateReportRequest(
            Tenant, "dimension_selection", new InsightsScopeRequest("tenant", null), "FY2025-26",
            RequestedDimensions: []);

        var response = await client.PostAsJsonAsync("/api/insights/reports", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorCodeAsync(response, "NO_DIMENSIONS_REQUESTED");
        Assert.Empty(cooldown.Calls);
        Assert.Empty(enqueuer.Calls);
    }

    /// <summary>
    /// [ADDED 2026-09-11] A plain (non-dimension_selection) request was never a list to split -
    /// exactly one unit, and its "dimension" field is null (there was never a caller-named
    /// dimension for this entry to report back).
    /// </summary>
    [Fact]
    public async Task Generate_FixedHolisticRequest_ReturnsOneReportWithNullDimension()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-fixed");

        var request = new GenerateReportRequest(
            Tenant, FixedHolisticComposition.ReportType, new InsightsScopeRequest("tenant", null), "FY2025-26");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

        var response = await client.PostAsJsonAsync("/api/insights/reports", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var reports = json.RootElement.GetProperty("reports");
        Assert.Equal(1, reports.GetArrayLength());
        Assert.True(reports[0].GetProperty("dimension").ValueKind is JsonValueKind.Null);
        Assert.Equal(FixedHolisticComposition.ReportType, reports[0].GetProperty("reportType").GetString());
        Assert.Single(enqueuer.Calls);
    }

    /// <summary>
    /// [ADDED 2026-09-11] Regression guard for a real bug found live: the fan-out originally ran
    /// each dimension's cooldown check concurrently (Task.WhenAll), which threw against the REAL
    /// EF-backed ICooldownRepository the first time a caller sent 3 dimensions through the actual
    /// dev host - "A second operation was started on this context instance before a previous
    /// operation completed" (EF Core's ConcurrencyDetector; ICooldownRepository is a scoped,
    /// DbContext-backed service, ONE instance per request). FakeCooldownRepository now detects the
    /// same re-entrancy (see its own doc comment) - this test would throw if the fix (sequential
    /// awaits) ever regressed back to concurrent.
    /// </summary>
    [Fact]
    public async Task Generate_MultipleDimensions_ChecksCooldownSequentially_NeverThrowsFromConcurrentReuse()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var cooldown = OpenCooldown();
        var enqueuer = new FakeRunEnqueuer("insights-1490-sequential");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: cooldown);

        var request = new GenerateReportRequest(
            Tenant, "dimension_selection", new InsightsScopeRequest("tenant", null), "FY2025-26",
            RequestedDimensions: ["Location", "Nature", "Act"]);

        var response = await client.PostAsJsonAsync("/api/insights/reports", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(3, cooldown.Calls.Count);
        Assert.Equal(3, enqueuer.Calls.Count);
    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expected)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
