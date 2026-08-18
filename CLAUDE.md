# CLAUDE.md — RegTrack Insights (MAF Core)

**Repo:** `trpl-regtrack-microsoft-agentic-framework-core`
**Read this before writing any code.** It is the operating contract for this repository.

---

## 1. What you are building

**RegTrack Insights** — an agentic compliance-analytics engine for TeamLease Regtech's
RegTrack platform (~2,290 active tenants). It answers questions a Chief Compliance
Officer actually asks: *how healthy is my compliance estate, what am I on the hook
for, and am I getting better or worse?*

Two products, already present in `Product` table of `vitComplianceSystem`:

| Id | Name | Tier | Delivery |
|---|---|---|---|
| 18 | RegInsights Basic | Free | Weekly email digest |
| 19 | RegInsights Pro | Paid | In-app interactive report |

### Authoritative documents — read before implementing a component

| Document | Use it for |
|---|---|
| `docs/RegTrack_Insights_System_Design_v1.md` | **The spec.** Every decision + rationale. Section refs below point here. |
| `docs/DIMENSION_SPECS.md` | Contracts for all 9 dimensions |
| `docs/RegTrack_Classification_Dictionary_v1.xlsx` | BA-signed status/enum semantics |
| `PHASE_1A_BUILD_BRIEF.md` | Phase 1a tasks, acceptance criteria, validation findings |
| `sql/01`–`sql/06` | Working, production-validated SQL |

---

## 2. The five non-negotiables

Violating any of these is a build-breaking error, not a style preference.

1. **Determinism owns truth and safety; the agent owns judgement.**
   Scope resolution, SQL execution, reconciliation, and rendering are **permanently
   deterministic**. The agent decides *what matters, in what order, and how to say
   it* — never *what a number is* or *who may see it*. (§3.1)

2. **Fail closed, and fail loudly.**
   Unknown enum, empty scope, failed reconciliation, unverifiable claim → **refuse
   and log**. Never guess, never default. A refused report is a good outcome; a
   wrong number carrying a provenance badge is a catastrophic one. (§6.5, §11)

3. **Every number is reconciled before it is shown.**
   Decompositions sum to totals; per-dimension sums tie to tenant control totals.
   Control totals must be captured **in the same instant** as the data they
   reconcile — never carried from an earlier query. (§6.8)

4. **Semantics live in the classification dictionary, never in a WHERE clause.**
   No `WHERE status NOT IN (...)` literal anywhere. Join `vInsightsStatusCurrent`.

5. **The narrative may only assert what the data layer verified.**
   Every quantitative/comparative claim maps to a typed assertion. Comparatives are
   **computed** in SQL (`rank`, `vs_tenant_avg_pp`, `share_pct`), never phrased by
   the LLM. (§6.10)

---

## 3. Hard rules

### Never
- Write `status NOT IN (4,5,15,18)` or any status literal. Join the dictionary.
- Filter or bucket on `ComplianceStatus.Name`. **Bucket by ID only** — names are
  duplicated and contain whitespace variants.
- "Fix" `ProductMapping.IsActive = 0` to `= 1`. **It is inverted: 0 = ENABLED.**
- Derive the tenant from user data. `@CustomerID` is always an input, always
  re-validated server-side against the user's eligible set. (IDOR risk — §5.6.2)
- Build dimension rows from the fact set. Build from the **dimension/entity list**,
  or empty members vanish — and an empty member is often the most important finding.
- Emit one finding per flagged member. Route every detector through the **emission
  policy** (§4 below).
- Let an LLM author SQL, resolve scope, or validate its own output.
- Use `SELECT ... INTO #t` then `ALTER TABLE #t ADD col` then reference `col` in the
  same procedure body — T-SQL name resolution fails. Declare the table explicitly.

### Always
- Filter `IsDeleted = 0` at **every** hop (User, Customer, CustomerBranch).
- Constrain queries on **both** scope axes: `(BranchID, CategoryId)`.
- Count instances at **every** node of the entity tree — leaf *and* intermediate.
- Anchor entity recursion on **apex OR orphan** (see §5 traps).
- Validate against **several tenants with different profiles**. Never one.

---

## 4. The detector emission policy — mandatory for every dimension

Absolute thresholds broke on every detector built so far. Flag rates measured across
8 production tenants ranged from 1.3% to 84% for the *same* rule.

```
flagged_pct >  20%  →  ONE aggregate finding (severity medium)
                       "N of M members (X%) show <pattern>"
                       individual findings SUPPRESSED
flagged_pct <= 20%  →  individual findings, capped at TOP 5 by materiality
```

Reference implementation: `#detector` table in `sql/05_dimension_location.sql`.
Reuse the shape. Any threshold must be **peer-relative to the tenant's own
distribution**, never an absolute constant.

Also handle these boundaries — all found in production:
- **Empty peer sample** (no member meets the materiality floor) → fall back to a
  wider sample and emit `degraded_peer_sample`. Never infer from an empty set.
- **Single member** → suppress comparatives. "Rank 1 of 1" is vacuous.
- **Zero obligations** → cannot assess; do not infer a verdict.

---

## 5. Schema traps — every one found empirically, several got the wrong answer first

| Area | Trap |
|---|---|
| `ProductMapping.IsActive` | **INVERTED** — 0 = enabled, 1 = disabled |
| `RiskType` | 3=Critical, **0=High**, 1=Medium, **2=Low** |
| `ComplianceStatus` | 23 codes; duplicate names ("Approved" = 7 *and* 9); whitespace variants |
| Status 15 vs 18 | Different statuses — reviewer-final vs performer-proposed NA. **Not typos** |
| Status 16 vs 17 | Same — performer vs reviewer "Not Complied" |
| Status 9 | Closed **but late** — not overdue, but *is* a delayed completion |
| `User` | `IsDeleted` ≠ `IsActive`. Keep deactivated users, **flag** them |
| `EntitiesAssignment` | 2-D scope; column misspelled `ComplianceCatagoryID` |
| `CustomerBranch.ParentID` | Active branch may sit under a **soft-deleted** parent. Apex-only recursion loses the whole subtree — measured at **89% of one tenant's estate**, and 3 tenants would have received a 100% EMPTY report |
| Category join | **Only** via `ComplianceInstance → Compliance → Act.ComplianceCategoryId` |
| `UserCustomerMapping` | **NOT** a reliable user↔tenant link — many users have zero rows |
| `RecentComplianceTransactionView` | Actively refreshed; flow metrics drift. ~10 rows have NULL status |
| `ComplianceTransaction.Penalty` | Essentially empty (~₹300 total). Report **exposure**, never *incurred* |
| `NatureOfCompliance` | ~49% "Others" on the reference tenant — declare the gap |
| `Compliance.Frequency` | ~28.5% NULL |

---

## 6. Architecture

**MAF (Microsoft Agent Framework) 1.0, .NET 8.** Not LangGraph — the engine is
majority-deterministic .NET/SQL and MAF keeps that native. (§3.2)

```
Angular  →  RegTrack API (auth, endpoints, enqueue)
                 ↓  ~1ms enqueue
         SQL Server Durable Task hub
                 ↓  dequeue
         Insights Worker  (PRIVATE — no ingress)
           gate → scope → procs → assertions
           → compose(agent) → narrate(agent) → PUBLISH GATE
           → normalizer → DOMPurify → encrypt → blob
                 ↓
         Blob artifact + SQL index row
```

- **Worker is queue-driven and private.** No public endpoints, no ingress.
- **API endpoints live in the existing RegTrack API** — auth already lives there.
- **Durable Task SQL Server provider** for state. **Not** Postgres (not viable),
  not Azure DTS.
- **Orchestration versioning ON from day one** — replay against changed workflow
  code throws non-determinism errors otherwise. (§3.7)
- **Orchestrator body must be deterministic.** LLM calls, `GETDATE()`, DB access
  all go in **activities**. This matches Durable Task's programming model exactly.
- **LLM activities must be idempotent** — a replay must not double-bill tokens.

---

## 7. Tech choices

| Concern | Decision |
|---|---|
| Data access | **Dapper** for the read path — everything is stored procs returning multiple result sets (`QueryMultiple`). EF Core only for the `GeneratedReport` index row. |
| LLM provider | Claude (first-party MAF provider) |
| Observability | OpenTelemetry → **LangFuse** (LLM traces) + Grafana/Loki (app metrics) |
| Durable state | Durable Task **SQL Server** provider |
| Secrets/keys | Azure Key Vault, reusing the **DocAI envelope-encryption pattern** |

---

## 8. Working with the other repos

Two repos already exist and are **private**; you have them locally, I do not.

| Repo | Contains |
|---|---|
| RegTrack API (.NET 8) | Where Insights endpoints go |
| `trpl-regtrack-angular-web` | Where Insights UI goes |

> **Before writing code that lands in an existing repo, READ that repo and match
> its conventions.** Namespaces, DI registration, logging, error handling,
> configuration, controller style, test framework. **Do not invent a parallel
> style.** If a convention is ambiguous, ask rather than guess.

Also determine, by inspection: whether the shared domain libraries (scope
resolution, data access) should be built here and consumed by the API, or already
exist in the backend repo and be consumed from there.

---

## 9. Build order

Do not reorder. Each phase is testable and nothing gets built twice.

**Phase 1a — foundation (no LLM)**
1. Classification dictionary (`sql/01`) — tables, seed, fail-closed coverage check
2. Golden regression (`sql/02`) — **wire into CI before writing dimension procs**
3. Scope resolution (`sql/03`) — deterministic, 2-D, fail-closed, unit-tested
4. Entity hierarchy (`sql/04` pt 1) — apex-or-orphan recursion, reconciliation
5. Entitlement gate (`sql/04` pt 2) — remember `IsActive = 0` means enabled

**Phase 1b — data layer**
6. Dimension procs, starting from `sql/05` (Location) as the template
7. Assertion builder — typed facts **and** computed comparatives
8. Publish gate — reconciliation, claim-checker, scope post-flight audit

**Phase 1c — free tier (ships FIRST)**
9. Weekly digest: gate → ~15 aggregates (`sql/06`) → capped LLM → email
10. Email infra: provider, SPF/DKIM/DMARC, recipients, bounce/unsubscribe

> Free tier ships first deliberately: it exercises the dictionary, scope service
> and procs end-to-end with **minimal LLM surface**, and delivers customer value
> while the paid engine is built.

**Phase 1d — paid engine**
11. MAF workflow graph, durable on SQL Server, versioning on
12. Composition + narrative agents with **bounded** reflection loops
13. Report generation (Way 1) → Emit Normalizer → DOMPurify → Playwright QA
14. Persistence: blob + index, envelope encryption, view-time re-auth, short SAS
15. Hub UI, scope-owned history, cooldown, progress streaming
16. Failure/refusal UX — all four classes
17. Cost instrumentation + circuit breakers — **launch requirement**

**Phase 2 (do not build now):** snapshots → delta + Q&A + backlog trend;
Way-2 trusted renderer; custom agentic-SQL dimensions; entity-count pricing tiers.

---

## 10. Definition of done for any component

- [ ] Golden regression passes on **≥5 tenants with different profiles**
- [ ] Reconciliation THROWs on mismatch — never warns
- [ ] Fail-closed paths tested (empty scope, unknown enum, empty sample)
- [ ] Detector output routed through the emission policy
- [ ] No status literal, no name-based bucketing anywhere
- [ ] Scope constrained on both axes, with post-flight audit
- [ ] Boundary cases covered: single member, zero obligations, empty peer sample

---

## 11. Testing discipline — read this twice

**Eight defects were found during design. Every single one passed on the first
tenant checked.** Single-tenant validation in this codebase is not weak evidence —
it is actively misleading.

The strategy had to evolve twice:
1. Single tenant → **multi-tenant with different profiles** (caught 5 defects)
2. Multi-tenant → **targeted boundary search** once a failure mode was closed
   structurally (caught 2 more: empty peer sample, single-member comparative)

**Rule:** when a failure mode is closed by construction, stop sampling and start
hunting boundaries — empty samples, single members, zero denominators. Random
tenants will not find them.

Useful tenant profiles for validation:

| Tenant | Why |
|---|---|
| 1490 | Small, well-understood, 2 apex entities (lopsided) |
| 1403 | 6 balanced legal entities |
| 1472 | Large (819 branches), 24 orphan roots |
| 29 | **89% of estate under a soft-deleted parent** |
| 5 | 20 distinct statuses; newly-onboarded profile |
| 1308 | Low closure ratio; large ghost-entity share |
| 522 | **No branch ≥50 instances** — empty peer sample |
| 2480 / 1807 | **Single-branch** tenants |
| 1216 | Very large (1.49M past-due schedules) — perf testing |

---

## 12. Open items — confirm before the dependent component ships

| # | Item | Blocks |
|---|---|---|
| O-4 | LangFuse .NET trace shape (~half-day spike) | Observability |
| O-9 | Does RegTrack already have a tenant switcher? Inherit it if so | Multi-tenant UI |
| O-3 | "Others" nature reclassification (BA worksheet issued) | Nature quality (not a blocker) |
| O-11 | Repair hierarchies for the 114 tenants with orphan roots | Data hygiene (not a blocker) |

---

## 13. When something surprises you

**Believe the data, not the column name.** Every trap in §5 was found by querying,
never by reading a name. If a value looks inverted, backwards, or duplicated — it
probably is. Verify against production before building logic on an assumption, and
add what you find to §5.
