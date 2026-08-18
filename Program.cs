// Composition root for the RegTrack Insights worker.
//
// PRIVATE and queue-driven: no public endpoints, no ingress. The Insights API endpoints
// live in the existing RegTrack API, where auth already lives (CLAUDE.md 6).
//
// Deliberately thin - all logic lives in the src\Insights.* libraries.
//
// TODO (Phase 1d, build order step 11): register the Durable Task SQL Server provider
// here, with orchestration versioning ON from day one, then the orchestrations and
// activities from Insights.Worker. The orchestrator body must stay deterministic -
// LLM calls, GETDATE() and DB access all belong in activities.
//
// TODO (Phase 1c, step 9): the free weekly digest scheduler attaches here first.

var builder = Host.CreateApplicationBuilder(args);

// builder.Services.AddHostedService<InsightsWorker>();

var host = builder.Build();
host.Run();
