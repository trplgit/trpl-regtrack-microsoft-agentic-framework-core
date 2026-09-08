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
| `docs/DIMENSION_SPECS.md` | Contracts for the 9 core dimensions (`22`-`27` documented in their headers) |
| `docs/METRIC_CALCULATION_REFERENCE.md` | **Every data point: definition, derivation, traps, QA checks** |
| `docs/PAID_TIER_SAMPLE_REFERENCE.md` | The target report shape, block by block, and what is not yet built |
| `docs/LEGAL_BRIEF_peer_comparison.md` | Original counsel brief - **answered by research, see addendum** |
| `docs/LEGAL_BRIEF_research_addendum.md` | Research findings: state is a defensible primary key but insufficient alone; add headcount band |
| `samples/` | Reconciled real-data report samples (anonymised) |
| `docs/RegTrack_Classification_Dictionary_v1.xlsx` | BA-signed status/enum semantics |
| `PHASE_1A_BUILD_BRIEF.md` | Phase 1a tasks, acceptance criteria, validation findings |
| `docs/API_CONTRACTS.md` | The five API endpoints; the IDOR rule |
| `docs/GOLDEN_FIXTURES.md` | CI fixture database — the ONLY place absolute values can be asserted |
| `docs/CONFIGURATION.md` | Every tunable, with the section that justifies it |
| `docs/SOLUTION_STRUCTURE.md` | Project layout; Durable Task shape |
| `prompts/` | Agent prompts — the D7 contract is enforced there |
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
- Let a helper procedure return a result set if another procedure calls it. A
  pre-flight `EXEC` that emits a grid makes that grid result set **#1 of the
  caller**, silently shifting the documented contract. Success is silence;
  failure is a `THROW`.
- Put **non-ASCII characters in SQL source** — not in comments, not in string
  literals. No box-drawing, em-dashes, arrows, stars, `§`, `≠`, `×`. Use `=`,
  `-`, `->`, `*`, `Sec.`, `!=`, `x`. (See §5a.)
- Assert a property of the **data** inside a suite that `THROW`s. If an assertion
  can fail on legitimate data, it belongs in a warning path. (See §11.)
- Detect characters with plain `LIKE`. The default collation is accent- and
  width-insensitive, so `LIKE '%'+NCHAR(8377)+'%'` matches things that are not
  that character. Always `COLLATE Latin1_General_BIN2`, or enumerate code points
  with `UNICODE()`.
- Name a field `SumOfRows` when the rows do not sum to the total. (See §4a.)
- Write a detector whose Flagged predicate can match rows outside its Eligible population. `Flagged` and `Eligible` MUST come from the same set. sql/05 flagged `single_point_of_failure` with no `Instances > 0` guard, so all 78 zero-obligation branches flagged too - **120 flagged of 99 eligible, 121.2%**. Every zero-work row will look like a single point of failure, because it has no people on work it does not have. Check every detector: can the flag fire on a row the Eligible count excludes?
- Leave a temp table unaliased when an inline subquery in the same statement reads it too. `SELECT ... (SELECT COUNT(*) FROM #rows ...) ... FROM #rows ORDER BY col` raises **"Ambiguous column name"** - and it fails at RUN TIME, after earlier result sets have already been emitted, so a caller reading only the first result set never sees it. Alias both.
- Join `RecentComplianceTransactionView` directly - go through `tvfInsightsLatestStatus`. (See §5.)
- Drop a past-due schedule because it has no transaction. **BA ruling: never-touched = overdue.**
  `tvfInsightsOverdueSchedules` includes them with `NeverTouched = 1`.
- Use `SUM(CASE WHEN ... NOT EXISTS (...) ...)` — SQL Server rejects an aggregate
  over a subquery. Use a `LEFT JOIN` and test for `NULL`.

### Always
- Filter `IsDeleted = 0` at **every** hop (User, Customer, CustomerBranch).
- Constrain queries on **both** scope axes: `(BranchID, CategoryId)`.
- Count instances at **every** node of the entity tree — leaf *and* intermediate.
- Anchor entity recursion on **apex OR orphan** (see §5 traps).
- Validate against **several tenants with different profiles**. Never one.
- Use the **same estate definition everywhere**. Every query counting "the
  tenant's obligations" applies the same filters — including
  `Compliance.IsDeleted = 0`. Two components can each reconcile internally and
  still disagree with each other.
- Keep the **rollback script in step** with the install scripts. Verify
  programmatically: every `CREATE` in `01`–`16` has a matching `DROP` in `99`.
- Select objects to change **by the condition, not by a list of names** written
  from memory. `WHERE definition LIKE ...` is self-completing; a hand-written
  list silently misses things.

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

## 4a. The residual rule for dimension contracts

Some dimensions have members that not every instance belongs to (an instance may
have no `NatureOfCompliance`, no `DepartmentID`, no assignee). Rows then cover
only part of the estate, which is **correct** — but it must be legible:

```
rows sum to the total        ->  name the field  SumOfRows
rows cover only part of it   ->  name it for what it covers, and pair it
                                 with the named residual
```

| Dimension | Field | Residual |
|---|---|---|
| Nature | `CategorisedInstances` | `UntaggedInstances` |
| Departments | `AssignedInstances` | `UnassignedInstances` |
| Users | `AssignedInstancesDistinct` | `UnassignedInstances` |
| Location, Entity, Risk, Act, Internal, Event | `SumOfRows` | none — rows sum exactly |

A field called `SumOfRows` that does not equal `ScopedInstances` reads as a bug.
On one tenant that was a phantom 3,946-instance gap.

> Users shows the other half of this: `SumOfPerUserInstances` (8,651) is
> deliberately larger than the estate (4,814) because an instance has both a
> performer and a reviewer. Keep distinct and non-distinct counts as **separate,
> differently-named** fields — conflating them produced the impossible 155%
> concentration figure during design.


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
| Join hints on TVFs | **NEVER use `INNER HASH JOIN` (or any join hint) on a query involving an inline TVF.** A join hint also forces join ORDER for the WHOLE statement, *including inside the inlined function* - so `tvfInsightsOverdueSchedules` could no longer filter by tenant before touching `ComplianceScheduleOn` (29.4M rows). Measured on a **7-branch** tenant: bare join 877 ms, hinted 26,492 ms, materialised-and-indexed 438 ms. Instead: **materialise each side into a temp table with a clustered index, then join** - real cardinality without constraining order |
| Scope FIRST, then reach | Put `tvfInsightsScopedInstances` into an indexed temp table before joining `ComplianceScheduleOn` / `ComplianceTransaction`. With the tenant filter three joins deep neither can be seeked: TimelinessFY was **183,502 ms**, scope-first is **157 ms** for identical output |
| `RecentComplianceTransactionView` | **Do not join it.** Non-indexed view over 45.7M `ComplianceTransaction` rows; a tenant filter three joins away is not pushed through, so it computes for ALL rows first. `GoldenInvariants` and `BacklogAging` timed out (>4 min) on production. Use `tvfInsightsLatestStatus` - explicit `TOP 1` seek per schedule on `IX_CT_CSO_Dated_ID`, 611 ms for 52K schedules, verified identical on 400 samples. Its inner join also HID schedules with no transaction at all |
| `ComplianceTransaction.Penalty` | Essentially empty (~₹300 total). Report **exposure**, never *incurred* |
| `NatureOfCompliance` | ~49% "Others" on the reference tenant — declare the gap |
| `Compliance.Frequency` | ~28.5% NULL |
| **Ownership has TWO mechanisms** | `ComplianceAssignment` (RoleID=3, instance-level) AND `ComplianceScheduleOn.Performerid` (schedule-level, populated on **99.8%** of schedules). Reading only the first overstates "ownerless" by **181x** - 44,480 reported vs 245 actually unowned on one tenant. Seven deployed dimensions have this defect; see `docs/SQL_CHANGES_REQUIRED.md`. The two-way split is genuinely predictive (16.3% vs 64.9% overdue) - keep the distinction, fix the label |
| `CustomerBranch.Status` | **A SECOND active flag.** `Status = 0` = deactivated: obligations remain but are frozen - not reported, no schedules or alerts. Filter `IsDeleted = 0 AND Status = 1` everywhere. Omitting it overstated overdue by **28%** on one tenant |
| `CustomerBranch.Type` | **Kind of location**, lookup `dbo.NodeType` (15 rows). 77% are the generic `Branch`; `Store` is rare and carries an identical profile - treat as one class. 17 orphan values exist (23-75) not in `NodeType`; classify as `unknown`, exclude, declare |
| `CustomerBranch.ComType` | **Legal entity type** (Public/Private/Listed/LLP...), NOT location type. Its ID range overlaps `LocationType.ID` by coincidence; a join on it produces plausible garbage. **No FK references `LocationType` from anywhere** - verify relationships in `sys.foreign_keys`, never from overlapping IDs |
| `ComplianceInstance.IsAvantis` | **OBSOLETE - ignore it.** Set on 97.6% of instances (3,580,854 of 3,670,054), so it discriminates nothing, and 1.9M of those are NOT Labour. The canonical view `vw_ci_ActiveInstance` maps `IsAvantis -> Labour`; that mapping is **stale**. Use `Act.ComplianceCategoryId` for category |
| Pre-flight procs | A helper that `SELECT`s shifts the caller's result-set contract by one — and only for callers that invoke it, so offsets differ per procedure |
| `Compliance.IsDeleted` | Instances can reference a **soft-deleted** Compliance master (70 on one tenant). Omitting the filter makes the control total disagree with every dimension |
| SQL file encoding | The deployment path is **not** UTF-8 aware. It corrupted a pre-existing RegTrack proc (`USP_GetEscalationCounts_Mobile_Statutory`) as well as ours |
| Character detection | Default collation is accent-insensitive; `LIKE` gives false positives when detecting non-ASCII |

---

## 5a. SQL source must be pure ASCII

The deployment path reads `.sql` files as ANSI/Windows-1252, so every multi-byte
UTF-8 character is decoded as several Latin-1 characters: `═` becomes
`â•<0x90>`, `—` becomes `â€"`. ~2,000 characters were corrupted across three
procedures and stored in the database, visible in comments **and inside message
strings that surface in output**. A source file was also corrupted at rest after
being opened and re-saved by a non-UTF-8 editor.

The scripts already written in pure ASCII were **completely immune**. That is the
fix: keep SQL source ASCII-only and the entire bug class disappears regardless of
deployment tooling.

Substitutions in use: `=` box-double, `-` box-light/dashes, `->` arrow, `=>`
double arrow, `*` star, `Sec.` section sign, `!=`, `<=`, `>=`, `x` multiply.

**Add to CI — fail the build on any result:**

```sql
SELECT o.name FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
WHERE o.name LIKE '%Insights%'
  AND m.definition COLLATE Latin1_General_BIN2
      LIKE N'%[^ -~' + NCHAR(9) + NCHAR(10) + NCHAR(13) + N']%';
```

`COLLATE Latin1_General_BIN2` is required — without it the check silently passes.


## 5b. Error code allocation

Every `THROW` code is unique across the whole codebase, and each file owns a
block of ten. Within a block:

```
x0        SCOPE DENIED
x1 - x4   RECONCILIATION FAILED
x5 - x9   DICTIONARY / MASTER DATA GAP
```

| Block | File | Block | File |
|---|---|---|---|
| 51000-51009 | `01`, `02` | 51120-51129 | `14` event |
| 51010-51019 | `03` scope | 51130-51139 | `15` digest log |
| 51020-51029 | `04` entity/entitlement | 51140-51149 | `16` suppression |
| 51030-51039 | `05` location | 51160-51169 | `21` licence |
| 51050-51059 | `07` entity | 51170-51176 | `22`-`25` **shared** (see note) |
| 51060-51069 | `08` risk | 51190-51199 | `26` forward risk |
| 51070-51079 | `09` nature | 51200-51209 | `27` coverage gaps |
| 51080-51089 | `10` departments | 51040-51049, 51150-51159, 51177-51189, 51210+ | **free** |
| 51090-51099 | `11` act | | |
| 51100-51109 | `12` users | | |
| 51110-51119 | `13` internal | | |

> **Note on 5117x.** Files `22`-`25` (BacklogAging 70-71, TimelinessFY 72,
> ForwardPipeline 73-74, EvidenceIntegrity 75-76) share one block. That
> breaks the one-block-per-file convention, but they were deployed to
> production before the convention was enforced and each code is still
> unique across the codebase - so they stay. New files take a fresh block.

> **[TRAP] One code per CONDITION, never per category of condition.** Seven files
> originally reused a single code for two or three different failures - one used
> 51101 for three distinct reconciliation errors. An operator seeing the code
> could not tell which check failed without reading the message text, and two
> files had also collided on 51130 outright. A code that does not identify a
> condition is not doing its job.

**Adding a new file:** take the next free block, declare it in the header
comment (`Error block NNNNN-NNNNN`), and follow the x0/x1-x4/x5-x9 convention.

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
- [ ] Helper procedures return **no** result set (`EXEC dbo.usp_Insights_AssertStatusCoverage`
      must produce no grid, so every dimension's grid #1 is `control_totals`)
- [ ] Encoding check returns zero rows (§5a)
- [ ] Rollback script drops every object the install scripts create
- [ ] Residual fields named per §4a

---

## 11. Testing discipline — read this twice

### Two tiers of test, and why the distinction matters

**Structural invariants** (`usp_Insights_GoldenInvariants`) assert relationships
that hold *regardless of what the data contains* — algebra and graph traversal.
These **`THROW`**. A failure means the **code** is wrong.

**Data-sanity checks** (`usp_Insights_StatusDataQuality`) are observations about a
particular dataset. These **warn**. A failure usually means the **data** is
unusual.

This is not pedantry. An invariant once asserted that imprisonment-bearing items
concentrate on `RiskType 3` — true of production (98.7% across 528 tenants) but
inverted in a test environment (96.8% on `RiskType 0`). As a `THROW`ing
invariant it turned the entire suite red permanently, and **a permanently-red
suite gets switched off**, which costs far more than the check was ever worth.

> **Test-environment data is arbitrary.** Absolute values can only be asserted
> against the golden fixture database (`docs/GOLDEN_FIXTURES.md`). Against any
> other environment, assert **relationships**, not **values**.

### Never validate on one tenant

**Fourteen defects were found building this. Every single one passed on the first
tenant checked** — eight during design, six more when the SQL was first executed
against a real database. Single-tenant validation in this codebase is not weak
evidence — it is actively misleading.

The strategy had to evolve three times:
1. Single tenant → **multi-tenant with different profiles** (caught 5 defects)
2. Multi-tenant → **targeted boundary search** once a failure mode was closed
   structurally (caught 2 more: empty peer sample, single-member comparative)
3. Static review → **actually executing the code** (caught 6 more that no amount
   of reading found: the result-set contract, a mis-classified invariant, two
   components disagreeing on the estate definition, encoding corruption, a
   misleading field name, and a stale rollback script)

**Static review cannot find contract, encoding, or cross-component defects. Run
the code.**

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
