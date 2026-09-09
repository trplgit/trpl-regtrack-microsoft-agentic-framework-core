using DurableTask.Core;
using DurableTask.SqlServer;
using Insights.Agents;
using Insights.Data;
using Insights.Persistence;
using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// Worker-side services that are not specific to one tier.
///
/// Call AFTER AddInsightsData - the publish gate depends on IScopeRepository for its post-flight
/// audit.
/// </summary>
public static class WorkerRegistration
{
    public static IServiceCollection AddInsightsWorker(this IServiceCollection services)
    {
        /*  The publish gate (build order step 8) - the deterministic arbiter that runs after
            narrative reflection and before anything renders. Nothing reaches a customer without
            passing it.

            Registered as the concrete type because it has no interface: it is a pure decision
            over inputs it is handed, with one dependency, and there is nothing to substitute.  */
        services.AddScoped<PublishGate>();

        return services;
    }

    /// <summary>
    /// Registers the Durable Task SQL Server hosting (build order step 11) - TaskHubWorker with
    /// every activity from Orchestration/Activities and InsightsReportOrchestrator itself,
    /// TaskHubClient for enqueueing runs. Call AFTER AddInsightsData and AddInsightsPaidReportAgents -
    /// every activity below depends on repositories or agents registered there.
    ///
    /// Findings from Task 1's spike, captured here rather than re-discovered:
    ///   - Classic DTFx (DurableTask.Core's TaskHubWorker/TaskHubClient), NOT the newer portable
    ///     Microsoft.DurableTask.Worker/.Client SDK - that SDK only supports Azure Durable Task
    ///     Scheduler as a backend, not self-hosted SQL Server.
    ///   - SqlOrchestrationService implements both IOrchestrationService and
    ///     IOrchestrationServiceClient (confirmed by the cast below succeeding in Task 1's spike).
    ///   - Custom status is exposed via TaskOrchestration.GetStatus() (confirmed - not the portable
    ///     SDK's SetCustomStatus).
    ///   - ConnectionStrings:DurableTaskHub needs Encrypt=False, not TrustServerCertificate=True -
    ///     the latter works for every other connection in this codebase but was not honoured
    ///     inside SqlOrchestrationService's own connection handling (root cause not diagnosed).
    /// </summary>
    public static IServiceCollection AddInsightsOrchestration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddInsightsOrchestrationClient(configuration);
        services.AddInsightsOrchestrationWorker(configuration);
        return services;
    }

    /// <summary>
    /// The half of AddInsightsOrchestration an API host needs: TaskHubClient (enqueue + read
    /// status), IRunStatusReader and IInsightsRunEnqueuer for API_CONTRACTS.md §3/§4. Deliberately
    /// does NOT register TaskHubWorker, any activity, or DurableTaskHostedService.
    ///
    /// [TRAP - found live 2026-08-24] The original single AddInsightsOrchestration bundled the
    /// worker's dequeue loop in with the client-only pieces an API host needs. A throwaway local
    /// listener built to test the two Insights.Api endpoints called it for TaskHubClient alone and
    /// - without meaning to - started a second real TaskHubWorker competing on the SHARED UAT task
    /// hub queue the instant the host started. This split makes that impossible to repeat: nothing
    /// reachable from this method can ever dequeue or execute a task.
    /// </summary>
    public static IServiceCollection AddInsightsOrchestrationClient(this IServiceCollection services, IConfiguration configuration)
    {
        RegisterSqlOrchestrationService(services, configuration);

        services.AddSingleton(sp => new TaskHubClient((IOrchestrationServiceClient)sp.GetRequiredService<SqlOrchestrationService>()));

        // Reads run progress out of the instance store for API_CONTRACTS.md 4. Registered here
        // because it needs TaskHubClient; the RegTrack API takes this file and IRunStatusReader
        // when it hosts the endpoint, and injects only the interface.
        services.AddSingleton<Insights.Data.IRunStatusReader, DurableTaskRunStatusReader>();

        // Enqueues a run for API_CONTRACTS.md 3, same reasoning as IRunStatusReader above.
        services.AddSingleton<Insights.Data.IInsightsRunEnqueuer, DurableTaskRunEnqueuer>();

        return services;
    }

    /// <summary>
    /// The half of AddInsightsOrchestration only the actual worker process should call: every
    /// activity, TaskHubWorker, and the hosted service that starts its dequeue loop
    /// (DurableTaskHostedService). Requires AddInsightsData and AddInsightsPaidReportAgents first -
    /// every activity below depends on repositories or agents registered there.
    /// </summary>
    public static IServiceCollection AddInsightsOrchestrationWorker(this IServiceCollection services, IConfiguration configuration)
    {
        RegisterSqlOrchestrationService(services, configuration);

        // insights.dimension.block_failures_total (design doc Sec.11.4/Sec.11.5, CONFIGURATION.md
        // Sec.13). One singleton exposed under both types, same reasoning as InsightsCostMetrics:
        // two registrations would mean two Meters with the same name double-counting failures.
        services.AddSingleton<DimensionFailureMetrics>();
        services.AddSingleton<IDimensionFailureRecorder>(sp => sp.GetRequiredService<DimensionFailureMetrics>());

        services.AddTransient<CheckTenantTokenBudgetActivity>();
        services.AddTransient<RecordTenantTokenUsageActivity>();
        services.AddTransient<GatherScopeActivity>();
        services.AddTransient<FetchDimensionsActivity>();
        services.AddTransient<ComputeScoreActivity>();
        services.AddTransient<ComposeActivity>();
        services.AddTransient<ReflectOnCompositionActivity>();
        services.AddTransient<NarrateActivity>();
        services.AddTransient<ReflectOnNarrativeActivity>();
        services.AddTransient<PublishGateActivity>();
        services.AddTransient<RenderHtmlActivity>();
        services.AddTransient<InjectFontActivity>();
        services.AddTransient<InjectCoverageGridActivity>();
        services.AddTransient<InjectCoverageCssActivity>();
        services.AddTransient<InjectCoverageScriptActivity>();
        services.AddTransient<NormalizeActivity>();
        services.AddTransient<SanitizeActivity>();
        services.AddTransient<ValidateFixedHolisticStructureActivity>();
        services.AddTransient<PlaywrightQaActivity>();
        services.AddTransient<PersistActivity>();

        // Build order item 14's write path: encrypt -> blob -> SQL index row.
        RegisterReportCodec(services, configuration);
        RegisterReportsDbContext(services, configuration);

        // Free digest lane (design doc 10.2) - resolve -> compose per scope group -> send per recipient.
        services.AddTransient<ResolveDigestRecipientsActivity>();
        services.AddTransient<ComposeDigestActivity>();
        services.AddTransient<SendDigestActivity>();

        services.AddSingleton(sp =>
        {
            var service = sp.GetRequiredService<SqlOrchestrationService>();
            var worker = new TaskHubWorker(service);

            // NameValueObjectCreator is fine here (unlike activities) - InsightsReportOrchestrator
            // has no constructor dependencies, so Activator.CreateInstance-based construction is
            // sufficient. Explicit Name/Version, matching the same explicit strings
            // InsightsReportOrchestrator's ScheduleTask calls and InsightsRunOnceWorker's
            // CreateOrchestrationInstanceAsync call use - not relying on whatever Type-based
            // AddTaskOrchestrations(typeof(...)) would have derived internally, which is
            // unconfirmed and was exactly the kind of guess that broke ActivityCreator below.
            worker.AddTaskOrchestrations(new NameValueObjectCreator<TaskOrchestration>(
                InsightsReportOrchestrator.Name, InsightsReportOrchestrator.Version, typeof(InsightsReportOrchestrator)));

            // Same explicit Name/Version treatment - FreeDigestOrchestrator likewise has no constructor
            // dependencies, and these strings must match what the scheduler passes to
            // CreateOrchestrationInstanceAsync.
            worker.AddTaskOrchestrations(new NameValueObjectCreator<TaskOrchestration>(
                FreeDigestOrchestrator.Name, FreeDigestOrchestrator.Version, typeof(FreeDigestOrchestrator)));

            worker.AddTaskActivities(
                ActivityCreator<CheckTenantTokenBudgetActivity>(sp), ActivityCreator<RecordTenantTokenUsageActivity>(sp),
                ActivityCreator<GatherScopeActivity>(sp), ActivityCreator<FetchDimensionsActivity>(sp),
                ActivityCreator<ComputeScoreActivity>(sp),
                ActivityCreator<ComposeActivity>(sp), ActivityCreator<ReflectOnCompositionActivity>(sp),
                ActivityCreator<NarrateActivity>(sp), ActivityCreator<ReflectOnNarrativeActivity>(sp),
                ActivityCreator<PublishGateActivity>(sp), ActivityCreator<RenderHtmlActivity>(sp),
                ActivityCreator<InjectFontActivity>(sp), ActivityCreator<InjectCoverageGridActivity>(sp),
                ActivityCreator<InjectCoverageCssActivity>(sp), ActivityCreator<InjectCoverageScriptActivity>(sp),
                ActivityCreator<NormalizeActivity>(sp), ActivityCreator<SanitizeActivity>(sp),
                ActivityCreator<ValidateFixedHolisticStructureActivity>(sp),
                ActivityCreator<PlaywrightQaActivity>(sp), ActivityCreator<PersistActivity>(sp),
                ActivityCreator<ResolveDigestRecipientsActivity>(sp), ActivityCreator<ComposeDigestActivity>(sp),
                ActivityCreator<SendDigestActivity>(sp));

            return worker;
        });

        services.AddHostedService<DurableTaskHostedService>();

        return services;
    }

    /// <summary>
    /// TryAdd - both AddInsightsOrchestrationClient and AddInsightsOrchestrationWorker depend on
    /// this, and AddInsightsOrchestration calls both. A plain AddSingleton would register the
    /// factory twice; harmless at resolve time (last registration wins) but untidy, and TryAdd
    /// costs nothing to get right instead.
    /// </summary>
    private static void RegisterSqlOrchestrationService(IServiceCollection services, IConfiguration configuration)
    {
        var taskHubConnectionString = Require(configuration, "ConnectionStrings:DurableTaskHub");

        services.TryAddSingleton(sp =>
        {
            var settings = new SqlOrchestrationServiceSettings(taskHubConnectionString)
            {
                // [DIAG - temporary] SqlOrchestrationServiceSettings.LoggerFactory routes the
                // library's own internal dispatch/poll diagnostics through this app's real
                // ILoggerFactory instead of nowhere - added to find out why the orchestration
                // dispatcher stops making progress after its first successful activity completion,
                // confirmed reproducible across multiple fresh instances/processes today.
                LoggerFactory = sp.GetRequiredService<ILoggerFactory>(),
                // [FIX - found live] "Duplicate execution of 'FetchDimensionsActivity' was
                // detected!" fired even for a run this same single process created and processed
                // alone (no other worker involved) - the default concurrency settings run several
                // internal dispatch loops in parallel within ONE process, and a lock-renewal race
                // between two of THIS process's own loops let two of them grab the same work item.
                // Forcing single-item concurrency removes the race entirely; this is a pure local
                // setting with no effect on the shared database or any other worker.
                MaxConcurrentActivities = 1,
                MaxActiveOrchestrations = 1,
            };
            var service = new SqlOrchestrationService(settings);
            service.CreateIfNotExistsAsync().GetAwaiter().GetResult();
            return service;
        });
    }

    /// <summary>
    /// Build order item 14's read half (design doc Sec.9.3, API_CONTRACTS.md §5) - what
    /// ReportContentEndpoints needs. Belongs to the API HOST, not the worker: unlike
    /// AddInsightsOrchestrationWorker, this never registers TaskHubWorker or any activity, so
    /// calling it from the RegTrack API (or a test host) cannot accidentally start a second
    /// dequeue loop, same reasoning as AddInsightsOrchestrationClient's split from ...Worker.
    ///
    /// Call AFTER AddInsightsData - IReportContentService depends on IScopeRepository and
    /// IEntityRepository, both registered there.
    /// </summary>
    public static IServiceCollection AddInsightsReportContentService(this IServiceCollection services, IConfiguration configuration)
    {
        RegisterReportCodec(services, configuration);
        RegisterReportsDbContext(services, configuration);

        var blobConnectionString = Require(configuration, "Azure:BlobConnectionString");
        var blobContainer = Require(configuration, "Azure:BlobContainer");
        services.AddSingleton<IReportViewPublisher>(_ => new AzureReportViewPublisher(blobConnectionString, blobContainer));

        // Reports:SasLifetimeMinutes - already scaffolded in appsettings.json (=10) ahead of this
        // being wired. Required, not optional-with-a-guessed-default: a view link's lifetime is a
        // security parameter (design doc Sec.9.3's "short-lived, single-use"), not a tunable that
        // should silently default to something nobody chose.
        var sasLifetimeMinutes = configuration.GetValue<int?>("Reports:SasLifetimeMinutes")
            ?? throw new InvalidOperationException("Reports:SasLifetimeMinutes is not configured.");
        var sasLifetime = TimeSpan.FromMinutes(sasLifetimeMinutes);

        /*  SCOPED, not singleton - depends on InsightsReportsDbContext (scoped by AddDbContext)
            and IScopeRepository/IEntityRepository (both scoped in AddInsightsData). A singleton
            depending on any of those would be a captive-dependency bug, holding one DbContext/
            connection open for the process lifetime instead of one per request.               */
        services.AddScoped<IReportContentService>(sp => new ReportContentService(
            sp.GetRequiredService<InsightsReportsDbContext>(),
            sp.GetRequiredService<IScopeRepository>(),
            sp.GetRequiredService<IEntityRepository>(),
            sp.GetRequiredService<IReportDecryptor>(),
            sp.GetRequiredService<IReportBlobReader>(),
            sp.GetRequiredService<IReportViewPublisher>(),
            sasLifetime,
            sp.GetRequiredService<ILogger<ReportContentService>>()));

        // API_CONTRACTS.md Sec.3 step 3 (design doc Sec.2.4's 30-day cooldown) - RunEndpoints
        // needs this on the SAME host that generates reports, and it depends on
        // InsightsReportsDbContext exactly like IReportContentService above, so it is registered
        // here rather than a separate composition method a caller could forget to invoke.
        //
        // Required, same "no guessed default for a locked spec value" reasoning as
        // Reports:SasLifetimeMinutes above.
        var cooldownDays = configuration.GetValue<int?>("Reports:CooldownDays")
            ?? throw new InvalidOperationException("Reports:CooldownDays is not configured.");

        // SCOPED - same captive-dependency reasoning as IReportContentService: it holds a scoped
        // InsightsReportsDbContext, so it cannot be a singleton.
        services.AddScoped<ICooldownRepository>(sp =>
            new EfCooldownRepository(sp.GetRequiredService<InsightsReportsDbContext>(), cooldownDays));

        return services;
    }

    /// <summary>
    /// AdalKeyVaultReportEncryptor and AzureReportBlobWriter each implement TWO interfaces
    /// (encrypt+decrypt, write+read) - registered ONCE as concrete singletons and exposed under
    /// both, so a process that does both write (PersistActivity, worker-only) and read
    /// (IReportContentService, API-host-only) never pays for a second Key Vault auth or blob
    /// client. In production these run in different processes entirely (worker is private/
    /// queue-driven, the API host is public - CLAUDE.md 6), so the sharing mostly matters for this
    /// repo's own combined test/demo hosts, but it costs nothing either way.
    /// </summary>
    private static void RegisterReportCodec(IServiceCollection services, IConfiguration configuration)
    {
        var regTrackConnectionString = Require(configuration, "ConnectionStrings:RegTrack");
        var blobConnectionString = Require(configuration, "Azure:BlobConnectionString");
        var blobContainer = Require(configuration, "Azure:BlobContainer");

        services.TryAddSingleton(_ => new AdalKeyVaultReportEncryptor(regTrackConnectionString));
        services.TryAddSingleton<IReportEncryptor>(sp => sp.GetRequiredService<AdalKeyVaultReportEncryptor>());
        services.TryAddSingleton<IReportDecryptor>(sp => sp.GetRequiredService<AdalKeyVaultReportEncryptor>());

        services.TryAddSingleton(_ => new AzureReportBlobWriter(blobConnectionString, blobContainer));
        services.TryAddSingleton<IReportBlobWriter>(sp => sp.GetRequiredService<AzureReportBlobWriter>());
        services.TryAddSingleton<IReportBlobReader>(sp => sp.GetRequiredService<AzureReportBlobWriter>());
    }

    /// <summary>
    /// TryAdd - both the worker's write path and AddInsightsReportContentService's read path need
    /// this, and a combined host calls both.
    ///
    /// [TEMP OVERRIDE 2026-09-08] ConnectionStrings:RegTrackReportsWrite, when set, overrides just
    /// this DbContext's target - added for a one-off manual run reading tenant data from a
    /// READ-ONLY production credential (ConnectionStrings:RegTrack) while still persisting the
    /// generated report to a writable database (UAT). Falls back to the normal shared connection
    /// string when unset, so every other caller of this method is unaffected. Remove once prod
    /// read-only testing is done, or promote to a permanent documented setting if this becomes a
    /// real recurring need.
    /// </summary>
    private static void RegisterReportsDbContext(IServiceCollection services, IConfiguration configuration)
    {
        var writeConnectionString = configuration["ConnectionStrings:RegTrackReportsWrite"]
            ?? Require(configuration, "ConnectionStrings:RegTrack");
        services.AddDbContext<InsightsReportsDbContext>(options => options.UseSqlServer(writeConnectionString));
    }

    /// <summary>
    /// [FIX - found live] `sp` here is the ROOT provider (captured once inside the TaskHubWorker
    /// singleton factory above), but every repository an activity depends on is registered
    /// AddScoped (IScopeRepository, IDimensionRepository, ITenantTokenBudgetRepository, PublishGate,
    /// etc. - this is every activity in the pipeline, not just one). Resolving a scoped service
    /// straight from the root provider throws InvalidOperationException, and DurableTask.Core's
    /// SQL dispatcher does not convert that construction-time failure into a TaskFailed history
    /// event - it just abandons the work item, which reappears after its lock expires and retries
    /// forever with no error ever visible in history or in this app's own logs. Confirmed live:
    /// CheckTenantTokenBudgetActivity sat at DequeueCount 20+ with zero TaskFailed/TaskCompleted
    /// events. Creating a fresh scope per activity resolution - and disposing it once the instance
    /// is constructed - is the standard fix for consuming scoped services outside a request/scope
    /// context; every current repository is a thin, stateless connectionString wrapper that opens
    /// its own connection per call (confirmed against SqlTenantTokenBudgetRepository), so nothing
    /// is lost by not keeping the scope alive for the activity's lifetime.
    /// </summary>
    private static DelegateActivityCreator<TActivity> ActivityCreator<TActivity>(IServiceProvider sp)
        where TActivity : TaskActivity =>
        new(() =>
        {
            using var scope = sp.CreateScope();
            return scope.ServiceProvider.GetRequiredService<TActivity>();
        });

    /// <summary>
    /// NameValueObjectCreator&lt;T&gt; (DurableTask.Core) only supports Type-based (Activator.
    /// CreateInstance) construction, confirmed by a real compile error against a factory-delegate
    /// call - not DI-friendly. This is the factory-based ObjectCreator&lt;TaskActivity&gt; DI
    /// construction actually needs.
    ///
    /// Name/Version are set explicitly in the constructor (both have `protected set`, no confirmed
    /// default - leaving them unset compiled fine but would have left Name/Version null, silently
    /// breaking TaskHubWorker's dispatch matching at runtime). typeof(TActivity).Name is used here
    /// AND in the matching context.ScheduleTask&lt;T&gt;(typeof(X).Name, "1.0", input) calls in
    /// InsightsReportOrchestrator - both sides derive the same string the same way, rather than
    /// relying on an unconfirmed implicit Type-to-Name/Version mapping inside DTFx itself.
    /// </summary>
    private sealed class DelegateActivityCreator<TActivity> : ObjectCreator<TaskActivity>
        where TActivity : TaskActivity
    {
        private readonly Func<TActivity> _factory;

        public DelegateActivityCreator(Func<TActivity> factory)
        {
            _factory = factory;
            Name = typeof(TActivity).Name;
            Version = "1.0";
        }

        public override TaskActivity Create() => _factory();
    }

    private static string Require(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set it in appsettings for local work, or via user-secrets / environment configuration elsewhere.");
}
