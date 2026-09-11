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
        IReportContentService? content = null,
        ICooldownRepository? cooldown = null,
        IReportRequestRepository? requests = null)
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
                if (cooldown is not null)
                    services.AddSingleton(cooldown);
                services.AddSingleton(requests ?? new FakeReportRequestRepository());
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

internal sealed class FakeRunStatusReader : IRunStatusReader
{
    private readonly Func<string, InsightsRunStatus?> _statusForRunId;

    public FakeRunStatusReader(InsightsRunStatus? status) => _statusForRunId = _ => status;

    private FakeRunStatusReader(Func<string, InsightsRunStatus?> statusForRunId) => _statusForRunId = statusForRunId;

    /// <summary>
    /// Per-runId responses - needed to test the fan-out reqId stream endpoint's aggregation, where
    /// several runIds under one reqId can each be in a DIFFERENT state at the same poll tick. A
    /// named factory, not a public constructor overload: `new FakeRunStatusReader(null)` (every
    /// existing single-status test) would be ambiguous between InsightsRunStatus? and
    /// Func&lt;string, InsightsRunStatus?&gt; - both accept a null literal equally well.
    /// </summary>
    public static FakeRunStatusReader PerRunId(Func<string, InsightsRunStatus?> statusForRunId) => new(statusForRunId);

    public int CallCount { get; private set; }

    public Task<InsightsRunStatus?> GetStatusAsync(string runId, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(_statusForRunId(runId));
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

/// <summary>
/// Answers with a fixed CooldownResult (or, via the second constructor, a per-call result keyed
/// on the effective period - needed to test the fan-out's best-effort behaviour, where different
/// dimensions in the SAME request can land open or closed independently), and RECORDS what it was
/// asked - same reasoning as the other fakes.
///
/// [BUG FOUND LIVE, 2026-09-11] Also DETECTS re-entrancy, the same shape as EF Core's real
/// ConcurrencyDetector - production's ICooldownRepository (EfCooldownRepository) is EF-backed and
/// registered scoped, so RunEndpoints.cs's fan-out sharing ONE instance across several dimensions
/// in the same request is exactly production's shape too. The original fan-out ran those calls
/// concurrently (Task.WhenAll), which threw live against a real DbContext ("A second operation was
/// started on this context instance before a previous operation completed") the first time a real
/// caller tried 3 dimensions in Postman. This fake reproduces that failure mode so a future
/// regression (reintroducing concurrent calls) fails a fast unit test instead of only a real request.
/// </summary>
internal sealed class FakeCooldownRepository : ICooldownRepository
{
    private readonly Func<string, CooldownResult> _resultForPeriod;
    private int _inFlight;

    public FakeCooldownRepository(CooldownResult result) : this(_ => result) { }

    public FakeCooldownRepository(Func<string, CooldownResult> resultForPeriod) => _resultForPeriod = resultForPeriod;

    public List<(int CustomerId, string ReportType, string ScopeDescriptor, string Period)> Calls { get; } = [];

    public async Task<CooldownResult> CheckAsync(
        int customerId, string reportType, string scopeDescriptor, string period,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "A second operation was started on this context instance before a previous operation completed " +
                "(FakeCooldownRepository simulating EF Core's real ConcurrencyDetector - see this class's own doc comment).");
        }

        try
        {
            Calls.Add((customerId, reportType, scopeDescriptor, period));
            // Real room for a concurrency bug to manifest - a synchronous fake (no await point)
            // would never actually overlap two "concurrent" calls even if the caller used
            // Task.WhenAll, since nothing yields control between them.
            await Task.Delay(5, cancellationToken);
            return _resultForPeriod(period);
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }
}

/// <summary>
/// In-memory stand-in for the fan-out reqId grouping (sql/30_report_request.sql) - records every
/// SaveAsync call and answers GetRunIdsAsync from what was saved, never touching a real DbContext.
/// </summary>
internal sealed class FakeReportRequestRepository : IReportRequestRepository
{
    public List<(Guid ReqId, IReadOnlyList<string> RunIds)> SaveCalls { get; } = [];
    private readonly Dictionary<Guid, List<string>> _byReqId = [];

    public Task SaveAsync(Guid reqId, IReadOnlyList<string> runIds, CancellationToken cancellationToken = default)
    {
        SaveCalls.Add((reqId, runIds));
        if (!_byReqId.TryGetValue(reqId, out var existing))
            _byReqId[reqId] = existing = [];
        existing.AddRange(runIds);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetRunIdsAsync(Guid reqId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(_byReqId.TryGetValue(reqId, out var runIds) ? runIds : []);
}

/// <summary>Records what it was asked to enqueue and hands back a fixed run id, never touching a real task hub.</summary>
internal sealed class FakeRunEnqueuer(string runIdToReturn) : IInsightsRunEnqueuer
{
    public List<(int TenantId, string ReportType, InsightsScopeRequest Scope, string Period, int UserId, LlmCallPriority Priority, IReadOnlyList<string>? RequestedDimensions)> Calls { get; } = [];

    public Task<string> EnqueueAsync(
        int tenantId, string reportType, InsightsScopeRequest scope, string period, int userId,
        CancellationToken cancellationToken = default, LlmCallPriority priority = LlmCallPriority.Interactive,
        IReadOnlyList<string>? requestedDimensions = null)
    {
        Calls.Add((tenantId, reportType, scope, period, userId, priority, requestedDimensions));
        return Task.FromResult(runIdToReturn);
    }
}
