// Composition root for the RegTrack Insights worker.
//
// PRIVATE and queue-driven: no public endpoints, no ingress. The Insights API endpoints
// live in the existing RegTrack API, where auth already lives (CLAUDE.md 6).
//
// Deliberately thin - all logic lives in the Insights.* folders alongside this file.
//
// TODO (Phase 1d, build order step 11): register the Durable Task SQL Server provider
// here, with orchestration versioning ON from day one, then the orchestrations and
// activities from Insights.Worker. The orchestrator body must stay deterministic -
// LLM calls, GETDATE() and DB access all belong in activities.
//
// TODO (Phase 1c, step 9): the free weekly digest scheduler attaches here first.

using Insights.Data;
using Insights.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Every SQL-backed repository, registered against ConnectionStrings:RegTrack.
//
// Available to inject from here on:
//   IDictionaryRepository       sql/01  dictionary coverage + status data-quality probe
//   IScopeRepository            sql/03  scope pairs, classification, post-flight audit
//   IEntityRepository           sql/04  entity tree, reconciled rollup, tenant shape
//   IEntitlementRepository      sql/04  entitlement gate (free | paid)
//   IGoldenRegressionRepository sql/02  golden invariants
//   IDimensionRepository        sql/05, sql/07 - sql/14  the nine dimensions
//   IFreeDigestRepository       sql/06  free weekly digest gate + the 15 aggregates
//
// FOR THE FREE TIER, THE CALL ORDER IS LOAD-BEARING:
//   1. IFreeDigestRepository.EvaluateGateAsync(customerId)
//   2. only if Decision == Proceed, GetAggregatesAsync(...)
// The gate is cheapest-first so an unentitled tenant costs nothing. Aggregating first
// and checking afterwards spends exactly what the gate exists to save.
//
// Every repository method can THROW by design - scope denied, reconciliation failed,
// dictionary gap, contract violation. Those are refusals, not transient faults: catch
// them, abandon the run, and never degrade one to a warning or an empty result.
builder.Services.AddInsightsData(builder.Configuration);

// The free weekly digest - LLM client, prompt loader, writer, renderer, email sender,
// pipeline and IFreeDigestService. Must come AFTER AddInsightsData, which it depends on.
//
// IFreeDigestService is THE integration surface. RegTrack's API calls RunForTenantAsync;
// the weekly scheduler (Phase 1c step 9, still to build) calls RunWeeklyAsync. Nothing
// outside needs to know about gates, aggregates, prompts, templates or providers.
builder.Services.AddInsightsFreeDigest(builder.Configuration);

// On-demand runner. Does nothing unless FreeDigest:RunOnce=true, so a bare `dotnet run`
// starts an idle host rather than mailing anyone:
//   dotnet run -- --FreeDigest:RunOnce=true --FreeDigest:CustomerId=1403
builder.Services.AddHostedService<FreeDigestRunOnceWorker>();

var host = builder.Build();
host.Run();



