using Insights.Api;
using Insights.Data;
using Insights.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Insights.UnitTests;

/// <summary>
/// Spins the Insights endpoints up on an in-memory server so they can be exercised through real
/// routing and real status codes.
///
/// Worth the extra moving part: the thing most likely to be wrong in these endpoints is the
/// authorisation ORDER - checking eligibility after fetching, or returning a 404 before the
/// eligibility check - and neither shows up when you unit-test the helpers they call.
/// </summary>
internal static class InsightsApiTestHost
{
    public static async Task<HttpClient> StartAsync(
        int callerUserId,
        ITenantDirectoryRepository tenants,
        IRunStatusReader? runs = null)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IInsightsCaller>(new FakeCaller(callerUserId));
                services.AddSingleton(tenants);
                if (runs is not null)
                    services.AddSingleton(runs);
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

        var host = await builder.StartAsync();
        return host.GetTestClient();
    }

    private sealed class FakeCaller(int userId) : IInsightsCaller
    {
        public int UserId { get; } = userId;
    }
}

/// <summary>
/// A tenant directory that answers from a fixed list, and RECORDS what it was asked.
/// The recording is the point: several tests assert not just the response but that the
/// eligibility question was actually put, for the right user and the right tenant.
/// </summary>
internal sealed class FakeTenantDirectory(params EligibleTenant[] eligible) : ITenantDirectoryRepository
{
    public List<(int UserId, int? CustomerId)> Calls { get; } = [];

    public Task<IReadOnlyList<EligibleTenant>> GetEligibleTenantsAsync(int userId, CancellationToken cancellationToken = default)
    {
        Calls.Add((userId, null));
        return Task.FromResult<IReadOnlyList<EligibleTenant>>(eligible);
    }

    public Task<EligibleTenant?> IsEligibleAsync(int userId, int customerId, CancellationToken cancellationToken = default)
    {
        Calls.Add((userId, customerId));
        return Task.FromResult(eligible.FirstOrDefault(t => t.TenantId == customerId));
    }
}

internal sealed class FakeRunStatusReader(InsightsRunStatus? status) : IRunStatusReader
{
    public int CallCount { get; private set; }

    public Task<InsightsRunStatus?> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(status);
    }
}
