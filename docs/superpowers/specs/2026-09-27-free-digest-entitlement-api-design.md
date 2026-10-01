SUPERSEDED 2026-09-29: entitlement API check removed from the free digest on the product owner's instruction.

# ADR: RegTrack show-entitlements as a scope restriction for the free weekly digest (RegInsights Basic)

## AS BUILT (2026-09-27)

**Deviations from the design below:** Design assumed a staged rollout (Off/Shadow/Gate/Narrow modes); implementation is UAT-only code with always-on "Narrow" behaviour. Key differences:

- **No Mode switch.** Behaviour is always "Narrow": each recipient's EntitiesAssignment scope is cut to the branches `show-entitlements` returns; a tenant not in `customerids` gets nothing. Product owner decision: code is UAT-only, so no shadow phase.
- **No orchestrator version bumps.** `FreeDigestGenerateOrchestrator`, `FreeDigestSendOrchestrator` and `FreeDigestInsightJsonOrchestrator` were changed in place at version 1.0. Before deploying, terminate/purge any in-flight FreeDigest* instances in the task hub, and do not deploy between Sunday generate and Monday send.
- **No sql/43 schema file, no new error block, no `usp_Insights_FreeMonthly_ResolveScope`.** Instead: one inline function `dbo.tvfInsightsEntitledScopePairs(@UserID, @CustomerID, @AllowedBranchIds VARCHAR(MAX))` in sql/34, plus an optional last parameter `@AllowedBranchIds VARCHAR(MAX) = NULL` (comma-separated branch ids; NULL = unfiltered) on the 5 slot procs (sql/36, 38, 39, 40, 41) and 2 loaders (sql/34, 35). New codes live in existing blocks: 51234 (sql/34) and 51243 (sql/35) for malformed lists; empty results reuse 51230/51240. sql/37 and sql/42 unchanged. sql/99 gains one DROP for the function.
- **sql/43 is read-only post-deploy validation only**, not a schema file.
- **Deploy order:** SQL first (34, 35, then 36/38/39/40/41), verify with sql/43, then the worker. Reverse order fails (Msg 8144: procedure rejects new parameter).
- **Login:** per recipient, token-with-details using recipient's email (from DB) + shared client secret; returned `user_id` must equal recipient's id or they are skipped. Then generate-hmac, then `show-entitlements` by user_id. `department_ids` ignored.
- **Config:** `FreeDigest:EntitlementApi:ClientSecret` and existing `FreeDigest:InsightApi:BaseUrl`. Both required at startup. No Key Vault, no cache, no batch-size/rate-limit keys (5 requests/second, ~100 lookups/min; lookups run 10 recipients/activity).
- **Safety nets added beyond the design:** C# repository refuses empty branch list; refuses any slot whose named 'location' findings fall outside allowed branches.
- **Known API behaviour:** email with no active user returns HTTP 500 (not documented 401), treated as "API unavailable"; tenant is refused for that run.

**Date:** 2026-09-27
**Status:** Proposed. This is the architect's decision, pending owner and BA sign-off. It authorises the
build (M0) and the **Shadow** rollout (M1). **Gate** and **Narrow** are turned on only when the gates
in Sec.12 pass. Narrow also needs the SQL change in Sec.5.6, which goes through Vinay.
**Scope:** The free digest's three orchestrations: Sunday generate, Sunday insight JSON and Monday
send. The paid tier is out of scope, but Sec.1.3 lists paid-tier defects found during this
investigation. They are more likely than anything in the free lane to be the reported "mixup".

---

## 0. The decision in one paragraph

Call RegTrack's `show-entitlements` API once per recipient, and use its answer only as a
**restriction** on the scope the digest already uses. It is never a scope of its own.

- A recipient's effective scope is their `EntitiesAssignment` (branch, category) pairs **whose branch
  is in the API's `branch_ids`**, and only when the tenant is in the API's `customerids`.
- Grouping, the artifact signature, the Monday re-check and the SQL slot procedures all run on that
  effective set.
- The API can only **remove** pairs. It can never add a branch, a category or a recipient.

It ships behind `FreeDigest:Entitlements:Mode` as a staircase:

| Mode | What it does |
|---|---|
| `Off` | Today's behaviour. |
| `Shadow` | Calls the API and measures. Changes nothing. |
| `Gate` | No SQL change needed. Skips every recipient whose effective set is smaller than their SQL scope. |
| `Narrow` | Needs the SQL change. Sends those recipients a digest trimmed to the effective set. |

`Narrow` is where we end up. `Gate` is the safety floor we can reach before the SQL change lands.

**The tradeoff.**

- **What we pay:**
  - Two HTTP calls per recipient per lane, rate-limited.
    - At the documented 100 req/min this adds about 2 hours to Sunday and again to Monday, across
      the whole fleet.
    - On Monday the last email moves from about 08:25 to about 10:10 IST, unless RegTrack raises our
      limit.
  - More LLM calls per tenant under Narrow (Sec.8).
  - A seven-file SQL change.
  - A new external dependency that fails the tenant **closed** when it is down.
- **What we get:** no digest ever carries a branch that RegTrack's own entitlement service says the
  recipient does not hold. That is not guaranteed today, because the recipient list comes from the
  management-role table while the data comes from the wider `EntitiesAssignment`.

---

## 1. What this fixes, and what it does not

### 1.1 The free lane already keeps users apart (verified)

The free lane cannot put one user's data into another user's digest:

- `@UserID` reaches the data only through `dbo.tvfInsightsScopePairs(@UserID, @CustomerID)`. The
  places it does so are sql/34 lines 185 and 215, sql/35 lines 101 and 128, sql/36 line 310, and
  sql/39 line 143.
- `ResolveDigestRecipientsActivity` groups recipients by `ScopeSignature.For(pairs)`, which is a
  SHA-256 of the sorted pair set. So every member of a group holds identical pairs, gets identical SQL
  output, and gives the LLM identical input.
- Each LLM call sees one group's slot data and nothing else. The free lane keeps no memory between
  runs (`TenantMemoryTool` is used only by the paid tier).
- `ResolveDigestDispatchActivity` recomputes the signature live on Monday. A recipient whose scope
  changed matches no artifact and is skipped.
- `AiReportWeeklyMapper` re-stamps `insight_id` and `user_id` per recipient. The card carries no other
  per-user field; `InsightCardBuilder` line 62 is the only place the user id is used.

The API integration **does not change this property and is not needed for it.** The owner should be
told this plainly. If a mixup was seen, look first at Sec.1.3.

### 1.2 What the API fixes: showing more than RegTrack's own entitlement allows

sql/01 lines 501-507 record an open item marked `[OPEN - BA]`:

- The recipient source is `tvfInsightsManagementUsers`, which reads `ComplianceCategoryMgmtUser`
  (CCMU).
- The data scope is `tvfInsightsScopePairs`, which reads `EntitiesAssignment` (EA).
- CCMU is **narrower** than EA for 14 of the 15 users measured. For example, user 22426 has 155 pairs
  in CCMU but 400 in EA.

So a person chosen **because of** a management role on 41 branches gets a digest built from 50
branches. The API is RegTrack's own answer, chosen by role, to "which branches is this user entitled
to". Intersecting with it closes that gap without trusting any single table.

### 1.3 Cross-user problems found during this investigation. This ADR does NOT fix them.

These fit "the SP or LLM mixed one user's info with another's" much better than anything in the free
lane. They need their own ADR, and it should come first.

| # | Where | Defect | Verified? |
|---|---|---|---|
| P1 | `Insights.Domain/InsightsRunId.cs`, method `For(tenantId, scopeDescriptor, reportType, period)`. `DurableTaskRunEnqueuer` swallows `OrchestrationAlreadyExistsException` and returns the same id. | The paid run id includes **no user and no scope pairs**. Two users in the same tenant with different scopes who both ask for `"tenant"` land on one instance. User B attaches to user A's run, which was computed with A's `UserId`. `/runs/{runId}/stream` checks only that B is eligible for the tenant (`RunEndpoints.cs` lines 185-201). | Verified in code |
| P2 | `Insights.Persistence/ReportContentService.cs` lines 88-89 | **Any** user with a non-empty scope in the tenant can open a `"tenant"` report (the check is `viewerPairs.Count > 0`). A user with a narrow scope can open a report built for a wide scope. The class's own doc comment already flags this as a known limitation. | Verified in code |
| P3 | `EfCooldownRepository` | The cooldown key is (tenant, reportType, descriptor, dimension). It includes neither the caller nor the pair set, so a report built on user A's scope locks user B out. If the UI then sends B to A's report, P2 lets B open it. | The lockout is verified. Whether the UI routes B to A's report is **not verified**. |
| P4 | `Insights.Agents/TenantMemoryTool.cs` line 185 (blob path `{tenantId}/history.md.enc`) | The paid narrate memory is stored **per tenant, not per scope**. Notes written during user A's run, including branch names and figures, are injected as `tenant_history` into the prompt for user B's narration. The LLM is literally given one user's information while writing another user's report. | Verified in code |
| F1 | `Email:RecipientOverride` (UAT) | Every scope group's email lands in one inbox, and the subject does not say which user or scope it was for. A tester sees several different digests addressed to themselves. | Verified in code |
| F2 | `FreeMonthlySettings.AllowPersonNames`, default `true`. sql/38 names performers. | The Users email names the people who hold in-scope work. `docs/FREE_DIGEST_ARCHITECTURE.md` Sec.1 says "No person names in the email", which the code contradicts. This is not a scope leak, but a reader could take it as "another user's info". | Verified in code |

What the separate ADR should do:

- **P1 to P3:** add a scope-pair signature to the run id, the cooldown key and the persisted
  `GeneratedReport` row. Allow a user to view a report only when their current pairs include every
  pair the report was built on.
- **P4:** key the memory by (tenant, scope signature), or turn it off.
- **F1:** when the override is set, stamp the intended recipient id and the first 8 characters of the
  signature into the subject.
- **F2:** fix the doc, or change the default.

---

## 2. Context (verified in code)

### 2.1 The free flow today

- **Sunday generate** (`FreeDigestGenerateOrchestrator` 1.0):
  1. `ResolveDigestRecipientsActivity` does the following, in order:
     1. runs the tenant gate (`usp_Insights_FreeDigestGate`);
     2. loads recipients (`GetRecipientsAsync`, from CCMU);
     3. drops recipients who have already been claimed;
     4. calls `GetScopePairsAsync` for each recipient;
     5. groups recipients by signature, using the first member as `RepresentativeUserId`.
  2. For each group:
     1. `ClaimDigestArtifactActivity` reserves the artifact slot. Its key is (customer, week,
        signature), and the signature is stored as `CHAR(64)`.
     2. `ComposeDigestActivity` runs the slot procedure for the representative and makes one LLM call.
     3. `PersistDigestArtifactActivity` stores the result.
- **Sunday insight JSON** (`FreeDigestInsightJsonOrchestrator` 1.0): the same resolve step, then one
  `ComposeInsightJsonActivity` per group (one LLM call), then one `PostInsightJsonActivity` per
  recipient.
- **Monday send** (`FreeDigestSendOrchestrator` 1.0): `ResolveDigestDispatchActivity` does the
  following, in order:
  1. re-runs the tenant gate;
  2. resolves the email gateway;
  3. lists the week's artifacts, and **exits early if there are none**;
  4. re-resolves recipients and their signatures live;
  5. matches each recipient to an artifact by exact signature; no match means the recipient is
     skipped.

  The artifact is then fetched and sent to each recipient.
- **What the free path never calls:**
  - `usp_Insights_EligibleTenants` for each user;
  - `usp_Insights_AuditScope`;
  - anything else that asks RegTrack whether this user is still entitled to this tenant.

  The API's `customerids` fills that per-user tenant-membership gap. That is the intent of the IDOR
  rule.

### 2.2 Three scope sources, and what each one decides

| Source | Axes | Used today for | How it relates to the others |
|---|---|---|---|
| EA: `EntitiesAssignment`, via `tvfInsightsScopePairs` | Two (branch and category) | **The data.** Every slot procedure. | The widest. sql/03 chose it on purpose, so that sites that are configured but dormant still show up. |
| CCMU: `ComplianceCategoryMgmtUser`, via `tvfInsightsManagementUsers` | Two | **Who receives** a digest | A strict subset of EA (sql/01 line 256: no exceptions across 2 tenants) |
| API: `branch_ids` and `department_ids` | **One** (branch only) | Nothing yet | Unknown. For management users the branches come from "source 8", which is probably CCMU's branch set. That is **not confirmed** (Q1). |

### 2.3 API contract facts that drive the design

- **Three calls.**
  1. `token-with-details` takes the integration email and secret, and returns `token`, `user_id` and
     `customer_id`.
  2. `generate-hmac` takes the exact request body and a Bearer token, and returns `result.hmac`.
  3. `show-entitlements` takes a Bearer token, the `X-Payload-HMAC` header, and **exactly the same
     bytes** as the body sent to `generate-hmac`.
- **The request body always has both keys:** `{"email":"","user_id":<number>}`. `email` is a string,
  never null. `user_id` is a JSON number, never a string.
- **A 200 response** returns `result.customerids`, spelled **without an underscore**, plus
  `branch_ids` and `department_ids`. An empty array is a real answer ("no mapping"), not an error.
- **A 404 response** means there is no active user. Its result uses **different keys**:
  `customerids`, `locations` and `departments`. Always decide by the HTTP status, never by the shape
  of the body.
- **Error statuses:**

  | Status | Meaning |
  |---|---|
  | 400 | Validation failed |
  | 401 | Four possible causes, one of which is an HMAC mismatch |
  | 408 | The stored procedure's 60 s timeout |
  | 429 | Rate limited: 100 req/min per caller IP plus token subject, with `X-RateLimit-*` headers |
  | 500 | Server error |

- **The endpoint is marked `[SessionClaimsBindingExempt]`**, so it looks up **any** user. That makes
  it an admin lookup, and makes the integration secret a high-value credential (Sec.9).

### 2.4 Operational constraints

- **The worker runs one activity at a time.** `WorkerRegistration.cs` sets
  `MaxConcurrentActivities = 1` and `MaxActiveOrchestrations = 1`. The paid and free tiers share that
  one slot, so a long free activity blocks paid interactive runs.
- **Fleet size and Monday target:** about 600 free tenants and about 5,000 recipients, per the
  `FreeDigestScheduler` doc comment. The Monday target is "in inboxes before 9am": sending starts at
  08:00 and takes about 17 minutes at 5 emails a second.
- **LLM calls:** one per scope group per lane, so the number of groups drives LLM cost. The
  `ScopeSignature` doc comment and the design doc Sec.10.5 budget assume about one digest per tenant.

---

## 3. Options considered

| Option | What it is | Verdict |
|---|---|---|
| A. Gate only | Compare the API's answer with EA. If EA contains a branch the API does not, drop the recipient. No SQL change. | **Kept as a step (`Gate`), not as the end state.** It is safe. But if the API's management branches equal CCMU, it would drop most recipients (14 of the 15 sampled). That gives up the product for safety when we could have both. |
| **B. Narrow** | The effective scope is the EA pairs whose branch is in the API's list. Needs a SQL restriction parameter. | **Chosen.** It is correct whatever the relationship between the API and EA turns out to be. If they are equal, nothing changes. If the API's set is smaller, the digest is trimmed. If they share nothing, the recipient is skipped. It never shows more than either source allows. |
| C. Replace EA with the API | The scope becomes the API's `branch_ids` combined with every category | **Rejected.** This is the "central trap" described in sql/03's header: a branch-only scope shows an EHS head the Labour data. A one-axis answer may only narrow a two-axis scope, never replace it. |
| D. Use `department_ids` as a third axis | Filter obligations by `ComplianceInstance.DepartmentID` | **Rejected for v1.** The free procedures have no department axis. `DepartmentID` can be null (the residual in CLAUDE.md §4a), so deciding whether unassigned obligations count would need a new BA ruling. See Sec.4.4. |
| E. Bulk snapshot table | Fetch everyone's entitlements into a SQL table on a schedule | **Rejected for v1.** It needs a new table (through Vinay) and a rule for how stale it may get. A weekly snapshot brings back the "cached at scheduling time" problem that ADR-0001 D5 forbids. Revisit if RegTrack offers a batch endpoint. |
| F. Fall back to SQL-only scope when the API is down | Keep sending with the EA scope | **Rejected.** A silent downgrade brings back exactly the risk the user asked us to close, and breaks non-negotiable 2. The only fallback is a person lowering `Mode`, which is loud and deliberate. |

---

## 4. Decision

### 4.1 Semantics

For recipient `u` of tenant `T`, with API answer `R`:

- **If the status is 200, the body parses, and `T` is in `R.customerids`:**
  - Let `A = R.branch_ids`.
  - The effective set is `E(u) = { (b, c) in EA(u, T) : b in A }`.
  - If `E(u)` is empty, the recipient is skipped.
- **Otherwise:** see Sec.6. Once `Mode` is `Gate` or higher, a recipient is never served on EA alone.

What follows from this:

- `E(u)` is always a subset of `EA(u)`. Categories come only from EA.
- Branches that belong to other customers drop out automatically, because EA is already filtered to
  `T` through `CustomerBranch.CustomerID`.
- Recipients still come only from CCMU. The API never adds a recipient.
- **The API's answer never reaches the LLM.** The LLM's input is still only the slot procedure's
  output. The "no hallucination" guarantee is unchanged: numbers come from SQL, and the API only
  narrows what SQL is asked about.

### 4.2 The representative invariant: one SQL call per group is still exact

Two members of the same group can have **different** EA sets and the **same** effective set. For
example:

- X has EA `{b1, b2} x {c1}` and API `{b1}`.
- Y has EA `{b1, b3} x {c1}` and API `{b1}`.
- Both have `E = {(b1, c1)}`, so they form one group.

The SQL is called with `@UserID` set to the group's representative and `@AllowedBranchIds` set to
`branches(E)`. It then returns exactly `E`, whichever member is the representative.

**Proof.** Take any member `u` with `E(u) = E`, and let `B = branches(E)`.

- **Everything in `E` is returned.** Every `(b, c)` in `E` is in `EA(u)`, and `b` is in `B`.
- **Nothing outside `E` is returned.** Take `(b, c)` in `EA(u)` with `b` in `B`. Some `(b, c')` is in
  `E`, so `b` is in the API set `A_u`. Therefore `(b, c)` is in `E`.

Two consequences:

- The SQL is sent branch ids, not pairs, and `@UserID` stays the only source of categories. A bug in
  the C# code can therefore **only narrow** the scope; SQL never trusts a category supplied by the
  caller.
- The SQL also checks that every allowed branch is inside the representative's EA branches (error
  51311, Sec.5.6). If the wrong representative is ever chosen, the call refuses instead of returning
  data.

### 4.3 Modes: `FreeDigest:Entitlements:Mode`, default `Off`

| Mode | API called | Grouping key | SQL | Who receives a digest |
|---|---|---|---|---|
| `Off` | No | EA signature | Unchanged | Same as today |
| `Shadow` | **Sunday lanes only** | EA signature | Unchanged | Same as today. The API's answer is only measured (Sec.12). |
| `Gate` | Sunday and Monday | EA signature | Unchanged | Only recipients whose `E(u)` equals `EA(u)`. Everyone else is skipped with reason `EntitlementNarrowingPending`. |
| `Narrow` | Sunday and Monday | Effective signature | Restriction applied (Sec.5.6) | Everyone with a non-empty `E(u)` |

- **Shadow runs only on Sunday**, so Monday's delivery time does not move while we are measuring.
- **Gate needs no SQL change.** Every recipient it serves has `E = EA`, so the unrestricted procedure
  already returns exactly `E`.

### 4.4 `department_ids`

`department_ids` is not a scope axis and is **not** included in the signature.

- **In Gate and Narrow, a recipient with a non-empty `department_ids` is skipped**, with reason
  `EntitlementDepartmentScoped`. We cannot honour a department restriction, so we refuse rather than
  ignore it.
- This is controlled by `FreeDigest:Entitlements:TreatDepartmentsAsRestriction`, which defaults to
  `true`. Set it to `false` only after the API team confirms that, for management-role users, the
  field is informational and not a restriction (Q4).
- Shadow measures how often the field is non-empty.

### 4.5 This ADR decides the `[OPEN - BA]` item

Adopting Narrow means:

- RegTrack's entitlement service is the authority for **branches**;
- EA is the authority for **categories**;
- the digest shows where they overlap.

**The consequence the BA must accept:** sql/03 deliberately keeps sites that are configured but
dormant, because they are "a key finding the engine exists to surface". Wherever the API excludes such
a site, it will no longer appear in that management user's digest.

**Where to record the ruling:** sql/01 cites `docs/OPEN_DECISIONS.md` for this item, but that file
**does not exist in the repo**. Record the ruling there, or wherever the BA keeps rulings, before Gate
is turned on.

---

## 5. Flow changes

### 5.1 Sunday generate: `FreeDigestGenerateOrchestrator` 2.0

1. **`ResolveDigestAudienceActivity` (new).** Runs the gate, loads recipients, removes those already
   claimed, and reads the clock once to fix the week-ending date. This is the old resolve step
   **without** the scope lookups and grouping.
   - The cheapest checks run first. A tenant that is not entitled, or whose week is fully claimed,
     costs zero API calls.
2. **`LookupRecipientEntitlementsActivity` (new).** Runs once per chunk of `LookupBatchSize`
   recipients (default 10), one chunk after another. For each recipient it:
   1. calls the API. It skips the call when Mode is `Off`, and may answer from the Sunday cache
      (Sec.7.4).
   2. reads the EA pairs with `GetScopePairsAsync`.
   3. computes `E(u)` with the pure `EffectiveScope` function in Insights.Domain.
   4. returns one record per recipient: `UserId`, `Outcome`, `EaSignature`, `EffectiveSignature?`,
      `EffectivePairCount?` and `DepartmentCount?`.

   It also returns **one** sorted `AllowedBranchIds` list for each distinct effective signature in
   the chunk. Branch lists are never repeated per recipient.
3. **Orchestrator body (pure logic, no I/O).**
   - Merge the chunk results in recipient order. That order is deterministic because it comes from
     the recorded history.
   - Apply the Mode rule from Sec.4.3.
   - Group recipients by the key the mode uses, in first-seen order. The first member is the
     representative.
   - Log the number of groups under the current mode, and the number there would be under Narrow.
4. **For each group,** unchanged except for two new fields:
   - `ClaimDigestArtifactActivity`, which is unchanged. The signature is whichever key the mode uses.
   - `ComposeDigestActivity`. `ComposeDigestInput` gains two nullable fields, added at the end:
     `AllowedBranchIds` and `ExpectedEffectivePairCount`.
     - In `Narrow`, both are always set, even when `E` equals EA.
     - In every other mode, both are null. Null means "call the procedure exactly as today".
   - `PersistDigestArtifactActivity`.
5. **A systemic API failure refuses the tenant** (Sec.6) **before any composing starts.** An outage
   therefore never leaves LLM money half spent.

### 5.2 Sunday insight JSON: `FreeDigestInsightJsonOrchestrator` 2.0

- The same shape as steps 1-3 of Sec.5.1.
- `ComposeInsightJsonInput` gains the same two trailing nullable fields.
- `PostInsightJsonActivity` is unchanged.
- Lookups are shared with the generate lane through the Sunday cache (Sec.7.4).

### 5.3 Monday send: `FreeDigestSendOrchestrator` 2.0

1. **`ResolveDigestDispatchAudienceActivity` (new).** Runs the cheapest checks first:
   1. Re-run the tenant gate.
   2. Resolve the email gateway.
   3. List the artifacts, deduplicated by signature exactly as today. **If there are none, exit here,
      before any API call.**
   4. Load the recipients live.
   5. **Remove recipients who already hold this week's send claim** (`GetClaimedUserIdsAsync`), before
      any lookup. Re-driving a Monday run after an outage then looks up only the people still
      unsent.
2. **`LookupRecipientEntitlementsActivity`**, in chunks and **always live**. The cache is bypassed,
   because ADR-0001 D5 requires entitlement to be evaluated at execution time. No branch lists are
   returned, because Monday composes nothing.
3. **Orchestrator.** Match each recipient's current-mode key to an artifact signature. If nothing
   matches, the recipient is skipped, as today.
4. Fetching and sending are unchanged.

In `Off` and `Shadow`, Monday makes no API calls and behaves exactly as 1.0 does.

### 5.4 The signature

`ScopeSignature.For` keeps its **canonical form unchanged**. In Narrow mode it is fed `E(u)`; in
every other mode it is fed `EA(u)`.

- When `E` equals EA, both modes produce the same hash. That is correct: the pairs are the same, so
  the content is the same. If the mode changes between Sunday and Monday, no one whose scope did not
  narrow is left without an artifact.
- Anyone whose scope did narrow hashes differently. They match no artifact and are refused for that
  one week.
- No schema change is needed. The signature is still SHA-256 hex in a `CHAR(64)` column.

### 5.5 Replay determinism and a clean history

- **API calls happen only inside activities.** The orchestrator reads only activity outputs, so a
  replay never calls the API again.
- **Never put the token, the HMAC, the client secret or a raw response body into any activity input
  or output.** Durable Task history is stored as plain text in SQL. Outputs carry only derived values:
  an outcome enum, signatures, counts, and branch-id lists without duplicates.
- **New fields on existing records go at the end and are nullable,** following the `EmailGatewayId`
  precedent. Histories recorded under 1.0 still deserialise.
- **LLM activities behave exactly as before.** On replay their recorded outputs are read back, so
  nothing is billed twice.

### 5.6 The SQL contract for Vinay (Narrow only)

This is a contract, not SQL.

1. **New file sql/43, in error block 51310-51319.**
   - A table type, `dbo.InsightsBranchIdList`, with one column `BranchID INT` as its primary key.
   - A helper procedure, `dbo.usp_Insights_FreeMonthly_ResolveScope`, with parameters `@UserID`,
     `@CustomerID`, `@EnforceBranchRestriction BIT` and a read-only `@AllowedBranchIds`.
   - The helper **fills a `#scope_pairs` table that the caller creates** (branch, category; primary
     key). It fills it with the EA pairs from `tvfInsightsScopePairs`. When the flag is 1, only pairs
     whose branch is in `@AllowedBranchIds` are kept.
   - Like every helper here, it **returns no result set**.
2. **Error codes, one per condition:**

   | Code | Kind | Condition |
   |---|---|---|
   | 51310 | Scope denied | `#scope_pairs` is empty |
   | 51311 | Structural | An allowed branch is not among the representative's EA branches. This means the caller grouped wrongly (Sec.4.2). |
   | 51315 | Input | The flag is 1 but the branch list is empty. Under enforcement, empty must never mean "no restriction". |
   | 51316 | Input | The flag is 0 but the branch list is not empty, so the caller's intent is ambiguous. |

   - **Widen `FreeMonthlyDigestRefusedException.IsMonthlyErrorNumber` to 51230-51319.** Otherwise these
     codes are retried three times as if they were infrastructure faults.
   - Add the new block to the table in CLAUDE.md §5b.
3. **The five slot procedures (sql/36, 38, 39, 40, 41)** each gain two parameters:
   `@EnforceBranchRestriction BIT = 0`, and `@AllowedBranchIds`, which is optional and empty by
   default.
   - This is **backward compatible**: existing callers are unaffected.
   - Each procedure creates `#scope_pairs` and calls the helper before anything else.
4. **Every scope read goes through `#scope_pairs`.** These are the direct readers found:

   | File and line | What it reads | Change |
   |---|---|---|
   | sql/34 line 185 | Pre-flight scope check | Read `#scope_pairs` instead. |
   | sql/34 line 215 | `tvfInsightsScopedInstances` | **Join it to `#scope_pairs` on branch and category.** The estate definition stays in the table-valued function. |
   | sql/35 lines 101 and 128 | Licence loader | Read `#scope_pairs` instead. |
   | sql/36 line 310 | Overview | Read `#scope_pairs` instead. |
   | sql/39 line 143 | **Location's member list** | Read `#scope_pairs` instead. If this one is missed, a branch the user is not entitled to, with zero obligations, would be **named** as a ghost location. |
5. **Add a CI check that finds these reads by condition, not by a list of names** (CLAUDE.md §3):
   - No module named `usp_Insights_FreeMonthly%` other than `_ResolveScope` may reference
     `tvfInsightsScopePairs`.
   - Every module that references `tvfInsightsScopedInstances` must also reference `#scope_pairs`.
6. **Add two columns to `control_totals`: `EffectiveScopePairs` and `RestrictionApplied`.**
   - The C# code checks that `EffectiveScopePairs == ExpectedEffectivePairCount`. A mismatch refuses
     the digest; it is not a warning. This is non-negotiable 3 applied to scope across the C#/SQL
     boundary.
   - `usp_Insights_AuditScope` is **not** added. It works from EA, so against the effective set it
     would always pass and prove nothing.
7. **Rollback and validation scripts.**
   - **sql/99** drops the helper and the changed procedures **before** it drops the type.
   - **sql/42** adds two checks. Vinay runs them in UAT on the tenant set from CLAUDE.md §11 (1285,
     1490, 1403, 1472, 29, 522, 2480/1807, 1216). Never run them on 1082.
     - **Parity:** with the restriction set to all of the user's EA branches, the output must be
       byte-identical to an unrestricted call.
     - **Containment:** with the restriction set to a strict subset, every location-type candidate
       and example must fall inside the allowed branches.
8. Keep the SQL source pure ASCII, and use no join hints (CLAUDE.md §5 and §5a).

---

## 6. Failure handling

**The rule:**

- An answer *about a user* skips that user.
- A failure *of the API* refuses the whole tenant.
- We never fall back to the SQL-only scope.

| Condition | Class | In Gate and Narrow | In Shadow |
|---|---|---|---|
| 200 and parsed; `T` is in `customerids`; `E` is not empty; `department_ids` is empty or the department setting is off | ok | served on `E` | recorded |
| 200, but `T` is **not** in `customerids` | answer | skip: `EntitlementTenantMismatch` | recorded |
| 200 with `branch_ids` equal to `[]` | answer | skip: `EntitlementNoBranches` | recorded |
| 200, but `E` is empty after the intersection | answer | skip: `EntitlementNoEffectiveScope` | recorded |
| 200 with a non-empty `department_ids`, and the department setting is `true` | answer | skip: `EntitlementDepartmentScoped` | recorded |
| 404, meaning no active user. Decided on the status alone; the body is **never** read as a 200 response. | answer | skip: `EntitlementUserNotActive` | recorded |
| 408 for one user, the stored procedure's 60 s timeout. Not retried inside the activity. | per-user | skip: `EntitlementTimeout`. This is **returned**, not thrown. | recorded |
| **Three 408s in a row inside one chunk** | transient | the chunk **throws**, and Durable retries it with backoff. This usually means the stored procedure is overloaded. | same |
| **Share of 408s across the whole tenant is above `max(3 recipients, 5%)`, counted after all chunks** | systemic | **refuse the tenant** (returned, final for this run). It counts across the tenant rather than per chunk, so two consistently slow users who happen to fall in the same chunk cannot refuse the tenant every week. | tenant logged |
| 200 but `success` is false; a key is missing or misspelled (for example `customer_ids`); a value has the wrong JSON type; the body will not parse. A missing key must **never** be read as an empty list. | contract | **refuse the tenant** (returned) and log an Error | tenant logged as a contract failure |
| 400 | contract | **refuse the tenant** (returned). The request body has a fixed shape, so a 400 means the contract has drifted, and a retry cannot help. | same |
| 401 | auth | refresh the token once and retry once. A second 401 (HMAC mismatch, bad secret, bad subject) **refuses the tenant** (returned). | same |
| 429 | transient | wait for `Retry-After` or `X-RateLimit-Reset` (60 s at most), up to 3 times. Then throw so Durable retries. | same |
| 5xx, or a network error | transient | retry once inside the activity, then throw so Durable retries (3 attempts). A 500 is **never** read as "no entitlements". | same |
| A chunk that still fails after Durable's retries | systemic | **refuse the tenant for this run** (`EntitlementApiUnavailable`). No composing on Sunday; no sending on Monday. | Shadow ignores it and carries on with EA |

**Returned or thrown.** Final outcomes such as a contract failure, a 400, a second 401 or the
tenant-level 408 share are **returned** as data from the activity, not thrown, so Durable does not
waste retries on them. `PostInsightJsonActivity` already uses this pattern. Only transient conditions
are thrown.

**A refused tenant.** The run ends with a Decision and an Error log. Like today's SQL refusal, it is
**final for the week**, because the instance id is keyed on (tenant, week).

- **A Sunday outage** means no artifacts and no LLM spend.
- **A Monday outage** leaves the artifacts fresh for `ArtifactFreshnessDays` (3 days). Once the API
  recovers, ops can re-run the send; the runbook needs a purge-and-rerun step. Because of the
  claimed-recipient filter in Sec.5.3, a re-run looks up only the people still unsent.

**Metrics** (added to `FreeDigestMetrics`):

- `insights.digest.entitlement_lookup_total{lane, outcome}`
- `insights.digest.entitlement_http_total{endpoint, status}`
- `insights.digest.entitlement_lookup_ms` (a histogram)
- `insights.digest.entitlement_ratelimit_wait_ms`
- Shadow only: `insights.digest.entitlement_shadow_relation_total{relation}`, where `relation` is one
  of `equal`, `api_subset`, `api_superset`, `overlap` or `disjoint`
- A new `FreeDigestSkipReason` member for each skip reason in the table above

**Logging:**

- At Information level, log the user id, tenant, outcome, status and latency. Never log the token,
  HMAC, secret or response body.
- Log a response body only for a contract failure, and cut it to 400 characters.
- For each tenant, log the number of groups under the current mode, the number projected under
  Narrow, and the count of each outcome.

---

## 7. Rate limit, throughput and API cost

### 7.1 Calls

- **Each lookup is 2 calls:** `generate-hmac`, then `show-entitlements`.
- **Tokens:** one call per token lifetime. The token is cached in memory and refreshed 2 minutes before
  `exp`. Only one refresh runs at a time; concurrent callers wait for it (Q3).
- **One shared `TokenBucketRateLimiter` covers all three endpoints.**
  - It has the same steady, no-burst shape as `RateLimitedEmailSender`: `TokenLimit = 1`, refilled
    every `60 / RequestsPerMinute` seconds.
  - The default is **90 requests per minute**, which leaves headroom under 100. That gives **45
    lookups per minute**, whichever endpoints RegTrack actually counts.
  - If RegTrack confirms that only `show-entitlements` counts toward the 100/min (Q2), raise
    `RequestsPerMinute` to **180**. The two endpoints alternate, so each still sees about 90/min, and
    lookups go up to about 90 per minute. Nothing else needs to change.
  - The time allowed to acquire a slot is **longer than a chunk** takes, so a chunk never times out
    waiting.
- **The limit applies per process.** A second worker replica would double the real rate, which is the
  same caveat as for email.

### 7.2 Throughput

| | `RequestsPerMinute = 90` (45 lookups/min), the default and safe under either reading of Q2 | `RequestsPerMinute = 180` (about 90 lookups/min), only after Q2 confirms `generate-hmac` is not counted |
|---|---|---|
| One tenant with 600 recipients (1,200 calls) | **about 13.3 min** | about 6.7 min |
| Whole fleet, about 5,000 recipients, one lane | **about 111 min** | about 56 min |
| Sunday: generate plus the JSON lane | 111 min, up to 222 min if the cache never hits | 56 to 111 min |
| **Monday: when the last email goes out** (today about 08:25 IST) | **about 10:10 IST** | about 09:15 IST |
| Monday, if RegTrack gives our subject 600 req/min or more | about 08:35 IST | about 08:35 IST |

**Monday decision:**

- Do **not** restructure the send phase.
- Do **not** cache across the Sunday-to-Monday gap. The Monday check must be live.
- Before Gate, ask RegTrack for a higher limit on the integration subject, or for a batch endpoint
  (Q2). If they refuse, the owner explicitly accepts the later Monday finish as the price of Gate and
  Narrow.
- Shadow never touches Monday.

### 7.3 Keeping paid runs moving on the one-slot worker

Lookups run **10 recipients per activity**, so paid activities can be picked up between chunks.

- **A typical chunk takes about 20-30 s:** 10 lookups, each using 2 limiter slots of about 0.67 s,
  plus network time.
- **The worst case is about 3.5 min:** three 408s in a row at 60 s each trip the chunk rule, plus the
  normal work. Chunks are sized for the worst case, not the average.
- **The HttpClient timeout is 75 s.** Startup refuses any value of 60 s or less, because a timeout at
  or below 60 s would fire before we could ever see the 408.

### 7.4 Caching

There is an in-process cache keyed on (tenant, user).

- **What it stores:** the **derived** outcome and the API's branch set, never the token or the
  response body.
- **Which outcomes it stores:** only definite answers (ok, 404, tenant mismatch, no branches). A
  failure is never cached.
- **How long:** `FreeDigest:Entitlements:SundayCacheMinutes`, default 120; 0 turns it off.
- **Who uses it:** the two Sunday lanes read and write it. The Monday lane **never reads** it.
- **Restarts:** a restart empties it, which only costs extra calls.
- **Why it exists:** it stops the JSON lane from doubling Sunday's API traffic.

---

## 8. Effect on LLM calls, tokens and output

**Tokens per call do not change.** The prompt's shape is fixed for each slot, and the token caps are
unchanged: `Budget:FreeMonthlyTokenCap:*` for the email and `Budget:InsightJsonTokenCap` for the card.
The API's answer is never part of the prompt.

**Calls are one per group per lane, as today. Only the number of groups changes:**

| Mode | Number of groups | What served recipients get | Who is left out |
|---|---|---|---|
| Off, Shadow | Unchanged | Unchanged | Nobody new |
| Gate | **Never more than today.** Members are removed, and groups left with no members disappear. | **Byte-identical to today** | Anyone whose scope would narrow gets **nothing** that week: no email and no card |
| Narrow | **One group per distinct effective set. The count can go down** (users with different EA can merge) **or up** (users with the same EA but different API sets split). The brief assumed groups could only split. That was wrong. Merging is safe, because merged users get identical data. | Numbers, locations and acts cover only `E`. Totals are smaller wherever `E` is smaller than EA. | Only those whose `E` is empty |

**Worst case under Narrow.** Take a tenant with about 600 users who are mostly tenant-wide in EA,
which gives about 15 groups today. If the API returns a different management set for each user, the
group count could climb toward the number of distinct sets, up to 600. With the JSON lane on, that is
up to 1,200 LLM calls a week instead of 30: about 40 times as many for that tenant.

**This is the main cost risk, and Shadow measures it before Narrow is turned on.**

- Shadow logs the projected Narrow group count for each tenant and for the whole fleet.
- Narrow is promoted only if the projection is **no more than 2 times** today's weekly call count
  across the fleet, or the owner signs off a larger budget. The design doc Sec.10.5 budget is about
  30 million tokens a year.

---

## 9. Secrets, integration identity and environment

**Secrets.** `FreeDigest:Entitlements:ClientEmail` and `FreeDigest:Entitlements:ClientSecret` come
from Key Vault or environment configuration, never from literals in appsettings. This matches how
`FreeDigest:InsightApi:ApiKey` is handled.

**Integration identity.** Use a **dedicated, non-human RegTrack service user** set up by the RegTrack
API team, never a person's account.

- **Why it is sensitive:** `show-entitlements` can look up any user, so this secret gives read access
  to the whole organisation's access map.
- **Ask RegTrack (Q9)** to limit this user to `generate-hmac` and `show-entitlements`, to allow it
  only from our IP addresses, and to document how the secret is rotated.
- **It must have no CCMU or EA rows.** Otherwise it would itself become a digest recipient.

**Environment: the risk of asking about the wrong person.** The API host is fixed, but
`ConnectionStrings:RegTrack` may point at UAT or at the production replica. If a user id has come to
mean a different person in the other environment, we would get **another person's** entitlements.
There are two layers of protection.

1. **Per process (weak).**
   - What it does: before the first lookup, compare the `user_id` returned by `token-with-details`
     with `[User].ID` for the integration email in our database. Optionally also compare it with
     `FreeDigest:Entitlements:ExpectedIntegrationUserId`.
   - Its limit: UAT is a copy of production, so the integration user's id will usually match in both.
     This catches a wholesale misconfiguration only. It cannot catch individual user ids that changed
     after the copy.
   - When it runs: at first use in each process, **not at startup**. A network call at startup would
     let an API outage stop the worker, which also runs the paid tier.
   - If it fails: every tenant in the lane is refused, and an Error is logged.
2. **Per recipient (strong, needs Q7 or Q10).**
   - What it does: check each answer is about the right person. Either:
     - RegTrack echoes the resolved `user_id` **and email**, and we compare them with our
       `[User].ID` and `User.Email` (Q7); or
     - we send both keys, and RegTrack confirms it treats them as "both must match" (Q10).
   - If the check fails: the answer is treated as a contract failure, and the tenant is refused.
   - Why this is the real guard: it also protects against a cross-user mistake on RegTrack's side.
3. **In Shadow,** a tenant-mismatch or 404 rate above 1% is the warning sign to stop on.

**Do not reuse the insight lane's configuration or HttpClient.** Reuse its conventions instead:

- a named client from `IHttpClientFactory`;
- a base URL containing the host only, with paths added by string concatenation (ADR-0003 D7);
- deciding by status code before choosing how to deserialise;
- validating configuration eagerly at startup.

The two lanes differ in:

| | Insight lane | Entitlements |
|---|---|---|
| Auth | A static `ApiKey` sent as Bearer | A token plus an HMAC |
| Timeout | 30 s | More than 60 s |
| On/off switch | `InsightApi:Enabled`, which controls a separate lane | `Mode` |

Tying the two together would mean that pointing one lane at a mock silently redirects the other.

---

## 10. Components

| Item | Where | Notes |
|---|---|---|
| `IRegTrackEntitlementClient`, `RegTrackEntitlementClient` | `Insights.Data/RegTrackApi/` | Token cache, the HMAC round trip, `show-entitlements`, deciding by status, and the shared rate limiter. It lives in Data, next to the email senders, so the paid API can use it later. |
| `RegTrackEntitlementContract` (the 200 and 404 response shapes, and the token and HMAC shapes) | `Insights.Data/RegTrackApi/` | Map `customerids` explicitly, with **no underscore**. Fields are required, so a missing key fails. It also holds the echoed `user_id` and email once Q7 is agreed. |
| `EntitlementLookupOutcome` (enum), `EntitlementLookup` (record) | `Insights.Domain` | A closed set of outcomes, matching Sec.6. |
| `EffectiveScope`: intersect, list the branches, classify how two sets relate. Pure, no I/O. | `Insights.Domain` | Reuses `ScopePair` and `ScopeSignature`. |
| `EntitlementApiSettings` | `Insights.Worker` | Bound and validated in `FreeDigestRegistration.BuildSettings`. |
| `ResolveDigestAudienceActivity`, `ResolveDigestDispatchAudienceActivity`, `LookupRecipientEntitlementsActivity` | `Insights.Worker/Orchestration/Activities/` | New. The old 1.0 activities stay registered and unchanged. |
| `ComposeDigestInput`, `ComposeInsightJsonInput` | Existing | Add `AllowedBranchIds?` and `ExpectedEffectivePairCount?` at the end. |
| `IFreeMonthlyDigestRepository.GetSlotAsync` | Existing | Takes the optional restriction and passes it as a table-valued parameter (Dapper `AsTableValuedParameter`). Checks `EffectiveScopePairs`. |
| The three orchestrators | Existing | See the versioning notes below this table. |

**Versioning for the three orchestrators.**

- `FreeDigestGenerateOrchestrator`, `FreeDigestInsightJsonOrchestrator` and
  `FreeDigestSendOrchestrator` all move to **2.0**.
- Register each 2.0 version alongside its 1.0 version. The scheduler enqueues 2.0.
- The note on the Send orchestrator saying it is "left at 1.0" no longer applies, because its sequence
  of activities changes.
- Remove the 1.0 versions only after one full week plus the freshness window has passed with no 1.0
  instance running.

**Configuration** (all keys under `FreeDigest:Entitlements:`):

| Key | Default | Rule |
|---|---|---|
| `Mode` | `Off` | Must be `Off`, `Shadow`, `Gate` or `Narrow`. Any other value stops startup. |
| `BaseUrl` | none | Required and must be an absolute URL, unless Mode is `Off`. |
| `ClientEmail`, `ClientSecret` | none | From Key Vault. Required unless Mode is `Off`. |
| `TimeoutSeconds` | 75 | Must be greater than 60. |
| `RateLimit:RequestsPerMinute` | 90 | Must be greater than 0. Raise to 180 only after Q2. |
| `RateLimit:AcquireTimeoutSeconds` | 300 | Must be longer than the worst-case chunk. |
| `LookupBatchSize` | 10 | Between 1 and 25. |
| `SundayCacheMinutes` | 120 | 0 turns the cache off. |
| `TreatDepartmentsAsRestriction` | `true` | |
| `ExpectedIntegrationUserId` | none | Optional. |

**Startup check for `Narrow`.** At startup, run a read-only query to confirm that `sys.parameters`
shows `@EnforceBranchRestriction` and `@AllowedBranchIds` on all five slot procedures. If either is
missing, refuse to start. This follows `FreeDigestRegistration`'s rule: fail at startup, not at 3am.

---

## 11. Tests

### Unit tests: fake `HttpMessageHandler`, same style as `PostInsightJsonActivityTests`

**Request shape**

1. `generate-hmac` and `show-entitlements` receive **byte-identical** bodies.
2. Both keys are always present. `email` is `""`, never null. `user_id` is a JSON **number**, not a
   string.
3. `X-Payload-HMAC` equals `result.hmac`, and both calls send the Bearer token.

**Response parsing**

4. These are **contract failures**, not empty lists:
   - a 200 that uses `customer_ids` (with an underscore);
   - a 200 with `branch_ids` missing.
5. A 200 with `branch_ids: []` is `NoBranches`, not a contract failure.
6. A 404 is always `UserNotActive` and is never read as a 200 body:
   - when its body has `customerids`, `locations` and `departments`;
   - when its body happens to contain `branch_ids`.
7. A 200 with `success:false` is a contract failure. A 500 never becomes "no entitlements".

**Status handling**

8. On a 401, the token is refreshed once and the call retried once. A second 401 refuses the tenant.
9. 429 and 408:
   - a 429 honours the reset headers, within the 60 s limit;
   - three 408s in a row in one chunk make the chunk throw;
   - the tenant-level 408 share refuses the tenant;
   - two 408s in one chunk refuse nothing.

**Token and rate limiter**

10. The token is fetched once across many lookups. It refreshes after `exp`. Concurrent callers share
    one refresh.
11. The rate limiter spaces lookups at no less than the configured interval.

**Effective scope and grouping**

12. `EffectiveScope`:
    - never widens the scope;
    - keeps EA's categories;
    - drops branches that belong to other customers;
    - skips the recipient when the intersection is empty.
13. **Representative invariant:**
    - two users with different EA but the same `E` share a signature;
    - each of them, used as representative, reproduces `E` exactly;
    - users with the same EA but different API sets are split into separate groups;
    - the merge case also holds.

**Orchestrators**

14. **Off-mode parity:** the 2.0 orchestrators produce the same groups, signatures and representatives
    as 1.0 on the same fixtures.
15. **Gate:** recipients whose scope would narrow are left out, and everyone served gets identical
    output.
16. **Monday:**
    - with no pending artifacts, no API calls are made;
    - recipients already claimed are never looked up;
    - if a recipient's API set changed after Sunday, they match no artifact and are skipped;
    - if the API is down, nothing is sent.
17. **History hygiene:** the fake returns marker values for the token and HMAC. Serialise every new
    activity input and output, and assert the markers never appear.
18. **Startup refuses to boot when:**
    - `Mode=Narrow` and the SQL parameters are missing;
    - the timeout is 60 s or less;
    - `Mode` is not a known value.
19. When the department setting is `true`, a non-empty `department_ids` is skipped.
20. If SQL returns an `EffectiveScopePairs` that differs from the expected count, the digest is
    refused.
21. **Identity check (once Q7 is agreed):** if the echoed `user_id` or email differs from ours, it is
    a contract failure.

### Integration tests

Run Shadow against UAT tenant 1285 and the profiles in CLAUDE.md §11. Never run on 1082. Vinay runs
the SQL parity and containment checks in sql/42.

---

## 12. Migration path and promotion gates

| Step | What ships | What must be true first |
|---|---|---|
| **M0** | All code, with the orchestrators at 2.0 and `Mode=Off` | The Off-mode parity tests pass. Behaviour is unchanged. |
| **M1 Shadow** | `Mode=Shadow`, Sunday lanes only, for at least **2** weekly cycles | Secrets are set up. The per-process environment check passes. Q1-Q4 and Q6-Q10 have been asked. |
| **M2 Gate** | `Mode=Gate` | Every item in the Gate checklist below this table. |
| **M3 SQL** | sql/43, the edits to sql/34-41, sql/99 and sql/42, and the CI check. Built in parallel with M1. | Vinay installs it in UAT, and the parity and containment checks pass on the CLAUDE.md §11 tenants. |
| **M4 Narrow** | `Mode=Narrow` | M3 is deployed, so the startup check passes. The projected LLM calls are no more than twice today's, or the owner signs off the budget (Sec.8). |

**Gate checklist (M2).** All of these must hold:

- The BA ruling from Sec.4.5 is recorded.
- There were **no** contract failures in any Shadow run.
- Fewer than **1%** of lookups returned a tenant mismatch.
- Fewer than **2%** returned 404, or every 404 is explained.
- Fewer than **1%** returned 401 or 5xx.
- Q7 or Q10 is agreed and the per-recipient identity check is on, or the owner explicitly accepts
  going without it.
- The owner accepts the later Monday finish (Sec.7.2), or RegTrack has raised the limit.
- The owner explicitly accepts the **coverage loss**: the share of recipients Shadow classed as
  `api_subset`, `overlap` or `disjoint`.

**Why Gate goes live even if the coverage loss is large.** Suppose Shadow shows that many recipients'
scopes would narrow. That means **production is showing them more than they are entitled to today**.
Once the BA has ruled that the API is the authority, refusing those recipients is the fail-closed
choice (non-negotiable 2) until Narrow restores their coverage. Staying in Shadow would mean knowingly
continuing to over-disclose.

**Rollback is configuration only:** Narrow, then Gate, then Shadow, then Off.

- The 2.0 orchestrators work in every mode.
- The SQL change is backward compatible, because the new flag defaults to 0.
- If the mode changes between Sunday and Monday, only recipients whose scope narrowed miss that week
  (Sec.5.4).

---

## 13. Confirmations required before the dependent step ships

**RegTrack API team**

| # | Question | Blocks |
|---|---|---|
| Q1 | Is "source 8" the same set of branches as `ComplianceCategoryMgmtUser`? Which table and column is the "source"? Shadow's three-way comparison of EA, CCMU and the API also answers this from the data. | Gate |
| Q2 | Does `generate-hmac` count toward the 100/min limit? Can the integration user be given 600/min or more, or a batch endpoint? | Gate (the Monday finish time) and the 180/min setting |
| Q3 | How long does a token last? Is it a JWT with `exp`? | M1 |
| Q4 | For management-role users, is `department_ids` a restriction or just information? For a user with several roles, do we get the union of their roles or only their main role's set? | Setting `TreatDepartmentsAsRestriction` to false |
| Q5 | Does the stored procedure leave out deleted or deactivated branches (`Status = 0`)? Our intersection handles this on our side; we need the answer to read the metrics correctly. | Nothing |
| Q6 | Which database does `regtrackapi.avantisregtec.in` read from? Is there a UAT host? | M1 |
| Q7 | Can the response **echo the resolved `user_id` and email**, so we can confirm it answered about the user we asked for? | Gate (strongly recommended; see the Gate checklist) |
| Q8 | Is the HMAC computed over the raw bytes of the body, so that whitespace and key order matter? | M1 |
| Q9 | Can the service user be limited to these two endpoints and to our IP addresses, and how is its secret rotated? | M1 |
| Q10 | When `user_id` is set and `email` is `""`, does the lookup use the id only? If both are sent, must both match? | M1; the alternative to Q7 |
| Q11 | For a 404, does "active" mean `User.IsActive = 1 AND IsDeleted = 0`? | Nothing (it explains the metrics) |

**BA**

- The ruling in Sec.4.5, recorded in a real `docs/OPEN_DECISIONS.md`.
- A ruling on the department axis, if Q4 says `department_ids` is a restriction.

**Vinay**

- The SQL contract in Sec.5.6.
- The CI check.
- Validation in UAT.

**Owner**

- Whether the later Monday finish is acceptable.
- Whether the coverage loss under Gate is acceptable.
- The LLM budget for Narrow.
- The priority of the paid-tier ADR described in Sec.1.3.

---

## 14. Consequences

**Positive**

- No free email or card will contain a branch that RegTrack's entitlement service withholds from that
  recipient, or a tenant the user is no longer mapped to.
- The category axis stays two-dimensional and owned by SQL.
- The free lane gains a per-user check that the user still belongs to the tenant. It had none before;
  this applies the IDOR rule's intent to the free tier.
- Scope is reconciled across the C#/SQL boundary through `EffectiveScopePairs`.
- Every step can be undone through configuration alone.

**Negative**

- The API adds about 1-2 hours of time across the fleet on Sunday, and again on Monday. The Monday
  finish moves toward 10:00 IST unless the rate limit is raised.
- A new external dependency. If it goes down, affected tenants lose that week's digest (fail closed).
- Gate can leave out a large share of recipients until Narrow ships.
- Narrow can multiply LLM calls for tenants whose users' entitlement sets differ.
- A seven-file SQL change, with a new type and a new error block.
- Sites that are configured but dormant drop out of management users' digests wherever RegTrack
  excludes them.

**Unchanged**

- Numbers come only from SQL. The LLM never sees the API's answer.
- Tokens per call.
- One LLM call per group per lane.
- The claim tables, the artifact schema and the send path.
