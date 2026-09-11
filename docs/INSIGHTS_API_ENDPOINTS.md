# RegTrack Insights — API endpoint reference

Companion to `API_CONTRACTS.md`. This file is the concrete, request-by-request view
(paths, params, bodies, responses, status codes) for wiring a client or a Postman
collection. `API_CONTRACTS.md` remains the source of truth for *why*.

- **Host:** the existing RegTrack .NET 8 API (not the Insights worker — it has no public surface).
- **Base path:** `/api/insights`
- **Auth:** the host's existing RegTrack authentication (bearer token / session). Every
  authenticated endpoint derives the caller from the validated principal
  (`IInsightsCaller.UserId`) — **never** from a query string, header, or body.
- **Tenant is always an explicit, server-validated parameter.** The client sends `tenantId`;
  the server re-derives the caller's eligible tenant set every request and rejects anything
  outside it (IDOR guard).

## Standard error envelope

```jsonc
{ "error": { "code": "SCOPE_DENIED", "message": "No entities are currently in your Insights scope." } }
```

| Code | HTTP | Meaning |
|---|---|---|
| `TENANT_NOT_ELIGIBLE` | 403 | Tenant not in the caller's eligible set |
| `SCOPE_DENIED` | 403 | Entitled to the tenant but zero scope pairs |
| `COOLDOWN_ACTIVE` | 409 | **Legacy shape, kept for reference only** — cooldown is now reported per-report inside a 202 response (see endpoint 3), never as a whole-request 409 |
| `NO_DIMENSIONS_REQUESTED` | 400 | `reportType: "dimension_selection"` with an empty/missing `requestedDimensions` |
| `REPORT_NOT_VISIBLE` | 404 | Report not found, wrong tenant, or `report_scope ⊄ viewer_scope`. **404, never 403** — existence is not revealed |
| `GENERATION_FAILED` | — | Not on the HTTP request; surfaced via the run-status stream as `status: "failed"` |

---

## 1. List eligible tenants

```
GET /api/insights/tenants
```

Auth required. No parameters.

**200**
```jsonc
{
  "tenants": [
    { "tenantId": 1490, "name": "Acme Ltd",  "tier": "pro",   "scopeClass": "tenant_wide" },
    { "tenantId": 2684, "name": "Beta Corp",  "tier": "basic", "scopeClass": "functional" }
  ]
}
```

- `tier`: `pro` (paid) | `basic` (free).
- `scopeClass`: `tenant_wide` | `functional` | `entity_scoped`.
- **Empty array is a 200**, not an error — it is the secure-deny state. Client behaviour:
  0 → deny screen, 1 → skip the picker, >1 → show the picker.

---

## 2. Report history

```
GET /api/insights/reports?tenantId={id}&reportType={type}
```

Auth required.

| Param | In | Type | Required | Notes |
|---|---|---|---|---|
| `tenantId` | query | int | yes | server-validated against the caller's eligible set |
| `reportType` | query | string | yes | `fixed_holistic` \| `dimension_selection` |

**200**
```jsonc
{
  "reports": [
    { "reportId": "6f1c…", "reportType": "fixed_holistic",
      "scopeDescriptor": "entity:92442", "scopeLabel": "Aquarelle",
      "period": "FY2025-26", "generatedAtUtc": "2026-09-10T08:16:00Z",
      "generatedByName": "R. Menon", "status": "complete" }
  ],
  "cooldown": { "isOpen": false, "nextAvailableUtc": "2026-10-10T08:16:00Z" }
}
```

Rows are filtered by `report_scope ⊆ viewer_scope` evaluated **now** against live entitlements —
a user whose scope was reduced immediately stops seeing reports covering the removed scope.

> **STATUS: contracted in `API_CONTRACTS.md` §2, NOT yet implemented in the API.** No `MapGet`
> route exists for it today (only tenants, POST /reports, the run stream, and content). Ship it
> alongside the hub UI (build-order item 15).

---

## 3. Generate a report

```
POST /api/insights/reports
```

Auth required.

**Body**
```jsonc
{
  "tenantId": 1490,
  "reportType": "fixed_holistic",
  "scope": { "type": "tenant" },
  "period": "FY2025-26",
  "requestedDimensions": null
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `tenantId` | int | yes | server-validated |
| `reportType` | string | yes | `fixed_holistic` \| `dimension_selection` |
| `scope` | object | yes | `{ "type": "tenant" }` or `{ "type": "entity", "entityId": 92442 }` |
| `period` | string | yes | e.g. `FY2025-26` |
| `requestedDimensions` | string[] \| null | no | **only** for `dimension_selection` — the dimensions to generate (e.g. `["Location","Nature","Act"]`). At least one required for this report type. Omit/`null` for `fixed_holistic`. |

> **[PRODUCT DECISION 2026-09-11] Picking N dimensions produces N INDEPENDENT reports, not
> one combined document.** Each requested dimension becomes its own orchestration run, its own
> cooldown key, its own blob, its own `GeneratedReport` row — exactly as if the client had called
> this endpoint once per dimension itself, except the server does the splitting for you in one
> call. Picking `"Entity"` (alone or alongside others) still redirects that ONE unit to a full
> `fixed_holistic` report (Entity has no dimension-specific template) — every other requested
> dimension is completely unaffected by that redirect.

**Server sequence, per requested dimension (or once, for `fixed_holistic`):** validate tenant
eligibility → resolve scope (empty → `SCOPE_DENIED`, whole request) → resolve Entity redirect →
cooldown check for `(scope, resolvedReportType, period+dimension)` → if open, acquire the
one-active-run-per-key lock and enqueue; if closed, report `"cooldown"` for that dimension only.
**Best-effort, not all-or-nothing** — one dimension on cooldown does not block the others.

**202 Accepted** — always `{ "reports": [...] }`, one entry per requested dimension (or a single
one-element array for `fixed_holistic`/a single dimension — the shape never changes):
```jsonc
{
  "reports": [
    { "dimension": "Location", "reportType": "dimension_selection", "status": "queued",
      "runId": "insights-1490-...", "streamUrl": "/api/insights/runs/insights-1490-.../stream" },
    { "dimension": "Entity", "reportType": "fixed_holistic", "status": "queued",
      "runId": "insights-1490-...", "streamUrl": "/api/insights/runs/insights-1490-.../stream" },
    { "dimension": "Act", "reportType": "dimension_selection", "status": "cooldown",
      "nextAvailableUtc": "2026-10-08T00:00:00Z" }
  ]
}
```

| Field | Present when | Notes |
|---|---|---|
| `dimension` | always | the ORIGINAL caller-named dimension, `null` for a plain `fixed_holistic` request. Still `"Entity"` even when `reportType` below reads `"fixed_holistic"` for that entry — use this to map a result back to the checkbox the user ticked. |
| `reportType` | always | the RESOLVED report type this unit actually runs as (post Entity-redirect) |
| `status` | always | `"queued"` or `"cooldown"` — a genuine infra failure to enqueue still fails the whole HTTP request (500), it is not modelled as a per-item status |
| `runId` / `streamUrl` | `status: "queued"` | poll/stream this the same way a single-report response always worked (endpoint 4) |
| `nextAvailableUtc` | `status: "cooldown"` | when this dimension's window reopens |

- Each `runId` is **deterministic** from `(tenant, scope, resolvedReportType, period+dimension)` —
  a second identical request attaches to the already-running instance instead of starting a
  duplicate, per dimension independently.
- The cooldown is consumed **only** on a fully successful, published report; every failure leaves
  that dimension's window open.

**400** — `NO_DIMENSIONS_REQUESTED` (`dimension_selection` with an empty/missing list) — the whole
request is refused before anything is enqueued.
**403** — `TENANT_NOT_ELIGIBLE` / `SCOPE_DENIED` — whole-request refusals; nothing was generated.

---

## 4. Run progress (stream)

```
GET /api/insights/runs/{runId}/stream
```

Auth required. Server-Sent Events / SignalR. The connection is capped at **15 minutes**; the run
continues regardless — the client just reconnects and picks it up (progress lives in the instance
store, not the connection).

**Event payload**
```jsonc
{ "runId": "t1490-…", "status": "running",
  "stage": "composing", "stagesComplete": 4, "stagesTotal": 7 }
```

- `stage`: `gathering` → `validating` → `composing` → `narrating` → `verifying` → `rendering` → `complete`
- Terminal `status`: `complete` | `failed`.
- On `failed`, `message` is **user-safe only** — never gate diagnostics. Internal detail goes to
  LangFuse / Grafana.
- On `complete`, the payload carries the new `reportId` — hand it to endpoint 5.

`runId` is not a bare id — `InsightsRunId.TryParse` recovers the tenant from it, and the endpoint
authorises against that before streaming.

---

## 5. Open a report (content)

```
GET /api/insights/reports/{reportId}/content?tenantId={id}
```

Auth required.

| Param | In | Type | Required |
|---|---|---|---|
| `reportId` | path | GUID | yes |
| `tenantId` | query | int | yes |

**Server sequence:** tenant eligibility → re-resolve the viewer's scope **now**, enforce
`report_scope ⊆ viewer_scope` → decrypt the blob (DocAI envelope, Key Vault) → mint a
**short-lived, freshly-generated read SAS** against a throwaway decrypted copy → write an
access-audit line.

**200**
```jsonc
{
  "contentUrl": "https://…blob…?sv=…&se=…&sig=…",
  "expiresUtc": "2026-09-10T08:21:00Z",
  "sandboxRequired": true
}
```

- `contentUrl` is a fresh URL every call — nothing is stored, nothing is reused.
- **`sandboxRequired: true` is a contract term.** The client MUST render `contentUrl` **only**
  in `<iframe sandbox="allow-scripts">` with **no** `allow-same-origin`, under the CSP in
  `API_CONTRACTS.md` §8.2. Never `innerHTML`, never `bypassSecurityTrustHtml`.

**404** — `REPORT_NOT_VISIBLE` for every refusal reason (no such report / wrong tenant / scope no
longer covers it) — the endpoint cannot be used to probe which report ids exist.
**403** — `TENANT_NOT_ELIGIBLE`.

---

## 6. Free digest — unsubscribe

```
GET /api/insights/digest/unsubscribe?c={customerId}&u={userId}&t={token}
```

**No login** (email clients cannot send auth). Safety is the `t` token — an HMAC over
`(customer, user)` issued when the mail was sent.

| Param | Type | Notes |
|---|---|---|
| `c` | int | customer id (`> 0`) |
| `u` | long | user id (`> 0`) |
| `t` | string | HMAC token; verified against `Email:UnsubscribeSigningKey` |

**200** `{ "message": "You have been unsubscribed…" }` · **400** invalid ids or bad token ·
**500** signing key not configured.

---

## 7. Free digest — bounce webhook

```
POST /api/insights/digest/bounce
```

Posted by the mail provider (no RegTrack account) — protect it the way the host protects its
other inbound webhooks.

**Body**
```jsonc
{ "customerId": 23, "userId": 4471, "bounceType": "hard", "reason": "550 mailbox unavailable" }
```

Only `bounceType: "hard"` suppresses the recipient (durable). A soft bounce is recorded and
ignored. **200** either way; **400** for invalid ids.

---

## Report types

| `reportType` | Composition | LLM steps | Notes |
|---|---|---|---|
| `fixed_holistic` | deterministic C# (`FixedHolisticComposition`) | narrate, reflect, render | 6 fixed tabs; matches `holistic-insights-tenant1300.html` |
| `dimension_selection` | deterministic C# (`DimensionSelectionComposition`) | narrate, reflect, render | ONE dimension per orchestration run (the API fans a multi-dimension request out into several of these, see endpoint 3); `requestedDimensions` required, always a single element by the time this runs |

> `compliance_health` (the original dynamic, LLM-composed path) was removed 2026-09-11 - these two
> are the only report types now.

## CLI (manual/local alternative to the API)

Run one paid report end-to-end without going through the HTTP endpoint - always exactly one
dimension per invocation, same as one unit of the API's own fan-out:

```
dotnet run --project src/RegtrackInsights -- \
  --Insights:RunOnce=true \
  --Insights:TenantId=1300 --Insights:UserId=22426 \
  --Insights:ReportType=fixed_holistic --Insights:Period=FY2025-26
# dimension_selection:
  --Insights:ReportType=dimension_selection --Insights:Dimensions=Location,Nature,Act
```

Needs `ConnectionStrings:RegTrack`, `ConnectionStrings:DurableTaskHub`, `Azure:BlobConnectionString`,
`Azure:KeyVaultUri`, and the `Llm:Maf:*` settings.
