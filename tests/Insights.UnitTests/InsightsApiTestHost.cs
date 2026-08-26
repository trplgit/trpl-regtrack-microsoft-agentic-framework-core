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
        IRunStatusReader? runs = null,
        IScopeRepository? scope = null,
        IInsightsRunEnqueuer? enqueuer = null,
        IReportContentService? content = null)
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
                if (scope is not null)
                    services.AddSingleton(scope);
                if (enqueuer is not null)
                    services.AddSingleton(enqueuer);
                if (content is not null)
                    services.AddSingleton(content);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapInsightsTenantEndpoints();
                    endpoints.MapInsightsRunEndpoints();
                    endpoints.MapInsightsReportContentEndpoints();
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

/// <summary>Answers with a fixed scope-pair count, and RECORDS what it was asked - same reasoning as FakeTenantDirectory.</summary>
internal sealed class FakeScopeRepository(int scopePairCount) : IScopeRepository
{
    public List<(int UserId, int CustomerId)> Calls { get; } = [];

    public Task<IReadOnlyList<ScopePair>> GetScopePairsAsync(int userId, int customerId, CancellationToken cancellationToken = default)
    {
        Calls.Add((userId, customerId));
        IReadOnlyList<ScopePair> pairs = Enumerable.Range(0, scopePairCount)
            .Select(i => new ScopePair(i, i))
            .ToList();
        return Task.FromResult(pairs);
    }

    public Task<ScopeClassification> ClassifyScopeAsync(int userId, int customerId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not needed by the endpoints under test.");

    public Task<ScopeAuditResult> AuditScopeAsync(int userId, int customerId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not needed by the endpoints under test.");

    public Task<IReadOnlyList<ScopelessUser>> FindScopelessUsersAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not needed by the endpoints under test.");
}

/// <summary>Answers with a fixed result (or null, for "not visible"), and RECORDS what it was asked - same reasoning as the other fakes.</summary>
internal sealed class FakeReportContentService(ReportContentResult? result) : IReportContentService
{
    public List<(Guid ReportId, int TenantId, int ViewerUserId)> Calls { get; } = [];

    public Task<ReportContentResult?> OpenAsync(Guid reportId, int tenantId, int viewerUserId, CancellationToken cancellationToken = default)
    {
        Calls.Add((reportId, tenantId, viewerUserId));
        return Task.FromResult(result);
    }
}

/// <summary>Records what it was asked to enqueue and hands back a fixed run id, never touching a real task hub.</summary>
internal sealed class FakeRunEnqueuer(string runIdToReturn) : IInsightsRunEnqueuer
{
    public List<(int TenantId, string ReportType, InsightsScopeRequest Scope, string Period, int UserId, LlmCallPriority Priority)> Calls { get; } = [];

    public Task<string> EnqueueAsync(
        int tenantId, string reportType, InsightsScopeRequest scope, string period, int userId,
        CancellationToken cancellationToken = default, LlmCallPriority priority = LlmCallPriority.Interactive)
    {
        Calls.Add((tenantId, reportType, scope, period, userId, priority));
        return Task.FromResult(runIdToReturn);
    }
}
