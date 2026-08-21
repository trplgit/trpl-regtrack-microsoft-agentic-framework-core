using DurableTask.Core;
using DurableTask.SqlServer;
using Insights.Agents;
using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        var taskHubConnectionString = Require(configuration, "ConnectionStrings:DurableTaskHub");

        services.AddTransient<GatherScopeActivity>();
        services.AddTransient<FetchDimensionsActivity>();
        services.AddTransient<ComposeActivity>();
        services.AddTransient<ReflectOnCompositionActivity>();
        services.AddTransient<NarrateActivity>();
        services.AddTransient<ReflectOnNarrativeActivity>();
        services.AddTransient<PublishGateActivity>();
        services.AddTransient<RenderHtmlActivity>();
        services.AddTransient<NormalizeActivity>();
        services.AddTransient<SanitizeActivity>();
        services.AddTransient<PlaywrightQaActivity>();
        services.AddTransient<PersistStubActivity>();

        services.AddSingleton(_ =>
        {
            var settings = new SqlOrchestrationServiceSettings(taskHubConnectionString);
            var service = new SqlOrchestrationService(settings);
            service.CreateIfNotExistsAsync().GetAwaiter().GetResult();
            return service;
        });

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

            worker.AddTaskActivities(
                ActivityCreator<GatherScopeActivity>(sp), ActivityCreator<FetchDimensionsActivity>(sp),
                ActivityCreator<ComposeActivity>(sp), ActivityCreator<ReflectOnCompositionActivity>(sp),
                ActivityCreator<NarrateActivity>(sp), ActivityCreator<ReflectOnNarrativeActivity>(sp),
                ActivityCreator<PublishGateActivity>(sp), ActivityCreator<RenderHtmlActivity>(sp),
                ActivityCreator<NormalizeActivity>(sp), ActivityCreator<SanitizeActivity>(sp),
                ActivityCreator<PlaywrightQaActivity>(sp), ActivityCreator<PersistStubActivity>(sp));

            return worker;
        });

        services.AddSingleton(sp => new TaskHubClient((IOrchestrationServiceClient)sp.GetRequiredService<SqlOrchestrationService>()));
        services.AddHostedService<DurableTaskHostedService>();

        return services;
    }

    private static DelegateActivityCreator<TActivity> ActivityCreator<TActivity>(IServiceProvider sp)
        where TActivity : TaskActivity =>
        new(() => sp.GetRequiredService<TActivity>());

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
