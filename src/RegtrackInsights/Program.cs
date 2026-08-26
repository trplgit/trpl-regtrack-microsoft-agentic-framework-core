// Composition root for the RegTrack Insights worker.
//
// PRIVATE and queue-driven: no public endpoints, no ingress. The Insights API endpoints live in
// the existing RegTrack API, where auth already lives (CLAUDE.md 6). Their source is in
// Insights.Api/ for that repo to take.
//
using Insights.Data;
using Insights.Worker;
using Insights.Worker.Orchestration;

var builder = Host.CreateApplicationBuilder(args);

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
builder.Services.AddInsightsPaidReportAgents(builder.Configuration);
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

// One-shot runner for testing a single tenant from the command line. Does nothing unless
// FreeDigest:RunOnce=true:
//   dotnet run -- --FreeDigest:RunOnce=true --FreeDigest:CustomerId=23
builder.Services.AddHostedService<FreeDigestRunOnceWorker>();

// One-shot runner for the paid orchestrator. Does nothing unless Insights:RunOnce=true:
//   dotnet run -- --Insights:RunOnce=true --Insights:TenantId=29 --Insights:UserId=38
builder.Services.AddHostedService<InsightsRunOnceWorker>();

var host = builder.Build();
host.Run();
