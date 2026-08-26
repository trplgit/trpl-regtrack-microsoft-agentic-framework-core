using Insights.Api;
using Insights.Data;
using Insights.Domain;
using Insights.Worker;
using Insights.Worker.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// Hosts the two built Insights endpoints on an in-memory server and calls them against REAL UAT
/// data - the closest thing to a live smoke test without giving the worker an ingress it is not
/// allowed to have (CLAUDE.md 6: the worker is private; the endpoints belong to the RegTrack API).
///
/// Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~InsightsApiManualRunTests
///
/// Requires (none read from any committed file):
///   ConnectionStrings__RegTrack   the UAT connection string
///   INSIGHTS_USER_ID              the user to impersonate, e.g. 38
/// Optional:
///   INSIGHTS_RUN_ID               a runId from a real paid run, to exercise the stream endpoint
/// </summary>
public sealed class InsightsApiManualRunTests(ITestOutputHelper output)
{
    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test - see the class doc comment.");

    [Fact]
    public async Task Tenants_ReturnsTheRealEligibleSet()
    {
        var userId = int.Parse(RequireEnv("INSIGHTS_USER_ID"));
        using var host = await StartAsync(userId);

        var response = await host.GetTestClient().GetAsync("/api/insights/tenants");
        var body = await response.Content.ReadAsStringAsync();

        output.WriteLine($"GET /api/insights/tenants  ->  {(int)response.StatusCode}");
        output.WriteLine(body);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The IDOR guard, exercised for real: build a well-formed run id for a tenant this user is
    /// NOT eligible for, and confirm the endpoint refuses it. 999999 is chosen because it cannot
    /// be a real customer - so a 403 here proves the check ran rather than that the run was
    /// simply absent.
    /// </summary>
    [Fact]
    public async Task Stream_RefusesATenantTheCallerIsNotEligibleFor()
    {
        var userId = int.Parse(RequireEnv("INSIGHTS_USER_ID"));
        using var host = await StartAsync(userId);

        var forged = InsightsRunId.For(999999, "tenant", "compliance_health", "FY2025-26");
        var response = await host.GetTestClient().GetAsync($"/api/insights/runs/{forged}/stream");

        output.WriteLine($"forged runId -> {(int)response.StatusCode}");
        output.WriteLine(await response.Content.ReadAsStringAsync());

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Streams a REAL run. Set INSIGHTS_RUN_ID to the instance id printed by
    /// InsightsRunOnceWorker; skips itself when that is absent rather than failing, because a
    /// completed run is not something this test can create for itself.
    /// </summary>
    [Fact]
    public async Task Stream_FollowsARealRun()
    {
        var runId = Environment.GetEnvironmentVariable("INSIGHTS_RUN_ID");
        if (string.IsNullOrWhiteSpace(runId))
        {
            output.WriteLine("INSIGHTS_RUN_ID not set - nothing to follow. Start a paid run first.");
            return;
        }

        var userId = int.Parse(RequireEnv("INSIGHTS_USER_ID"));
        using var host = await StartAsync(userId);

        var response = await host.GetTestClient().GetAsync($"/api/insights/runs/{runId}/stream");

        output.WriteLine($"GET stream -> {(int)response.StatusCode}  {response.Content.Headers.ContentType}");
        output.WriteLine(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// THROWAWAY - a real (not in-memory) Kestrel listener on localhost, so all three endpoints
    /// (tenants, generate, stream) can be poked from Postman, PLUS the AG-UI test frontend
    /// (AgUiTestFrontend.cs) at "/" - a real browser page driving the real pipeline through genuine
    /// AG-UI protocol events.
    ///
    /// [TRAP - found live 2026-08-24] The first version of this called AddInsightsOrchestration,
    /// which bundles in AddHostedService&lt;DurableTaskHostedService&gt; - that starts a REAL
    /// TaskHubWorker dequeue loop against the SHARED UAT task hub the moment builder.StartAsync()
    /// runs. A "just let me read run status" listener was, without meaning to, a second live
    /// worker competing on the same queue as whatever else is running against it. Fixed at the
    /// source: AddInsightsOrchestration is now split into AddInsightsOrchestrationClient (used
    /// here - TaskHubClient, IRunStatusReader, IInsightsRunEnqueuer, no dequeue loop) and
    /// AddInsightsOrchestrationWorker (TaskHubWorker + activities + DurableTaskHostedService,
    /// worker-process only). This listener is now structurally incapable of dequeuing a task.
    ///
    /// Listens until the process is killed - run this alone, not as part of a normal test pass. A
    /// real worker (InsightsReportOrchestratorManualRunTests.RunAsync_ProcessesWhateverIsAlreadyQueued
    /// or a full RunAsync_RealTenant_ReachesCompleteStatus run) must be running separately for a
    /// generated run to ever actually progress - this host only enqueues and reads, never processes.
    /// Requires ConnectionStrings__RegTrack, ConnectionStrings__DurableTaskHub, INSIGHTS_USER_ID,
    /// AZURE_BLOB_CONNECTION_STRING (for the AG-UI page's "view report" link).
    /// </summary>
    [Fact]
    public async Task HostForPostmanAsync()
    {
        var userId = int.Parse(RequireEnv("INSIGHTS_USER_ID"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:RegTrack"] = RequireEnv("ConnectionStrings__RegTrack"),
                ["ConnectionStrings:DurableTaskHub"] = RequireEnv("ConnectionStrings__DurableTaskHub"),
                ["Azure:BlobConnectionString"] = RequireEnv("AZURE_BLOB_CONNECTION_STRING"),
                ["Azure:BlobContainer"] = "insights-reports-temp",
                // Item 14 read path (design doc Sec.9.3) - the SAS lifetime for GET .../content.
                ["Reports:SasLifetimeMinutes"] = "10",
            })
            .Build();

        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseKestrel();
            web.UseUrls("http://localhost:5080");
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddLogging();

                // [FIX - found live 2026-08-24] A bare HostBuilder() does not wire the local
                // `configuration` object built above into DI as IConfiguration - it provides its
                // own (near-empty) one. AddInsightsData/AddInsightsOrchestrationClient below both
                // take `configuration` as a direct parameter and never hit this, so they worked
                // fine; AgUiTestFrontend's view endpoint asks for IConfiguration via DI (a normal
                // minimal-API parameter) and got the WRONG, empty one - "ConnectionStrings:RegTrack
                // is not configured" even though it plainly was, right above.
                services.AddSingleton<IConfiguration>(configuration);

                // IScopeRepository, ITenantDirectoryRepository, and friends - all scoped SQL
                // wrappers, no hosted services, safe for an API host.
                services.AddInsightsData(configuration);

                // Client-only orchestration pieces - see the TRAP note above.
                services.AddInsightsOrchestrationClient(configuration);

                // Item 14 read path (design doc Sec.9.3, API_CONTRACTS.md §5) - decrypt, re-auth,
                // mint a view SAS. Same "client-only, no dequeue loop" safety as AddInsightsData/
                // AddInsightsOrchestrationClient above - never registers TaskHubWorker or an activity.
                services.AddInsightsReportContentService(configuration);

                services.AddSingleton<IInsightsCaller>(new FixedCaller(userId));
            });
            web.Configure(app =>
            {
                // TEST-ONLY, kept deliberately: shows the real exception instead of a bare 500. Found
                // OrchestrationAlreadyExistsException this way (2026-08-24) - a bare 500 would have
                // taken far longer to diagnose.
                app.UseDeveloperExceptionPage();
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapInsightsTenantEndpoints();
                    endpoints.MapInsightsRunEndpoints();
                    endpoints.MapInsightsReportContentEndpoints();
                    endpoints.MapAgUiTestEndpoints();
                });
            });
        });

        using var host = await builder.StartAsync();
        output.WriteLine("Listening on http://localhost:5080 - open it in a browser for the AG-UI test page.");
        await host.WaitForShutdownAsync();
    }

    private static async Task<IHost> StartAsync(int callerUserId)
    {
        var connectionString = RequireEnv("ConnectionStrings__RegTrack");

        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();

                /*  Real repository, real SQL, real UAT. The only stub is the caller - RegTrack
                    supplies that from its authenticated principal, and this harness has no auth
                    stack of its own by design.                                                  */
                services.AddSingleton<ITenantDirectoryRepository>(
                    _ => new SqlTenantDirectoryRepository(connectionString));
                services.AddSingleton<IInsightsCaller>(new FixedCaller(callerUserId));

                /*  The stream endpoint needs a status reader. The DTFx-backed one needs the whole
                    orchestration stack; this returns null, which is enough to exercise the
                    authorisation path - the part that can actually leak.                        */
                services.AddSingleton<IRunStatusReader>(new NullRunStatusReader());
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapInsightsTenantEndpoints();
                    endpoints.MapInsightsRunEndpoints();
                });
            });
        });

        return await builder.StartAsync();
    }

    private sealed class FixedCaller(int userId) : IInsightsCaller
    {
        public int UserId { get; } = userId;
    }

    private sealed class NullRunStatusReader : IRunStatusReader
    {
        public Task<InsightsRunStatus?> GetStatusAsync(string runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<InsightsRunStatus?>(null);
    }
}
