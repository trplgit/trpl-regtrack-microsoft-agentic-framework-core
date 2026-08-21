// Composition root for the RegTrack Insights worker.
//
// PRIVATE and queue-driven: no public endpoints, no ingress. The Insights API endpoints live in
// the existing RegTrack API, where auth already lives (CLAUDE.md 6). Their source is in
// Insights.Api/ for that repo to take.
//
// TODO (Phase 1d, build order step 11): register the Durable Task SQL Server provider here, with
// orchestration versioning ON from day one, then the orchestrations and activities from
// Insights.Worker. The orchestrator body must stay deterministic - LLM calls, GETDATE() and DB
// access all belong in activities.

using Insights.Data;
using Insights.Worker;

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

// The free weekly digest (Phase 1c): LLM client, prompt loader, writer, renderer, email sender,
// pipeline, IFreeDigestService, and the weekly scheduler. Must come AFTER AddInsightsData.
//
// The scheduler is the production trigger - weekly, staggered by hash(tenantId) % 7, lowest
// priority lane (design doc 10.3). Inert unless FreeDigest:Schedule:Enabled is true.
builder.Services.AddInsightsFreeDigest(builder.Configuration);

// The publish gate (build order step 8) - reconciliation, claim-checker, scope post-flight audit.
builder.Services.AddInsightsWorker();

// One-shot runner for testing a single tenant from the command line. Does nothing unless
// FreeDigest:RunOnce=true:
//   dotnet run -- --FreeDigest:RunOnce=true --FreeDigest:CustomerId=23
builder.Services.AddHostedService<FreeDigestRunOnceWorker>();

var host = builder.Build();
host.Run();
