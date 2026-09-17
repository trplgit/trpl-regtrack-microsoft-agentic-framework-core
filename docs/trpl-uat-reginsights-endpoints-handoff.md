# TRPL UAT RegInsights — Endpoint Handoff

**Base URL:** `https://uatreginsights.internal.teamleaseregtech.com`
**Postman:** import `docs/postman/trpl-uat-reginsights-endpoints-collection.postman_collection.json` and `docs/postman/trpl-uat-reginsights-endpoints-environment.postman_environment.json`, then select the "TRPL UAT RegInsights" environment.

This doc describes what's actually deployed and verified working on UAT right now. If anything here conflicts with an older doc in this repo, this one is current — refer to the code (`src/RegtrackInsights/Insights.Api/`) as the ultimate source of truth, not older docs.

---

## 1. The flow — do this, in order

1. **Call `List_Tenants`** to confirm your token is valid and see which tenants you're eligible for.
2. **Call `Generate_Report_Single` or `Generate_Report_MultiDimension`** to start a report. You get back a `runId` per report and a `streamUrl`.
3. **Immediately subscribe to `Run_Status_Stream`** using that `runId`. This is a Server-Sent Events connection — hold it open and wait. It closes on its own once the run reaches a terminal state (`complete` or `failed`). A real report takes roughly 5-10 minutes end to end (measured: 495s / ~8.25 min for a single-dimension report against tenant 29, ~116K tokens).
4. Once the stream reports `status: "complete"`, it also carries a `reportId` in that same final event — no separate lookup needed.
5. **Call `Get_Report_Content`** with that `reportId` to get a real `contentUrl` — a short-lived SAS link. Open it directly in a browser tab.

That's the whole contract. Steps 3 and 5 are the two most commonly missed: don't poll `Generate_Report_*` for status — subscribe to the stream instead; don't try to guess or construct `reportId` yourself — it only exists in the stream's final event.

---

## 2. Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/insights/tenants` | List tenants the caller is eligible to generate reports for |
| `POST` | `/api/insights/reports` | Start generating a report (or several, if multiple dimensions requested) |
| `GET` | `/api/insights/runs/{runId}/stream` | Server-Sent Events — subscribe to watch a run to completion |
| `GET` | `/api/insights/reports/{reportId}/content?tenantId={id}` | Get a fresh, short-lived link to view the finished report |
| `GET` | `/api/insights/digest/unsubscribe` | The only anonymous endpoint — see §5 |
| `GET` | `/health`, `/health/ready`, `/health/live` | Infrastructure health checks — see §6 |

Full request/response shapes and worked examples are in the Postman collection's own per-request descriptions — this doc covers what those don't.

---

## 3. Headers

Every endpoint except `/health*` and the digest unsubscribe link requires:

```
Authorization: Bearer <valid JWT>
```

That's the only header you need to set manually. `Content-Type: application/json` on the `POST` request, `Accept: application/json` (or `text/event-stream` for the stream endpoint) are already set in the collection.

There is **no other header** this API reads for identity or authorization — no `X-Insights-UserId`, no API key header, no tenant header. The tenant is always a field in the request body/query, and it's re-validated server-side against the caller's real eligible set on every single request — a client can't just claim a tenant it doesn't have.

---

## 4. Real UAT test tenants

Only two tenants on the UAT database are actually entitled to paid Insights reports (`ProductMapping.ProductID = 19`, enabled) with real assigned scope. Both confirmed working end to end tonight:

| Tenant ID | Name | A real eligible user ID | Eligible users (total) |
|---|---|---|---|
| **29** | ABCD Pvt Ltd | **38** | 32 |
| **1285** | Adi Demo Customer | **11384** | 14 |

Use `sub: "38"` / tenant `29` as your default — it's the one this whole integration was validated against tonight (real generate → compose → narrate → render → QA → persist → SAS link, confirmed complete).

Every other tenant on UAT is either `basic` tier (free digest only, can't generate paid reports at all) or has zero assigned scope.

---

## 5. Digest unsubscribe (the one anonymous endpoint)

Email clients can't send a Bearer token, so `/api/insights/digest/unsubscribe` is deliberately exempted from JWT auth — it's protected instead by its own HMAC signature (the `t` query parameter, issued when a real digest email was sent to that customer/user). There's nothing to test here without a real digest send first; it's included in the collection for completeness.

---

## 6. Health checks

`/health`, `/health/ready`, `/health/live` require a **different** credential — a shared secret token, not a JWT:

```
X-Health-Token: <the real health check token — ask ops for it>
```

Without that header they return `401`. This is infrastructure tooling (for Kubernetes probes and manual ops checks), not part of the application API — you shouldn't need it for normal integration testing.

---

## 7. How JWT auth actually works here

The token is validated against four things: **signature**, **issuer**, **audience**, **expiry**. The signature check uses a symmetric key — **the same signing key the real RegTrack API already uses**. That's deliberate: a token issued by a real RegTrack login is understood here automatically, with zero extra work on RegTrack's side.

Once validated, the app reads exactly **one claim** — `sub` (the user id) — and nothing else. It never trusts a tenant id, role, or entitlement from the token itself; every request re-checks live against the database whether that user id is actually allowed to see that tenant's data. The token only answers "who is asking," never "what they may see" — that's a deliberate IDOR guard, not an oversight.

**In production, RegTrack's real login issues this token.** This app never mints one itself — it's a pure validator.

### The pre-request script in this collection

Since we don't have a real RegTrack login session for testing, the collection includes a JavaScript pre-request script (Postman's "Pre-request Script" tab, visible on the collection itself) that **mints a test-only token locally**, signed with the real signing key, using whatever `testUserId` is set in the active environment. It runs automatically before every request and writes the result into `{{bearerToken}}`.

This is **testing infrastructure only** — it exists because integration testing needs *a* valid token and there's no other way to get one outside a real RegTrack login flow. It is structurally indistinguishable from a real token to this app (the app only checks signature/issuer/audience/expiry, never *who* actually signed it), which is exactly why the signing key itself is so sensitive: **anyone holding it can mint a valid token for any RegTrack user, not just Insights users.** Treat it accordingly — it's requested from ops, pasted directly into your own local Postman environment, and never committed anywhere.

---

## 8. Known gaps (flagged, not hidden)

- **Report history** (`GET /api/insights/reports?tenantId=...`) is documented in an earlier design doc but **not implemented** — no route exists for it today. It's intentionally left out of this collection rather than included as broken.
- The digest **bounce webhook** is not mapped on this host either (it has no auth gate of its own; exposing it here would let anyone durably unsubscribe any customer/user pair) — also intentionally left out.
