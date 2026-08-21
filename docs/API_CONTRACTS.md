# API Contracts — RegTrack Insights

**Where these live:** the **existing RegTrack .NET 8 API**, not the worker.
Authentication already lives there; duplicating it in a second service is how auth
bugs get introduced. The Insights worker has **no public surface** (spec §3.6).

Base path: `/api/insights`

---

## Cross-cutting rules — apply to every endpoint

### 1. Tenant is always an explicit, server-validated parameter

> **[TRAP — IDOR]** The client supplies `tenantId`. **The server must
> independently re-derive the caller's eligible tenant set and reject anything
> outside it, on every single request.** Never cache the selection in session
> state and trust it later. If the backend trusts a client-supplied id, a user
> passes an arbitrary value and receives another customer's report — the worst
> possible defect in this feature. (Spec §5.6.2)

**Eligible** requires all three:
1. `Customer.IsDeleted = 0`
2. RegInsights product mapped **and enabled** (`ProductMapping.IsActive = 0` — inverted)
3. The user has scope rows (`EntitiesAssignment`) for that customer

### 2. Scope is re-resolved per request
Never cached across requests. Never carried across a tenant switch.

### 3. Standard error envelope
```jsonc
{ "error": { "code": "SCOPE_DENIED", "message": "No entities are currently in your Insights scope." } }
```

| Code | HTTP | Meaning |
|---|---|---|
| `TENANT_NOT_ELIGIBLE` | 403 | Tenant not in the caller's eligible set |
| `SCOPE_DENIED` | 403 | Entitled but zero scope pairs — surface the §11.2 message, **not** an empty report |
| `COOLDOWN_ACTIVE` | 409 | Includes `nextAvailableUtc` |
| `REPORT_NOT_VISIBLE` | 404 | `report_scope ⊄ viewer_scope`. **404, not 403** — do not reveal existence |
| `GENERATION_FAILED` | 200 on the job, not the request | Surfaced via job status |

---

## 1. List eligible tenants

```
GET /api/insights/tenants
```

```jsonc
{
  "tenants": [
    { "tenantId": 1490, "name": "…", "tier": "pro",   "scopeClass": "tenant_wide" },
    { "tenantId": 2684, "name": "…", "tier": "basic", "scopeClass": "functional" }
  ]
}
```

`scopeClass`: `tenant_wide` | `functional` | `entity_scoped`.

> Client behaviour: **0** → secure-deny state. **1** → skip the picker entirely.
> **>1** → show the picker. Never make a single-tenant user click. (§5.6.3)

---

## 2. Report history

```
GET /api/insights/reports?tenantId={id}&reportType={type}
```

```jsonc
{
  "reports": [
    { "reportId": "…", "reportType": "compliance_health",
      "scopeDescriptor": "entity:92442", "scopeLabel": "Aquarelle",
      "period": "FY2025-26", "generatedAtUtc": "…",
      "generatedByName": "…", "status": "complete" }
  ],
  "cooldown": { "isOpen": false, "nextAvailableUtc": "…" }
}
```

Scope-filtered by the subset test `report_scope ⊆ viewer_scope`, evaluated **at
request time against current entitlements** — so a user whose scope was reduced
immediately stops seeing reports covering the removed scope. (§2.3, closes D4.)

---

## 3. Generate

```
POST /api/insights/reports
{ "tenantId": 1490, "reportType": "compliance_health",
  "scope": { "type": "tenant" }, "period": "FY2025-26" }
```

→ `202 Accepted`
```jsonc
{ "runId": "…", "status": "queued", "streamUrl": "/api/insights/runs/{runId}/stream" }
```

Server sequence:
1. Validate tenant eligibility (IDOR check)
2. Resolve scope; empty ⇒ `SCOPE_DENIED`
3. Check cooldown for `(scope, reportType, period)`; closed ⇒ `409` + `nextAvailableUtc`
4. Acquire the one-active-run-per-key lock
5. Enqueue the Durable Task orchestration (~1 ms) and return

> The cooldown is **consumed only on a fully successful, published report** — every
> failure class leaves the window open. (§11.5)

---

## 4. Run progress

```
GET /api/insights/runs/{runId}/stream        (SignalR / SSE)
```

```jsonc
{ "runId": "…", "status": "running",
  "stage": "composing", "stagesComplete": 4, "stagesTotal": 7 }
```

Stages: `gathering` → `validating` → `composing` → `narrating` → `verifying` →
`rendering` → `complete`.

Terminal: `complete` | `failed`. On `failed`, include a **user-safe** message
only — never leak gate diagnostics such as "reconciliation variance of 3 on
branch X". Internal detail goes to LangFuse/Grafana and alerts the team. (§11.3)

---

## 5. Open a report

```
GET /api/insights/reports/{reportId}/content?tenantId={id}
```

```jsonc
{ "contentUrl": "https://…blob…?sv=…", "expiresUtc": "…", "sandboxRequired": true }
```

Server sequence:
1. Validate tenant eligibility
2. Re-resolve scope **now**; enforce `report_scope ⊆ viewer_scope`, else `404`
3. Decrypt via the DocAI envelope pattern (Key Vault)
4. Mint a **short-lived, single-use SAS** (minutes)
5. Write an access-audit row (who / which report / when)

> **The blob is never directly reachable.** No public URL, no long-lived SAS.
> The client must render the content in a **sandboxed iframe with
> `sandbox="allow-scripts"` and NO `allow-same-origin`**, under the CSP in §8.2.
> `sandboxRequired: true` is a contract term, not a hint. (§9.3)

---

## Angular notes

- Tenant context belongs in a shared service; **switching tenants must clear all
  cached scope and report state.**
- Render report content **only** in the sandboxed iframe — never `innerHTML`, never
  `bypassSecurityTrustHtml`.
- Inspect `trpl-regtrack-angular-web` and match its existing module, routing, and
  state conventions. Do not introduce a parallel pattern.
