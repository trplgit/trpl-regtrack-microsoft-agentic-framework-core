using System.Net;
using System.Net.Http.Json;
using System.Text;
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
        new(tenantId, new InsightsScopeRequest("tenant", null), "FY2025-26", "compliance_health");

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
    /// [ADDED 2026-09-11] The fan-out umbrella id - every unit that actually queued gets grouped
    /// under one reqId in IReportRequestRepository, so the frontend can poll ONE thing for combined
    /// progress (GET /api/insights/requests/{reqId}/stream) instead of tracking N runIds itself.
    /// </summary>
    [Fact]
    public async Task Generate_ReturnsAReqId_AndSavesTheQueuedRunIdUnderIt()
    {
        const string runId = "insights-1490-abc123";
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer(runId);
        var requests = new FakeReportRequestRepository();

        var client = await InsightsApiTestHost.StartAsync(
            Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown(), requests: requests);

        var response = await client.PostAsJsonAsync("/api/insights/reports", Request());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var reqId = json.RootElement.GetProperty("reqId").GetGuid();

        var saveCall = Assert.Single(requests.SaveCalls);
        Assert.Equal(reqId, saveCall.ReqId);
        Assert.Equal([runId], saveCall.RunIds);

        // [ADDED 2026-09-14] The SAME reqId returned to the caller and saved into the grouping
        // table must also reach the orchestration input (InsightsReportOrchestrationInput.ReqId,
        // via IInsightsRunEnqueuer.EnqueueAsync's reqId param) - that is what lets
        // LangfuseSessionTaggingChatClient tag every real LLM call this run makes with it.
        var enqueueCall = Assert.Single(enqueuer.Calls);
        Assert.Equal(reqId.ToString(), enqueueCall.ReqId);
    }

    /// <summary>
    /// [BUG FOUND LIVE, 2026-09-11] The write-capable DB account was granted GeneratedReport/
    /// InsightsTenantTokenUsage only, before InsightsReportRequest existed - this INSERT 500'd
    /// the ENTIRE generate call (report already genuinely enqueued) until the grant catches up.
    /// The reqId grouping is a convenience on top of real, already-enqueued reports; its own
    /// failure must never take those down with it.
    /// </summary>
    [Fact]
    public async Task Generate_StillReturns202AndTheRealReports_WhenSavingTheReqIdGroupingFails()
    {
        const string runId = "insights-1490-abc123";
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer(runId);
        var requests = new FakeReportRequestRepository { ThrowOnSave = true };

        var client = await InsightsApiTestHost.StartAsync(
            Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown(), requests: requests);

        var response = await client.PostAsJsonAsync("/api/insights/reports", Request());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("reqId", out _));
        var reports = json.RootElement.GetProperty("reports");
        Assert.Equal(runId, reports[0].GetProperty("runId").GetString());
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
            Tenant, new InsightsScopeRequest("tenant", null), "FY2025-26", "dimension_selection",
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
            Tenant, new InsightsScopeRequest("tenant", null), "FY2025-26", "dimension_selection",
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
    /// [ADDED 2026-09-15] The frontend does not send ReportType at all - it only ever sends
    /// RequestedDimensions. A non-empty list with no ReportType must still fan out as
    /// dimension_selection, exactly as if the caller had named it explicitly.
    /// </summary>
    [Fact]
    public async Task Generate_OmittedReportType_WithDimensions_InfersDimensionSelection()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-inferred");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

        var request = new GenerateReportRequest(
            Tenant, new InsightsScopeRequest("tenant", null), "FY2025-26",
            RequestedDimensions: ["Location", "Act"]);

        var response = await client.PostAsJsonAsync("/api/insights/reports", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, enqueuer.Calls.Count);
        Assert.All(enqueuer.Calls, call => Assert.Equal("dimension_selection", call.ReportType));
        Assert.Contains(enqueuer.Calls, call => call.RequestedDimensions is ["Location"]);
        Assert.Contains(enqueuer.Calls, call => call.RequestedDimensions is ["Act"]);
    }

    /// <summary>
    /// [ADDED 2026-09-15] Same omission, but with no RequestedDimensions either - the only signal
    /// the frontend sends is silence, which must mean the full fixed_holistic report, not a
    /// refusal and not an empty dimension_selection fan-out.
    /// </summary>
    [Fact]
    public async Task Generate_OmittedReportType_WithNoDimensions_InfersFixedHolistic()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-inferred-fixed");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

        var request = new GenerateReportRequest(Tenant, new InsightsScopeRequest("tenant", null), "FY2025-26");

        var response = await client.PostAsJsonAsync("/api/insights/reports", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var call = Assert.Single(enqueuer.Calls);
        Assert.Equal(FixedHolisticComposition.ReportType, call.ReportType);
    }

    /// <summary>
    /// [ADDED 2026-09-15] The typed tests above post a C# object where ReportType serializes as
    /// `"reportType": null` - proves the server accepts an explicit null. This test sends the
    /// raw JSON body the frontend will actually send: the key OMITTED entirely, not present as
    /// null. Both must work; only this one proves it for a truly absent key.
    /// </summary>
    [Fact]
    public async Task Generate_RawJsonWithReportTypeKeyEntirelyAbsent_InfersFixedHolistic()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-rawjson");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

        var rawJson = $$"""{"tenantId": {{Tenant}}, "scope": {"type": "tenant"}, "period": "FY2025-26"}""";
        using var content = new StringContent(rawJson, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/insights/reports", content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var call = Assert.Single(enqueuer.Calls);
        Assert.Equal(FixedHolisticComposition.ReportType, call.ReportType);
    }

    /// <summary>
    /// [ADDED 2026-09-15] An explicit ReportType still wins over inference - a caller (or a
    /// future report type) that wants to override what RequestedDimensions alone would imply
    /// must still be able to.
    /// </summary>
    [Fact]
    public async Task Generate_ExplicitReportType_OverridesInference()
    {
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer("insights-1490-explicit");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

        // RequestedDimensions is non-empty (would infer dimension_selection) but ReportType is
        // explicitly fixed_holistic - the explicit value must win, matching Entity's own
        // redirect precedent of "resolved" always beating "requested".
        var request = new GenerateReportRequest(
            Tenant, new InsightsScopeRequest("tenant", null), "FY2025-26",
            FixedHolisticComposition.ReportType, RequestedDimensions: ["Location"]);

        var response = await client.PostAsJsonAsync("/api/insights/reports", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var call = Assert.Single(enqueuer.Calls);
        Assert.Equal(FixedHolisticComposition.ReportType, call.ReportType);
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
            Tenant, new InsightsScopeRequest("tenant", null), "FY2025-26", "dimension_selection",
            RequestedDimensions: ["Nature"]);
        var actRequest = new GenerateReportRequest(
            Tenant, new InsightsScopeRequest("tenant", null), "FY2025-26", "dimension_selection",
            RequestedDimensions: ["Act"]);

        await client.PostAsJsonAsync("/api/insights/reports", natureRequest);
        await client.PostAsJsonAsync("/api/insights/reports", actRequest);

        Assert.Equal(2, cooldown.Calls.Count);
        Assert.Equal(2, enqueuer.Calls.Count);
        // Same caller-supplied period ("FY2025-26") for both - the enqueuer's effective period still
        // differs per dimension (unchanged, real fix from 2026-09-09). The cooldown check itself
        // [REDESIGNED 2026-09-25] no longer looks at period at all - it is keyed on the raw
        // dimension name directly, which is what must differ here instead.
        Assert.NotEqual(enqueuer.Calls[0].Period, enqueuer.Calls[1].Period);
        Assert.Equal("Nature", cooldown.Calls[0].Dimension);
        Assert.Equal("Act", cooldown.Calls[1].Dimension);
    }

    /// <summary>
    /// API_CONTRACTS.md §3 step 3 / design doc Sec.2.4's cooldown.
    /// [ADDED 2026-09-08] The check itself was a [KNOWN LIMITATION] until build order item 14
    /// (GeneratedReport persistence) shipped; these are its first tests. Keyed to
    /// (scope, reportType, dimension) [REDESIGNED 2026-09-25, was period], NOT to the caller.
    /// Request() below is a non-dimension_selection type (compliance_health) - dimension is null,
    /// there being only one unit for that report type.
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
        Assert.Null(call.Dimension);
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
        // [REDESIGNED 2026-09-25] The real dimension name is passed directly now - only "Act" is closed.
        var cooldown = new FakeCooldownRepository(dimension =>
            string.Equals(dimension, "Act", StringComparison.OrdinalIgnoreCase)
                ? new CooldownResult(false, nextAvailable)
                : new CooldownResult(true, null));
        var enqueuer = new FakeRunEnqueuer("insights-1490-fanout");

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: cooldown);

        var request = new GenerateReportRequest(
            Tenant, new InsightsScopeRequest("tenant", null), "FY2025-26", "dimension_selection",
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
            Tenant, new InsightsScopeRequest("tenant", null), "FY2025-26", "dimension_selection",
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
            Tenant, new InsightsScopeRequest("tenant", null), "FY2025-26", FixedHolisticComposition.ReportType);

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
            Tenant, new InsightsScopeRequest("tenant", null), "FY2025-26", "dimension_selection",
            RequestedDimensions: ["Location", "Nature", "Act"]);

        var response = await client.PostAsJsonAsync("/api/insights/reports", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(3, cooldown.Calls.Count);
        Assert.Equal(3, enqueuer.Calls.Count);
    }

    /// <summary>
    /// [ADDED 2026-09-25] The real missing piece flagged since 2026-09-24: a recognised period
    /// keyword now actually reaches the enqueuer as a concrete window, not just the free-text
    /// Period string it always was. ReportPeriodResolver's own resolution is trusted here (already
    /// covered by ReportPeriodResolverTests) - this test only pins that RunEndpoints actually calls
    /// it and forwards the result.
    /// </summary>
    [Fact]
    public async Task Generate_PeriodIsARecognisedKeyword_ResolvesAndForwardsARealWindow()
    {
        const string runId = "insights-1490-window";
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer(runId);

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

        var response = await client.PostAsJsonAsync("/api/insights/reports", Request() with { Period = "last_30_days" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var call = Assert.Single(enqueuer.Calls);
        var expected = ReportPeriodResolver.Resolve(ReportPeriodChoice.Last30Days, DateTime.UtcNow);
        Assert.Equal(expected.StartInclusive, call.WindowStart);
        Assert.Equal(expected.EndExclusive, call.WindowEnd);
        // Period itself is unchanged - the window is ADDITIVE, never a replacement of its other job
        // (the cooldown/run-id key, GeneratedReport.Period storage).
        Assert.Equal("last_30_days", call.Period);
    }

    /// <summary>
    /// Free text (every existing caller's Period value, including the default "FY2025-26" this
    /// whole file's Request() helper already uses) must resolve to NO window - preserving exactly
    /// today's behaviour for every dimension, not silently defaulting to a guessed period
    /// (CLAUDE.md's "fail closed, never guess" non-negotiable).
    /// </summary>
    [Fact]
    public async Task Generate_PeriodIsFreeText_ForwardsNoWindow_PreservingExistingBehaviour()
    {
        const string runId = "insights-1490-nowindow";
        var directory = new FakeTenantDirectory(Eligible(Tenant));
        var scope = new FakeScopeRepository(scopePairCount: 3);
        var enqueuer = new FakeRunEnqueuer(runId);

        var client = await InsightsApiTestHost.StartAsync(Caller, directory, scope: scope, enqueuer: enqueuer, cooldown: OpenCooldown());

        var response = await client.PostAsJsonAsync("/api/insights/reports", Request()); // Period = "FY2025-26"

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var call = Assert.Single(enqueuer.Calls);
        Assert.Null(call.WindowStart);
        Assert.Null(call.WindowEnd);
    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expected)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
