// Composition root for the RegTrack Insights worker.
//
// [CHANGED 2026-09-16, ADR-0004] Was: "PRIVATE and queue-driven: no public endpoints, no
// ingress." Product decision, same date: this is now ONE combined host serving the Insights HTTP
// API (Kestrel, real JWT auth) *and* the existing Durable Task dequeue loop, in one process. See
// RegtrackInsights.csproj's own comment and Insights.Worker.Auth.JwtAuthRegistration.
//
// Explicit usings below that Microsoft.NET.Sdk.Web would normally supply as GlobalUsings - this
// project deliberately stays on Microsoft.NET.Sdk.Worker (see RegtrackInsights.csproj's own
// comment on why: the Web SDK's default Content/None globs would silently break prompts/
// templates/vendor loading), so FrameworkReference brings in the types but not the free usings.
using Insights.Api;
using Insights.Data;
using Insights.Worker;
using Insights.Worker.Auth;
using Insights.Worker.HealthChecks;
using Insights.Worker.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Explicit bind, not relying on an environment variable that may not be set in every environment.
// The worker previously had no HTTP listener at all; the observed "connection refused" against
// the deployed pod's containerPort 8080 is exactly the signature of that missing bind, not a
// misconfigured port mapping - so this is set here, in code, unconditionally.
builder.WebHost.UseUrls("http://0.0.0.0:8080");

// Every SQL-backed repository, registered against ConnectionStrings:RegTrack.
//
//   IDictionaryRepository       sql/01  dictionary coverage + status data-quality probe
//   IGoldenRegressionRepository sql/02  golden invariants
//   IScopeRepository            sql/03  scope pairs, classification, post-flight audit
//   IEntityRepository           sql/04  entity tree, reconciled rollup, tenant shape
//   IEntitlementRepository      sql/04  entitlement gate (free | paid)
//   IDimensionRepository        sql/05, sql/07 - sql/14  the nine dimensions
//   IFreeDigestRepository       sql/06, 15, 16  digest gate, aggregates, send log, suppression
//
// Every repository method can THROW by design - scope denied, reconciliation failed, dictionary
// gap, contract violation. Those are refusals, not transient faults: catch them, abandon the run,
// and never degrade one to a warning or an empty result.
builder.Services.AddInsightsData(builder.Configuration);

// Design doc Sec.12.3's per-tenant monthly token circuit breaker + 80% alert - NOT optional like
// the schedulers below: InsightsReportOrchestrator now calls CheckTenantTokenBudgetActivity
// unconditionally at the start of every run, so this must always be registered, not gated behind
// a feature flag. Must come AFTER AddInsightsData (needs ConnectionStrings:RegTrack) and BEFORE
// AddInsightsOrchestration (registers the two activities that consume it).
builder.Services.AddInsightsTenantTokenBudget(builder.Configuration);

// Permanent home for each LLM agent's own reasoning summary (sql/32) - deliberately separate from
// dt.Payloads, which is slated for a future purge. Must come AFTER AddInsightsData, BEFORE
// AddInsightsOrchestration (registers the three activities that consume it).
builder.Services.AddInsightsAgentReasoning(builder.Configuration);

// The free weekly digest (Phase 1c): LLM client, prompt loader, writer, renderer, email sender,
// pipeline, IFreeDigestService, and the weekly scheduler. Must come AFTER AddInsightsData.
//
// The scheduler is the production trigger - weekly, staggered by hash(tenantId) % 7, lowest
// priority lane (design doc 10.3). Inert unless FreeDigest:Schedule:Enabled is true.
builder.Services.AddInsightsFreeDigest(builder.Configuration);

// The publish gate (build order step 8) - reconciliation, claim-checker, scope post-flight audit.
builder.Services.AddInsightsWorker();

// The paid-tier agents (build order step 12, already built and manually verified) and the
// Durable Task orchestrator that runs them durably (build order step 11). Must come AFTER
// AddInsightsData and AddInsightsWorker - the orchestration's activities depend on repositories
// registered there and reuse the same PublishGate singleton, never a duplicate.
//
// [TEMP - client-only mode] A real worker is already live elsewhere on this shared UAT task hub
// (confirmed: a session from a Kubernetes pod, trpl-regtrack-dot-net-core-api-*, actively
// dequeuing - "Duplicate execution of 'FetchDimensionsActivity' was detected!" only happens when
// TWO workers race the same message). Running a second local worker against the SAME shared
// vitInsightsTaskHub just adds a second competitor rather than helping. Insights:ClientOnly=true
// registers ONLY the client half (enqueue + poll status) and skips TaskHubWorker/
// DurableTaskHostedService entirely, so this process never dequeues or executes anything - the
// already-running remote worker does all the work, uninterrupted. Opt-in, defaults to today's
// existing full-worker behaviour when unset.
builder.Services.AddInsightsPaidReportAgents(builder.Configuration);
var clientOnly = builder.Configuration.GetValue("Insights:ClientOnly", false);
if (clientOnly)
    builder.Services.AddInsightsOrchestrationClient(builder.Configuration);
else
    builder.Services.AddInsightsOrchestration(builder.Configuration);

// The paid_batch keep-warm lane (design doc Sec.4.2-4.5) - re-runs a (scope, reportType, period)
// key a paying tenant has generated AND actually viewed recently, staggered by
// hash(tenantId) % Schedule:PaidAnchorModulo. Middle of the three priority lanes - behind
// paid_interactive, ahead of the free digest (Sec.4.4). Must come AFTER AddInsightsOrchestration -
// depends on IInsightsRunEnqueuer and InsightsReportsDbContext, both registered there. Inert
// unless Schedule:PaidKeepWarm:Enabled is true.
builder.Services.AddInsightsPaidKeepWarm(builder.Configuration);

// OTel -> LangFuse (build order item 17 / O-4). Skips itself if Otel:LangfuseEndpoint is unset -
// see ObservabilityRegistration's doc comment for why that one key alone is optional.
builder.Services.AddInsightsObservability(builder.Configuration);

// [ADDED 2026-09-16, ADR-0004] The read half of build order item 14 (design doc Sec.9.3,
// API_CONTRACTS.md 5) - decrypt + serve, cooldown check, and the reqId fan-out grouping.
// RunEndpoints and ReportContentEndpoints below both need this; it was never called from this
// Program.cs before because nothing here ever mapped those endpoints. Must come AFTER
// AddInsightsData (needs IScopeRepository, IEntityRepository).
builder.Services.AddInsightsReportContentService(builder.Configuration);

// [ADDED 2026-09-16, ADR-0004] "Who is asking" for every mapped endpoint below - JWT Bearer
// validated against the same Jwt:Key/Issuer/Audience the real RegTrack API already uses, plus a
// fallback authorization policy that requires an authenticated user on anything mapped without an
// explicit AllowAnonymous(). See JwtAuthRegistration's own doc comment for the full design,
// including the deliberately deferred token-revocation decision.
builder.Services.AddInsightsAuthentication(builder.Configuration);

// [ADDED 2026-09-16, ADR-0004] /health, /health/live, /health/ready. Independent of every
// registration above - health checks must never depend on (or be blocked by) anything they exist
// to report on. See HealthCheckRegistration's own doc comment for exactly what each path checks
// and why SQL/LLM/blob checks are deliberately out of scope for now.
builder.Services.AddInsightsHealthChecks(builder.Configuration);

// One-shot runner for testing a single tenant from the command line. Does nothing unless
// FreeDigest:RunOnce=true:
//   dotnet run -- --FreeDigest:RunOnce=true --FreeDigest:CustomerId=23
builder.Services.AddHostedService<FreeDigestRunOnceWorker>();

// DIAGNOSTIC ONLY - decrypts a tenant's stored digest artifact(s) straight to local .html
// files, reusing the same Key Vault decrypt path production uses. Does nothing unless
// FreeDigest:DumpOnce=true:
//   dotnet run -- --FreeDigest:DumpOnce=true --FreeDigest:CustomerId=29
builder.Services.AddHostedService<FreeDigestArtifactDumpWorker>();

// PREVIEW ONLY - renders the monthly free digest for one tenant to local .html + .debug.txt
// files, in-process: no orchestration, no claim, no artifact, no email. Does nothing unless
// FreeDigest:Preview:Enabled=true - see FreeMonthlyPreviewWorker's own doc comment:
//   dotnet run -- --FreeDigest:Preview:Enabled=true --FreeDigest:CustomerId=1490 --Insights:ClientOnly=true --FreeDigest:Schedule:Enabled=false
builder.Services.AddHostedService<FreeMonthlyPreviewWorker>();

// One-shot runner for the paid orchestrator. Does nothing unless Insights:RunOnce=true:
//   dotnet run -- --Insights:RunOnce=true --Insights:TenantId=29 --Insights:UserId=38
builder.Services.AddHostedService<InsightsRunOnceWorker>();

var app = builder.Build();

// [ADDED 2026-09-16, ADR-0004] Loud, anonymous startup warnings for config gaps that fail OPEN
// rather than closed - mirroring the sibling RegTrack API's own Program.cs convention exactly
// (same pattern used there for its own several secrets). Jwt:Key/Issuer/Audience are NOT here:
// JwtAuthRegistration already fails CLOSED (throws) at registration time above if any of those
// are missing, so reaching this line at all means auth is already correctly configured.
var healthCheckToken = app.Services.GetRequiredService<IOptions<HealthCheckAuthConfig>>().Value?.Token;
if (string.IsNullOrWhiteSpace(healthCheckToken))
{
    app.Logger.LogError(
        "STARTUP: HealthCheckAuth:Token is not set, so /health, /health/ready and /health/live are " +
        "being served ANONYMOUSLY. Add the token via the HealthCheckAuth__Token environment variable " +
        "(never appsettings.json in a real environment) once this host sits behind an ingress.");
}

if (!string.IsNullOrWhiteSpace(app.Configuration["Reports:LocalFallbackDirectory"]))
{
    app.Logger.LogWarning(
        "STARTUP: Reports:LocalFallbackDirectory is set - this is a TEMPORARY diagnostic override " +
        "(see PersistActivity/NormalizeActivity). Confirm this is intentional in this environment.");
}

// Same three RunOnce/DumpOnce flags as before, but now co-resident with a real public HTTP
// surface - a stray true in deployed config used to mean "the worker does something odd"; now it
// means "the internet-facing pod is doing something odd". Confirm all three are unset outside
// local development.
foreach (var (key, description) in new[]
{
    ("FreeDigest:RunOnce", "one-shot free digest run"),
    ("FreeDigest:DumpOnce", "one-shot digest artifact dump"),
    ("FreeDigest:Preview:Enabled", "one-shot monthly digest preview"),
    ("Insights:RunOnce", "one-shot paid orchestrator run"),
})
{
    if (app.Configuration.GetValue(key, false))
        app.Logger.LogWarning(
            "STARTUP: {Key} is true - this host will attempt a {Description} instead of (or in " +
            "addition to) serving normal traffic. Confirm this is intentional in this environment.",
            key, description);
}

// Token gate applies to /health and everything under it, ahead of auth/routing - these three
// paths are infra-facing and intentionally not part of the JWT-authenticated surface below.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/health"),
    appBuilder => appBuilder.UseMiddleware<HealthCheckTokenMiddleware>());

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Human diagnostic - every registered check, token-gated, never itself probed by Kubernetes.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => true,
    AllowCachingResponses = false,
}).AllowAnonymous();

// "Is this pod ready to take traffic" - self + the Durable Task worker's own readiness flag. Not
// SQL, not the LLM - see HealthCheckRegistration's own doc comment.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    AllowCachingResponses = false,
}).AllowAnonymous();

// "Is the process alive" - "self" only, always healthy, touches nothing external. A minimal
// inline writer, not the default JSON serializer, for the same reason the sibling RegTrack API
// switched: UIResponseWriter-style full HealthReport serialization on every few-second probe was
// observed there to cause an OutOfMemoryException under frequent polling.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    AllowCachingResponses = false,
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            report.Status == HealthStatus.Healthy ? "{\"status\":\"Healthy\"}" : "{\"status\":\"Unhealthy\"}");
    },
}).AllowAnonymous();

// [ADDED 2026-09-16, ADR-0004] The real Insights HTTP surface. Every one of these requires a
// valid, authenticated caller by default (JwtAuthRegistration's fallback policy) - IInsightsCaller
// resolves to JwtInsightsCaller, reading the already-validated ClaimsPrincipal. Tenant eligibility
// is still re-checked live, per request, inside each handler - the token is trusted for WHO is
// asking, never for WHAT they may see.
app.MapInsightsTenantEndpoints();
app.MapInsightsRunEndpoints();
app.MapInsightsReportContentEndpoints();

// Unsubscribe only - explicitly AllowAnonymous inside DigestEndpoints, protected by its own HMAC
// token. Bounce is deliberately NOT mapped here yet - see DigestEndpoints' class-level doc
// comment: it has no gate of its own today, and mapping it on a publicly reachable host would be
// an unauthenticated way to durably suppress digest email for any (customerId, userId) pair.
app.MapDigestUnsubscribeEndpoint();

app.Run();
