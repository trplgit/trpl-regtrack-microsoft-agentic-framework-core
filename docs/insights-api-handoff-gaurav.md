# RegTrack Insights — API handoff (for Gaurav)

**Date:** 2026-09-11
**Branch:** `reginsights-staging` (both Charan's and Tanvi's work merged, tests green)

## What this is

Three HTTP endpoints that let RegTrack's Angular frontend generate, poll, and open
AI-written compliance reports for a tenant. The endpoint code is real, tested, and
proven tonight end-to-end against real UAT data — not a mock or a stub.

## Where the code lives

`src/RegtrackInsights/Insights.Api/*.cs` — copy this folder's contents into the real
RegTrack API repo as-is. It's plain ASP.NET Core minimal-API endpoint code with no
dependency on anything worker-specific; it only talks to the shared domain/data
libraries (`Insights.Domain`, `Insights.Data`), which either already exist in your
repo or need to come along too — see "What you need to wire" below.

Full request/response reference: **`docs/INSIGHTS_API_ENDPOINTS.md`** (updated
tonight with the new `reqId` endpoint — read that file for exact JSON shapes,
status codes, and error envelopes). This doc is the short version.

## The endpoints

| # | Method + path | What it does |
|---|---|---|
| 1 | `GET /api/insights/tenants` | List tenants the caller may generate for |
| 3 | `POST /api/insights/reports` | Generate — enqueues 1..N reports, returns `reqId` + per-dimension `runId`s |
| 4 | `GET /api/insights/runs/{runId}/stream` | Progress for ONE report (SSE) |
| 4a | `GET /api/insights/requests/{reqId}/stream` | Combined progress for the WHOLE batch (SSE) — new tonight, poll this instead of tracking every `runId` yourself |
| 5 | `GET /api/insights/reports/{reportId}/content?tenantId={id}` | Get a short-lived (10 min) link to view a finished report |

(#2, report history, is contracted but not yet implemented — see the full reference.)

## Proven for real tonight, not just unit-tested

Ran the complete pipeline against real UAT tenants (Minda Corporation Group,
Agrocel Group) through these exact endpoints:
- Real SQL data → real Claude narrate/render calls → real encryption → real save
  → real decrypted view link, opened and readable.
- The `reqId` fan-out endpoint tested against real mixed-state batches (one
  dimension failing while others succeed) — confirmed it reports `error`
  immediately rather than hiding it.

## What you need to wire on your side

1. **Real auth.** Our local dev host stands in for auth with a plain
   `X-Insights-UserId` header — that is a dev-only shim, remove it. Your endpoint
   integration needs `IInsightsCaller.UserId` to come from RegTrack's real
   authenticated principal, not a header.
2. **Real connection strings**, not the UAT ones we tested with tonight:
   `ConnectionStrings:RegTrack`, `ConnectionStrings:DurableTaskHub`,
   `Azure:BlobConnectionString`, `Azure:TempBlobContainer` (new tonight — a
   dedicated container for decrypted view-copies, separate from the permanent
   encrypted one; see the container note below).
3. **A deployed worker.** These endpoints only *enqueue* — nothing processes a
   queued report unless the worker (`src/RegtrackInsights`, the rest of this repo
   outside `Insights.Api/`) is running somewhere as its own long-lived service.
   **As of tonight, nothing is deployed anywhere** — that's a separate,
   still-open task, not something these endpoints do for you.

## Known, deliberate limitations (not bugs)

- View links expire in 10 minutes (`Reports:SasLifetimeMinutes`) — by design,
  short-lived, freshly minted per request.
- Some reports occasionally get refused with a generic "couldn't generate this
  report to our accuracy standard" message — that's the real quality gate working
  (deterministic structure/reconciliation checks), not an infra failure. Retrying
  usually succeeds; it's LLM output variance, not a systemic problem.
- A handful of composite-score components (Evidence, Timeliness on some tabs) are
  correctly *omitted* rather than shown — there's no real data source for them
  yet on any tenant tested. Never a placeholder, just absent.

## Postman

`docs/postman/RegTrack-Insights.postman_collection.json` +
`RegTrack-Insights.postman_environment.json` — real examples, tenant 1300/user
22426 (the one test tenant with real Pro entitlement in UAT).

## Questions

Ping Charan or Tanvi on this branch — both have been running real traffic through
these exact endpoints tonight and can answer anything the reference doc doesn't
cover.
