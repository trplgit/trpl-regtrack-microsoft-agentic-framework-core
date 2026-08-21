# Durable Task orchestrator — design spec

**Build order item:** Phase 1d, step 11 ("MAF workflow graph, durable on SQL Server, versioning on")
**Status:** design approved, not yet implemented
**Author:** Claude Code + developer-charannagarj, 2026-08-21

---

## 1. Problem

`ReportCompositionPipeline` (`src/RegtrackInsights/Insights.Worker/ReportCompositionPipeline.cs`)
already implements compose → reflect → narrate → reflect → publish-gate as a plain sequential
C# class. It is real, tested manually against live UAT data and a live LLM (see
`ReportHtmlAgentManualRunTests.cs`, `ReportCompositionPipelineManualRunTests.cs`), and correct.

It is not durable. If the process crashes mid-run, all progress is lost and any spent LLM tokens
are wasted. It also cannot be triggered as a background job — today it only runs from inside a
test. `Program.cs` has carried a `TODO (Phase 1d, build order step 11)` for this since the free
digest was built, and it is still there, untouched, on `development` as of this spec.

This spec covers wrapping that existing, working logic in a Durable Task orchestration, per
CLAUDE.md §6 and design doc §3.2–§3.8 (all **[LOCKED]** decisions — this spec does not revisit
them, only applies them).

## 2. Scope for this slice

Full workflow graph (design doc §3.3) has 12 nodes. This slice builds a durable orchestration for
nodes 1–11. **Node 12 (encrypt + store) is stubbed** — logs what it would persist and returns a
placeholder artifact id. Real persistence is build order item 14, a separate slice; the stub is
the seam it plugs into later.

Out of scope for this slice: item 14 (persistence), item 15 (hub UI / AG-UI progress streaming to
a real UI — the orchestration reports custom status internally, but nothing consumes it yet), item
16 (failure/refusal UX), item 17 (cost instrumentation / LangFuse — noted as a future hook point,
not built here). Each is its own later slice, in that order, per the earlier decomposition.

## 3. A correction to the design doc

Design doc §3.3's mermaid diagram draws node 7 (render) before node 8 (publish gate). The actual
code disagrees: `PublishGate.EvaluateAsync(userId, customerId, NarrativeResult narrative, ...)`
takes the narrative, not HTML, and `ReportCompositionPipeline` calls it immediately after the
narrative reflection loop — before any HTML rendering happens. `docs/API_CONTRACTS.md` §4's stage
list agrees with the code: `...composing → narrating → verifying → rendering → complete` — verify
before render.

This spec follows the code and `API_CONTRACTS.md` (gate before render), not the stale diagram.
Rationale holds up independently: there is no reason to spend render tokens on a narrative that is
about to be refused. **The main design doc's §3.3 diagram should be corrected separately** — not
part of this slice, flagged here so it isn't lost.

## 4. Where this lives

`Insights.Worker` — no new project. Durable Task is a worker concern; `ReportCompositionPipeline`
already lives here and this replaces it in place (the orchestrator becomes the thing that actually
runs; the plain class's logic moves into the orchestrator body + activities rather than staying as
a separate parallel path).

```
src/RegtrackInsights/Insights.Worker/
  Orchestration/
    InsightsReportOrchestrationInput.cs      record: TenantId, ReportType, Scope, Period, UserId
    InsightsReportOrchestrator.cs            the orchestrator function — deterministic only
    InsightsRunStatus.cs                     custom-status shape matching API_CONTRACTS.md §4
    Activities/
      GatherScopeActivity.cs                 nodes 1-2: entitlement gate + scope resolution
      FetchDimensionsActivity.cs             nodes 3-4: the 9 dimension repo calls (each already
                                              returns Assertions/Findings + has already passed its
                                              own reconciliation THROW - see DimensionRepositoryTests)
      ComposeActivity.cs                     node 5 - LLM
      ReflectOnCompositionActivity.cs        node 5r - LLM
      NarrateActivity.cs                     node 6 - LLM
      ReflectOnNarrativeActivity.cs          node 6r - LLM
      PublishGateActivity.cs                 node 8 - deterministic, the non-negotiable arbiter
      RenderHtmlActivity.cs                  node 7 - LLM (runs after the gate - see §3 above)
      NormalizeActivity.cs                   node 9 - deterministic, called twice (pre- and
                                              post-sanitize, per the loop-closing check built for
                                              item 13)
      SanitizeActivity.cs                    node 10 - deterministic (real DOMPurify, headless
                                              Chromium)
      PlaywrightQaActivity.cs                node 11 - deterministic, advisory/cosmetic only
      PersistStubActivity.cs                 node 12 STUB - logs, returns a fake artifact id
  InsightsRunOnceWorker.cs                   manual trigger, mirrors FreeDigestRunOnceWorker
```

`WorkerRegistration.cs` gains the Durable Task client/worker registration and activity
registrations, added after `AddInsightsData` (activities depend on the same repositories) and
after the existing `PublishGate` registration (reused, not duplicated).

## 5. The orchestrator body

Deterministic only — no LLM calls, no `GETDATE()`, no direct DB access, only
`context.CallActivityAsync<T>(...)` and plain C# control flow, per CLAUDE.md §6 / design doc §3.7.
This mirrors `ReportCompositionPipeline.RunAsync` almost exactly; the reflection loops move over
unchanged, because a bounded loop with an approve/revise branch over activity results is exactly
what an orchestrator body is allowed to contain.

```
context.SetCustomStatus(stage: "gathering", stagesComplete: 0, stagesTotal: 7)
scope = await CallActivityAsync(GatherScopeActivity, input)
  -> empty scope: throw OrchestrationRefusedException("SCOPE_DENIED")  (§11.2 secure-deny)

context.SetCustomStatus(stage: "validating", ...)
dimensionData = await CallActivityAsync(FetchDimensionsActivity, scope)
  -> assertions/findings already attached per dimension

context.SetCustomStatus(stage: "composing", ...)
plan = await CallActivityAsync(ComposeActivity, dimensionData)
for i in 0..MaxReflectionIterations:
    reflection = await CallActivityAsync(ReflectOnCompositionActivity, plan)
    if reflection.Verdict == Approve: break
    plan = await CallActivityAsync(ComposeActivity, dimensionData, plan, reflection.Issues)

context.SetCustomStatus(stage: "narrating", ...)
narrative = await CallActivityAsync(NarrateActivity, plan)
for i in 0..MaxReflectionIterations:
    reflection = await CallActivityAsync(ReflectOnNarrativeActivity, narrative)
    if reflection.Verdict == Approve: break
    narrative = await CallActivityAsync(NarrateActivity, plan, narrative, reflection.Issues)

context.SetCustomStatus(stage: "verifying", ...)
gateResult = await CallActivityAsync(PublishGateActivity, narrative)
  -> not approved: throw OrchestrationRefusedException(gateResult) - REFUSAL PATH, never publish

context.SetCustomStatus(stage: "rendering", ...)
html = await CallActivityAsync(RenderHtmlActivity, plan, narrative)
html = await CallActivityAsync(NormalizeActivity, html)
  -> unnormalizable: throw OrchestrationRefusedException("NOT_NORMALIZABLE")
html = await CallActivityAsync(SanitizeActivity, html)
html = await CallActivityAsync(NormalizeActivity, html)   -- the post-sanitize re-check, item 13
  -> re-check failure: throw OrchestrationRefusedException("POST_SANITIZE_VIOLATION")
qaResult = await CallActivityAsync(PlaywrightQaActivity, html)   -- advisory, never throws

context.SetCustomStatus(stage: "complete", stagesComplete: 7, stagesTotal: 7)
artifact = await CallActivityAsync(PersistStubActivity, html)
return artifact
```

**Correction, 2026-08-21 (see §11 below):** the `context.SetCustomStatus(...)` calls shown above
describe the newer portable SDK's API, not classic DTFx's. DTFx exposes custom status via
overriding `TaskOrchestration.GetStatus() : string` instead — the orchestrator tracks its own
current stage in a private field, updated at each stage transition, and `GetStatus()` returns it
serialized. Same observable effect (`TaskHubClient.GetOrchestrationStateAsync(...).Status` carries
the current stage, matching §6's shape below), different mechanism. See the implementation plan's
Task 14 for the corrected code.

Every `OrchestrationRefusedException` carries a typed reason. The orchestrator lets it propagate;
Durable Task marks the instance `failed`. Item 16 (failure/refusal UX) later maps these reasons to
the four failure classes (§11) and user-safe messages — out of scope here, but the reason codes
this slice throws are exactly what that later work will switch on, so name them deliberately now
(`SCOPE_DENIED`, `GATE_REFUSED`, `NOT_NORMALIZABLE`, `POST_SANITIZE_VIOLATION`) rather than leaving
them as free-text.

## 6. Input / status contract

Matches `docs/API_CONTRACTS.md` exactly, so nothing about the orchestration's public shape changes
when the real API endpoint (in the other repo) eventually enqueues it for real — only the
enqueue mechanism changes (CLI flag today, HTTP `POST /api/insights/reports` later).

```csharp
public sealed record InsightsReportOrchestrationInput(
    int TenantId, string ReportType, InsightsScopeRequest Scope, string Period, int UserId);
```

`InsightsScopeRequest` does **not** exist yet — checked against `Insights.Domain`, which only has
`ScopePair` (a single *resolved* branch/category pair, the output of scope resolution) and
`ScopeClassification`. Neither shape matches `API_CONTRACTS.md` §3's request body
(`{ "type": "tenant" }` / presumably `{ "type": "entity", "entityId": ... }` for entity-scoped
requests — the doc only shows the `tenant` case). `InsightsScopeRequest` is a **new** record this
slice defines in `Insights.Domain`, representing what the caller asked for, before
`GatherScopeActivity` resolves it into the real `IReadOnlyList<ScopePair>` via
`IScopeRepository`. Exact shape (discriminated union vs. nullable `EntityId` field) to be settled
during implementation against the full request grammar — `API_CONTRACTS.md` only documents the
`tenant`-scope case explicitly.

Custom status shape (what `GET /api/insights/runs/{runId}/stream` will read once item 15 exists):
```jsonc
{ "stage": "composing", "stagesComplete": 4, "stagesTotal": 7 }
```
Stage values: `gathering`, `validating`, `composing`, `narrating`, `verifying`, `rendering`,
`complete` — the 7 names `API_CONTRACTS.md` §4 already locked. Terminal: `complete` or `failed`.

## 7. Idempotency

Per design doc §3.7: on a mid-activity crash, Durable Task may re-run that activity on resume.
Stored-proc and scope-resolution activities are naturally idempotent (pure reads). **LLM
activities are not** — a replay must not double-bill tokens. Each LLM activity keys its result by
`(run_id, node_id)` and checks a cache before calling the LLM; `run_id` comes from
`context.InstanceId`, `node_id` is the activity's own name. This is a hard requirement, not an
optimization — CLAUDE.md §6 states it directly.

## 8. Task hub

Dedicated SQL Server database, separate from `vitComplianceSystem` — fills the currently-empty
`ConnectionStrings:DurableTaskHub` stub already sitting in `appsettings.json`. Isolation over
convenience: zero risk to the compliance schema, trivially resettable in dev.

## 9. Versioning

**On from day one**, per design doc §3.7 [LOCKED] — non-negotiable, not a nice-to-have added
later. In-flight orchestration instances complete on the code version they started with; new
instances use current code. Without this, a worker redeploy while a run is in flight throws a
non-determinism error on replay, which is worse than not being durable at all.

## 10. Trigger (this slice only)

`InsightsRunOnceWorker` — a hosted service, inert unless `Insights:RunOnce=true`, mirroring
`FreeDigestRunOnceWorker`'s exact shape:
```
dotnet run -- --Insights:RunOnce=true --Insights:TenantId=29 --Insights:UserId=38 --Insights:ReportType=compliance_health
```
Starts one orchestration instance via the Durable Task client, polls/prints stage transitions to
console, waits for terminal status, prints the result. This is the only way to run a report until
item 15 exists — no scheduler, no real API endpoint, by design (both are later slices).

## 11. Package decision — resolved 2026-08-21

`Directory.Packages.props` had the Durable Task SQL Server provider as an unpinned comment:
`Microsoft.DurableTask.Worker / .Client + the SQL Server provider`. That phrasing conflates two
incompatible families — verified via Microsoft's own docs, not guessed:

- `Microsoft.DurableTask.Worker`/`.Client` is the newer **portable SDK**. It talks to a gRPC
  sidecar, and that sidecar is **exclusively Azure Durable Task Scheduler** — Microsoft's docs
  state directly that these SDKs do not support alternative storage backends such as SQL Server.
- The real, current, actively-maintained SQL Server provider is **`Microsoft.DurableTask.SqlServer`
  1.7.0** on NuGet, and it targets the **classic `DurableTask.Core` ("DTFx") programming model** —
  `TaskHubWorker` / `TaskHubClient` — not the portable SDK.

**Decision: classic DTFx.** It is the only path that satisfies the already-[LOCKED] "SQL Server,
not Azure DTS, not Postgres" constraint for a self-hosted plain .NET 8 worker. The alternative —
Durable Functions with an MSSQL backend — requires adopting the Azure Functions host/runtime,
which nothing in CLAUDE.md or this codebase asks for (`Program.cs` uses a plain
`Host.CreateApplicationBuilder`, matching every other worker in this repo).

**Consequence for orchestration versioning (§9):** DTFx's `TaskHubWorker` supports registering
multiple `Name`+`Version` pairs for the same orchestration natively — confirmed via Microsoft's
orchestration-versioning documentation. This satisfies the [LOCKED] "versioning on from day one"
requirement directly, with no preview feature needed.

**Consequence for hosting:** DTFx's `TaskHubWorker`/`TaskHubClient` are not natively
`IHostedService`-shaped. The plan wraps them in a small `IHostedService` (~30 lines, own code —
not the community `durabletask-hosting` wrapper, to avoid a third-party dependency on the
orchestrator's core hosting path) that starts `TaskHubWorker` on `StartAsync` and stops it on
`StopAsync`, matching how every other long-lived singleton in this codebase already starts
(e.g. `IBrowser` in §... — see the implementation plan for the exact shape).

Packages to pin in `Directory.Packages.props`:
- `Microsoft.DurableTask.SqlServer` 1.7.0 (or whatever is current at implementation time — check
  NuGet before pinning, this spec's version number is a snapshot from 2026-08-21, not a promise)
- `DurableTask.Core` (transitive via the above, but pin explicitly per this repo's central package
  management convention — every other package here is pinned directly, not left transitive)

## 12. Testing

Mirrors the existing `*ManualRunTests.cs` pattern used throughout item 12/13: a manual integration
test, not part of the default suite, that starts a real (local or dev) Durable Task host against
the dedicated task-hub DB, real UAT dimension data, and a real LLM call; starts one orchestration;
asserts it reaches `complete`; asserts the stub artifact id comes back. Per CLAUDE.md §11, run
against at least two tenants with different profiles once the happy path works — tenant 23 (the
one everything so far has used) and tenant 29 (89% under a soft-deleted parent — already validated
today as a real cross-tenant data point for the composition/narrative/render chain, good candidate
to also be the second orchestrator validation).

Unit-testable in isolation without a real Durable Task host: the orchestrator body's control flow
(reflection loop bounds, refusal branching) can be tested using Durable Task's built-in
orchestration-testing harness (`TestOrchestrationContext` or equivalent) with mocked activity
results — confirms the loop/branch logic without spending real tokens on every test run. Exact
harness API to be confirmed against whatever package version lands per §11.

## 13. Future hook points (not built in this slice)

- **LangFuse**: each LLM activity is a natural place to open an OTel span once
  `Otel:LangfuseEndpoint` + keys are available (blocked on Vinay providing them — see chat
  2026-08-21). Not wired here; noted so the activity boundaries aren't accidentally drawn in a way
  that makes this harder later.
- **Item 14 (persistence)**: `PersistStubActivity` is the exact seam — swap its body for real
  blob-write + SQL index-row + envelope encryption, nothing else in the graph changes.
- **Item 16 (failure UX)**: the four `OrchestrationRefusedException` reason codes from §5 are
  what that work switches on.
