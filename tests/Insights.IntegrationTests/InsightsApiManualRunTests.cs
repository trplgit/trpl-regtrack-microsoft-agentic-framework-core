using Insights.Api;
using Insights.Data;
using Insights.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
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
