using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insights.Data;

/// <summary>
/// Registers every SQL-backed repository in one call, so a caller only needs
/// <c>builder.Services.AddInsightsData(builder.Configuration);</c> and can then inject any of
/// the interfaces below.
///
/// All six wrap stored procedures that already enforce scope, entitlement and reconciliation in
/// SQL. Nothing here re-derives any of that - the wrappers only type what the procs decided.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Connection string key in appsettings - points at vitComplianceSystem.</summary>
    private const string RegTrackConnectionName = "RegTrack";

    /// <summary>
    /// Registers the scope, entity, entitlement, golden-regression, dimension and free-digest
    /// repositories against <c>ConnectionStrings:RegTrack</c>.
    ///
    /// Throws at STARTUP if that connection string is missing, rather than at first query. A
    /// worker that boots healthy and then fails on its first tenant is far harder to diagnose
    /// than one that refuses to start, and this is the same fail-closed stance the SQL takes.
    /// </summary>
    public static IServiceCollection AddInsightsData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(RegTrackConnectionName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{RegTrackConnectionName} is not configured. Set it in appsettings.Development.json " +
                "for local work, or via user-secrets / environment configuration elsewhere. Never point it at production.");
        }

        /*  Scoped rather than singleton: each repository opens and disposes its own SqlConnection
            per call, so there is no shared mutable state, but scoped keeps them aligned with the
            per-run lifetime a Durable Task activity or a digest job expects.                     */

        // sql/01 - dictionary coverage + status data-quality probe. Run these as a BATCH
        // pre-flight, not per tenant: a dictionary gap is a property of the deployment.
        services.AddScoped<IDictionaryRepository>(_ => new SqlDictionaryRepository(connectionString));

        // sql/03 - scope pairs, classification, post-flight audit, scopeless users.
        services.AddScoped<IScopeRepository>(_ => new SqlScopeRepository(connectionString));

        // sql/04 part 1 - entity tree, reconciled rollup, tenant shape.
        services.AddScoped<IEntityRepository>(_ => new SqlEntityRepository(connectionString));

        // sql/04 part 2 - entitlement gate. [TRAP] ProductMapping.IsActive is INVERTED.
        services.AddScoped<IEntitlementRepository>(_ => new SqlEntitlementRepository(connectionString));

        // sql/02 - golden invariants. Run these before trusting any dimension output.
        services.AddScoped<IGoldenRegressionRepository>(_ => new SqlGoldenRegressionRepository(connectionString));

        // sql/05, sql/07 - sql/14, sql/21 - the ten dimensions, one repository for all of them.
        services.AddScoped<IDimensionRepository>(_ => new SqlDimensionRepository(connectionString));

        // sql/17 - eligible tenants. THE IDOR GUARD: every endpoint taking a client-supplied
        // tenantId re-checks it here, on every request. Never cached across requests.
        services.AddScoped<ITenantDirectoryRepository>(_ => new SqlTenantDirectoryRepository(connectionString));

        // sql/06 - free weekly digest. Call EvaluateGateAsync BEFORE GetAggregatesAsync.
        services.AddScoped<IFreeDigestRepository>(_ => new SqlFreeDigestRepository(connectionString));

        return services;
    }
}

