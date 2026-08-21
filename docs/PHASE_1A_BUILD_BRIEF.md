# Phase 1a Build Brief — Foundation

**For:** Claude Code + assisting developer
**Spec:** `RegTrack_Insights_System_Design_v1.md` (read §0.4 first — the five non-negotiables)
**Scope of this phase:** deterministic foundation only. **No LLM code in Phase 1a.**

---

## Why this phase exists

Everything in RegTrack Insights — free tier and paid tier, every dimension, every
report — sits on five deterministic components. If any of them is wrong, every
number downstream is wrong, and the agentic layer will confidently narrate the
wrong number. So these are built first, in this order, with tests before use.

**Phase 1a is complete when the golden regression suite passes against at least
five production tenants with different status profiles.** Not when the code
compiles.

---

## Deliverables, in build order

### 1. Classification dictionary — `sql/01_classification_dictionary.sql`

**Status: written, ready to run.** Idempotent T-SQL.

Creates:
- `dbo.InsightsDictionaryVersion` — version registry (reports pin the version used)
- `dbo.InsightsStatusClassification` — 23 statuses × 3 facets, v1.0 seeded
- `dbo.InsightsEnumPolarity` — RiskType, inverted flags, structural rules
- `dbo.vInsightsStatusCurrent` — current-version view
- `dbo.usp_Insights_AssertStatusCoverage` — fail-closed coverage check
- `dbo.tvfInsightsOverdueSchedules` — the canonical overdue predicate

**Acceptance:**
- [ ] Script runs clean and is re-runnable
- [ ] `EXEC dbo.usp_Insights_AssertStatusCoverage` returns `StatusCoverageComplete = 1`
- [ ] `SELECT * FROM dbo.vInsightsStatusCurrent` returns 23 rows
- [ ] The `CK_ISC_Coherent` constraint rejects an attempted insert of
      `OverdueEligible = 1` with `ClosureClass = 'completed'`

**Do not:**
- Write `WHERE status NOT IN (...)` anywhere in the codebase. Join the dictionary.
- Filter or bucket on `ComplianceStatus.Name`. Bucket by ID only.
- "Fix" `ProductMapping.IsActive = 0` to `= 1`. It is inverted by design.

---

### 2. Golden regression suite — `sql/02_golden_regression.sql`

**Status: written and validated against 5 production tenants.**

`dbo.usp_Insights_GoldenInvariants @CustomerID` runs 7 drift-proof invariants
(G-1 … G-7) and THROWs on any failure. §B of the file specifies the frozen
CI fixture database (F-1 … F-10) that still needs building.

**Acceptance:**
- [ ] Passes against **at least 5 tenants with different status profiles** —
      a single tenant proves nothing (see the war story below)
- [ ] Wired into CI, blocking on failure
- [ ] Fixture database F-1 … F-10 built with the hand-verified expected values

> **War story — read this.** The first version of invariant G-2 omitted the
> status-17 term. It passed on four production tenants and failed on the fifth,
> which had 416 past-due schedules in status 17 while the others had zero.
> The test itself had exactly the bug the dictionary exists to prevent: an
> incomplete enumeration that is right *by luck* on the data you look at first.
> **Never validate an invariant on one tenant.**

---

### 3. Scope resolution — `sql/03_scope_resolution.sql`

**Status: written and validated against production.** The .NET service is a thin wrapper over these objects.

Creates `tvfInsightsScopePairs`, `usp_Insights_ClassifyScope`, `tvfInsightsScopedInstances`, `usp_Insights_AuditScope`, `usp_Insights_FindScopelessUsers`.

**Spec:** §5.5. This is the security boundary.

```
ResolveScope(userId, customerId, productId) → ScopeResult
```

1. Gate: `Customer.IsDeleted = 0` AND RegInsights product mapped (`IsActive = 0`)
2. `scope_pairs = {(BranchID, ComplianceCatagoryID)}` from `EntitiesAssignment`,
   joined to `CustomerBranch` where `IsDeleted = 0`
3. **Empty ⇒ DENY.** Never "no restriction."
4. Classify `tenant_wide` ⟺ all active branches **AND** all categories

Every downstream query constrains on **both** axes; a post-flight audit
re-verifies every returned row on both axes.

**Acceptance:**
- [ ] Unit-tested, deterministic, no LLM anywhere near it
- [ ] Empty scope returns DENY, not unrestricted
- [ ] A user scoped to (branch B, category C) gets **zero rows** for (B, other category)
- [ ] `tenant_wide` classification requires both axes — a user with all branches
      but 8 of 9 categories classifies as **functional**, not tenant-wide
- [ ] Post-flight audit rejects any row outside `scope_pairs`

**Notes:**
- Column is misspelled `ComplianceCatagoryID`.
- Category join is **only** `ComplianceInstance → Compliance → Act.ComplianceCategoryId`.
- Use `EntitiesAssignment`, not `ComplianceCategoryMgmtUser` (§5.5.4 explains why).

---

### 4. Entity hierarchy — `sql/04_entity_and_entitlement.sql` (Part 1)

**Status: written and validated.** Creates `tvfInsightsEntityTree`, `usp_Insights_EntityRollup` (THROWs on reconciliation failure), `usp_Insights_TenantShape`.

**Spec:** §6.7.

- Apex enumeration: `ParentID IS NULL AND IsDeleted = 0`
- `tenant_shape` = apex count; dominance check (>~70% or childless shell ⇒ descend)
- Recursive rollup **counting instances at every node, leaf AND intermediate**
- Reconcile every subtree sum to the tenant control total

**Acceptance:**
- [ ] Fixture F-6 (intermediate node holding instances) rolls up to 25, not 15
- [ ] G-5 passes on all test tenants
- [ ] `IsDeleted = 0` filtered at **every** hop
- [ ] A lopsided tenant (one apex holding >70%) descends one level automatically

---

### 5. Entitlement gate — `sql/04_entity_and_entitlement.sql` (Part 2)

**Status: written.** Creates `usp_Insights_EvaluateGate`.

**Spec:** §5.3.

Cheapest-first, short-circuit before spend:
`free entitled? → paid mapped (supersession)? → tenant opt-out? → recipients? → work`

**Acceptance:**
- [ ] Unmapped customer costs **zero** — no aggregation, no LLM, no email
- [ ] Paid mapping suppresses the free digest (supersession)
- [ ] Entitlement is read at **job execution time**, not cached at schedule time
      (test: mid-cycle upgrade ⇒ next day's digest does not send)
- [ ] `IsActive = 0` means enabled everywhere in the code

---

---

## Validation performed against production

Every component below was tested against live production before shipping, not
just written. Results:

| Check | Result |
|---|---|
| Dictionary seed | All 23 statuses present; trap mappings (7/9/15/16/17/18/2) verified |
| G-2 overdue invariant | **PASS on 5 tenants** after correction (see war story) |
| Scope classification | Correctly identifies 1 tenant-wide vs 2 functional users on the reference tenant |
| 2-D scope protection | Validated — branch-only would leak up to **119,797 instances** on one tenant |
| Entity rollup (G-5) | Flat control total = recursive rollup exactly (5,040 = 5,040, gap 0) |
| tenant_shape | Balanced 6-apex tenant → `apex`; lopsided 96.65% tenant → `descend_one_level` |

### Extended validation — 12 tenants with varied status profiles

Selected empirically for **maximum profile diversity** (rare statuses, size
extremes, differing category restriction), not by convenience. Tenant 5 was the
most adversarial: 20 distinct statuses including the deprecated (8/19),
deviation (21/22) and interim (13/23) codes.

| Invariant | Result |
|---|---|
| **G-1** dictionary coverage | PASS — no status in use outside 1..23 |
| **G-2** overdue invariant | **PASS on all 12** (17 tenants total) |
| **G-5** entity rollup | **FAILED on 3 of 12** → root cause found and fixed (below) |
| **G-9** unknown status | 10 NULL-status rows system-wide, 9 past-due, across 5 tenants |
| **Coverage** | 23 tenants tested individually + all 2,290 scanned for the orphan defect |

**The old overdue definition was wrong for essentially every tenant, in both
directions** — understating by up to 1,571 (tenant 1832) and overstating by up
to 834 (tenant 1099). V-Mart alone was understated by 427.

### ★ Critical bug found and fixed: orphaned subtrees

G-5 failed on three tenants, severely:

| Tenant | Control | Apex-only rollup | Lost |
|---|---:|---:|---:|
| 29 | 1,140 | 130 | **1,010 (89%)** |
| 5 | 12,905 | 12,021 | 884 |
| 1818 | 1,637 | 1,635 | 2 |

**Root cause.** The recursion filters `IsDeleted = 0` at every hop, so it cannot
traverse *through* a soft-deleted intermediate node. Every active branch beneath
a deleted parent becomes unreachable and silently vanishes. Tenant 29 had ~22
active "SNG Golds" branches (46 instances each) under a soft-deleted parent
grouping node.

This is open item **O-5**, which the spec had recorded as theoretical and "not
observed in reference tenants." It is real, and severe.

**Fix (applied).** Anchor the recursion on any ACTIVE branch with no ACTIVE
parent — `ParentID IS NULL` **or** parent missing / soft-deleted / in another
tenant. Verified: **12/12 tenants now reconcile with gap 0.**

Orphan roots are tagged `RootKind = 'orphan'` and surfaced as a data_quality
entry, never silently re-parented. Note **V-Mart has 24 orphan roots** and was
passing only because none of them held unreachable instances.

### ★★ Blast radius of the orphan bug — system-wide

After fixing the recursion, the bug's full scope was measured across all 2,290
active tenants:

| Measure | Count |
|---|---:|
| Tenants with orphan roots | **114** (5% of active tenants) |
| Unreachable branches | 2,954 |
| Instances hidden by apex-only recursion | **5,715** |
| Tenants that would actually lose data | 8 |

Most of the 114 have orphan roots holding no instances — they passed by luck.
The 8 that lose data:

| Tenant | Total instances | Would be hidden | % of estate |
|---|---:|---:|---:|
| 985 | 428 | 428 | **100.0%** |
| 379 | 268 | 268 | **100.0%** |
| 192 | 22 | 22 | **100.0%** |
| 29 | 1,140 | 1,010 | 88.6% |
| 1064 | 6,081 | 3,054 | 50.2% |
| 153 | 233 | 47 | 20.2% |
| 5 | 12,905 | 884 | 6.9% |
| 1818 | 1,637 | 2 | 0.1% |

**Three tenants would have received a completely EMPTY entity report** — zero
obligations shown where hundreds exist.

This is the worst possible failure for a compliance product, and it is exactly
the danger §11.2 of the spec warns about: *"a blank report reads as 'you have no
compliance obligations' — for a compliance tool, a dangerous lie."* The
apex-only implementation would have produced that lie silently, with a
provenance badge, for three real customers.

**Operational follow-up (separate from this build):** the 114 tenants with
orphan roots have a genuine data-hygiene problem — parent entities were deleted
while active children remained. The engine now handles it correctly and declares
it, but the hierarchies themselves should be repaired by the ops/BA team.

### Refinement: unknown statuses are declared, not dropped

Production has 10 rows with a NULL `ComplianceStatusID` (9 past-due, 5 tenants).
The overdue TVF inner-joins the dictionary, so these are excluded — safe, but
silent, which violates "fail closed AND loudly." Refusing an entire report over
1 unknown row in 650,000 would be over-strict.

`usp_Insights_StatusDataQuality` implements the proportionate rule:
- **Unmapped (non-NULL) status** → always RAISE (the dictionary is out of date)
- **NULL status** → declare as data_quality; RAISE only above threshold
  (default: >100 rows or >0.10% of past-due)

Mirrors the labelled-placeholder decision (§11.4): never silently incomplete.

### Four "correct by luck" findings — the recurring lesson

Three separate times in this build, logic that looked right passed on the first
tenant checked and was wrong in general:

1. **Status 17 omitted from the G-2 invariant.** Passed on 4 tenants, failed on
   the 5th (416 past-due status-17 schedules where others had zero).
2. **2-D scope appeared unnecessary.** The first tenant showed ZERO leakage from
   branch-only scoping. Across other tenants, branch-only leaks up to 119,797
   instances — and 31,659 to a single user.
3. **The old overdue definition.** Right by luck on the reference tenants
   because they had no status 7/9 past-due items; wrong on tenants that do,
   in BOTH directions (overstating by 834 on one, understating by 1,571 on another).
4. **Apex-only entity recursion.** Reconciled perfectly on the reference tenant
   (zero orphans). Loses 89% of another tenant's instances.

**Rule: never validate an invariant, a constraint, or a definition on one
tenant.** Pick tenants with deliberately different profiles.

### One spec correction

§1.4 claims stock metrics were "rock-stable" while only flow metrics drift.
Qualify this: the reference tenant's instance count moved from 4,791 to 5,040
during the design session. That is genuine configuration growth, not volatility
— but control totals must be captured in the same instant as the data they
reconcile, never carried over from an earlier query.

---

### Phase 1b begun — Location dimension (the worked exemplar)

`sql/05_dimension_location.sql` implements §7.3: five result sets (control
totals, rows, typed assertions, findings, data quality), mandatory
reconciliation, and the detection rules. The .NET layer serialises these into
the dimension JSON and adds provenance.

**Fifth "correct by luck" finding — the onboarding-artifact detector.**
The natural rule (lifetime closures per instance < 1.0) was validated on one
tenant and generalised badly:

| Tenant | Median ratio | Flagged by ABSOLUTE rule |
|---|---:|---:|
| 1490 | 7.90 | 8% ✓ |
| 1216 | 5.06 | 8% ✓ |
| 1308 | 1.34 | **57%** ✗ |
| 5 | 2.26 | **60%** ✗ |

Baseline closure ratio depends on platform tenure and frequency mix, so an
absolute threshold is meaningless across tenants. **Two-tier peer-relative rule
now implemented:**

- **Tier 1** — tenant median ratio < 1.0 ⇒ the whole tenant is newly onboarded.
  Emit a tenant-level data_quality note and **suppress** per-branch flags.
- **Tier 2** — otherwise flag branches below **20% of the tenant median**.

Validated across 7 tenants:

| Tenant | Median | Tier | Flagged |
|---|---:|---|---:|
| 5 | 0.30 | tenant-level note | 0 |
| 1308 | 0.71 | tenant-level note | 0 |
| 1832 | 2.92 | per-branch | 18.5% |
| 1472 | 5.76 | per-branch | 13.3% |
| 1403 | 4.81 | per-branch | 12.5% |
| 1216 | 5.33 | per-branch | 9.6% |
| 1490 | 9.93 | per-branch | **8.3% — still exactly the known artifact site** |

The known false-star site on the reference tenant remains correctly and solely
identified, while the two newly-onboarded tenants now produce a sensible
tenant-level statement instead of flagging 60% of their sites.

**Sixth finding — zero-obligation entities vanished.** The first implementation
built rows from the instance set, which silently dropped every branch with no
obligations: **4 of 16 active branches** on the reference tenant. Reconciliation
still PASSED, because zeros add nothing to the sum — the defect is invisible
without an explicit branch-count check.

Three of the four were legitimate grouping/holding nodes. The fourth was a
**leaf with no children and no obligations — a ghost entity**, exactly the
coverage blind spot the engine exists to surface, presented as if it did not
exist.

Fixed: rows are built from the branch list (LEFT JOIN instances), with two
distinct flags — `grouping_node` (expected, no finding) and
`no_obligations_configured` (a ghost entity, raised as a **high**-severity
finding with a narrative guard: *"a coverage gap, not a clean record"*).
Control totals now also carry `ActiveBranchesInTenant` and
`BranchesWithNoObligations` so the count integrity is checkable, not implicit.

Verified: all 16 branches now returned — 12 with obligations, 3 grouping nodes,
1 ghost entity.

**Two rules that generalise to every other dimension:**
1. Any threshold-based detector must be **peer-relative** to the tenant's own
   distribution, never an absolute constant.
2. Build dimension rows from the **entity/dimension list**, never from the fact
   set — otherwise empty members disappear, and an empty member is often the
   most important finding.

### ★★ Seventh finding — every absolute-threshold detector was broken

Validating the Location dimension across 8 tenants (not just the one it was
designed against) showed that **all four detectors** produced sane rates on
their design tenant and absurd rates elsewhere:

| Detector | Best tenant | Worst tenant |
|---|---:|---:|
| onboarding_artifact | 8% | **60%** |
| ghost_entity | 1.3% | **66%** |
| single_point_of_failure | 4.6% | **84%** |
| high_ownerless | 1.2% | **76%** |

A finding that fires on 84% of a tenant's locations is not a finding — it is a
description of how that tenant operates. Tenant 1308 would have received
**247 individual findings**.

**This was one systemic error, not four bugs**, so it is fixed once, as a shared
**detector emission policy** (`#detector` table in the Location proc) that every
dimension must reuse:

```
flagged_pct >  20%  →  ONE aggregate finding (medium)
                       "N of M locations (X%) show <pattern>"
                       individual findings SUPPRESSED
flagged_pct <= 20%  →  individual findings, capped at TOP 5 by materiality
```

Result — findings per detector, all 8 tenants:

| Tenant | Raw flags (ghost/SPOF/ownerless) | Findings emitted |
|---|---|---|
| 1308 | 181 / 56 / 10 | **1 / 1 / 5** |
| 29 | 117 / 32 / 29 | **1 / 1 / 1** |
| 5 | 147 / 61 / 48 | **1 / 1 / 1** |
| 1472 | 91 / 28 / 7 | 5 / 5 / 5 |
| 1403 | 12 / 13 / 42 | 5 / 5 / 1 |
| 1490 | 1 / 1 / 2 | 1 / 1 / 2 |

Tenant 1308 goes from 247 findings to 7. The policy makes findings
**scale-invariant**: the same code yields 1 finding for a 16-branch tenant and
1 aggregate for an 819-branch tenant, never 117 items.

**MANDATORY for all remaining dimensions.** Do not write a detector that emits
one finding per flagged member without routing it through this policy.

### Structural validation across 8 tenants — all pass

| Check | Result |
|---|---|
| `rows_from_tree` = `active_branches` | **8/8** — orphan fix holds |
| `sum_row_instances` = `control_total` | **8/8** — reconciliation holds |

### Eighth finding — boundary conditions (found by targeted edge-case search)

Once the detector policy made explosion structurally impossible, random tenant
sampling stopped paying. **Targeted boundary search** did:

**A. The peer-baseline sample can be EMPTY.** The natural sample is "branches
with >= 50 instances". Production tenant 522 has **446 branches and 2,685
instances with a largest branch of 34** — the sample was empty,
`PERCENTILE_CONT` returned NULL, `ISNULL` forced 0, and the tenant was declared
"newly onboarded". The verdict was right (true average ratio 0.11) but reached
from an empty sample, not from measurement. **Hundreds of small but HEALTHY
branches would be misclassified identically.** Tenant 29 hits the same path.

Fixed with an adaptive sample: prefer branches >= 50; if none qualify, fall back
to all branches with obligations, and emit a `degraded_peer_sample` data_quality
note so the narrative states peer comparisons with less confidence. Tenant 522
now reports a measured median of 0.087 instead of a fabricated 0.

**B. Comparatives are vacuous on single-member tenants.** Production has **many
single-branch tenants** (2480, 1807, 2459, 2629 … each with 1,000–2,400
instances). `RANK()` yields "rank 1 of 1", and the report would tell a customer
their only site is "the worst location". Comparatives are now suppressed below 2
rankable members.

**C. Zero-obligation tenants** cannot be assessed at all; `@hasAnyObligations`
now guards the onboarding verdict rather than inferring one from no data.

**The generalisable rule:** once a failure mode is closed *structurally*, stop
sampling and start hunting boundaries — empty samples, single members, zero
denominators. Random tenants will not find them.

### Structural risk checks — all clear

Four risks that tenant sampling would never surface were checked across the
whole population:

| Risk | Result |
|---|---|
| Hierarchy depth vs recursion limit | Max **8** levels; nothing beyond 10 |
| Circular parent references | **None** — traversal terminates naturally |
| `Act.ComplianceCategoryId` NULL | **0** — the scope category join never silently drops rows |
| Compliances with no Act | **0** — the category join is total |

### A false alarm worth recording

A check for "cross-tenant scope rows" reported 43,457 rows across 56 users. It
was **wrong** — the query assumed `UserCustomerMapping` is the authoritative
user↔tenant link. It is not: many users legitimately have zero rows there
(confirmed by email domain and by each referencing exactly one customer).

The correct test — users whose EA rows span multiple customers — found **53 users
spanning exactly 2 tenants**, almost all one corporate group. Legitimate, and the
scope service is safe because it filters on the `@CustomerID` parameter.

Two guardrails come out of this:
- **`UserCustomerMapping` is not a user↔tenant mapping.** Use it only for
  product/recipient mapping.
- **Never derive the tenant from EA rows.** Always pass `@CustomerID` in.

### Multi-tenant users (spec §5.6)

- Tenant is an explicit parameter; **the server must re-derive the eligible tenant
  set on every request** and reject anything outside it (IDOR risk).
- Eligible = `Customer.IsDeleted = 0` **AND** RegInsights mapped (`IsActive = 0`)
  **AND** the user has EA scope rows for that customer.
- Picker only when >1 eligible tenant; skip it entirely for one.
- **Switching tenants must fully re-resolve scope** — cached scope pairs carried
  across a switch are a cross-tenant leak.
- One report = one tenant. Always.

---

## Guardrails for this phase

1. **No LLM code.** Phase 1a is entirely deterministic. If you find yourself
   adding an agent, you are in Phase 1d.
2. **Tests before use.** The golden suite exists so a dictionary edit cannot
   silently move a number. Wire it into CI before writing dimension procedures.
3. **Fail closed, loudly.** Unknown enum ⇒ THROW. Empty scope ⇒ DENY.
   Never default, never guess.
4. **Reconcile everything.** Any decomposition must sum to its total; any
   per-dimension sum must tie to the tenant control total.
5. **When the schema surprises you, believe the data, not the name.** Every
   trap in §6.10 was found by querying, not by reading a column name.
6. **Never derive the tenant from user data.** `@CustomerID` is always an input,
   always re-validated server-side against the user's eligible tenant set.
7. **Validate on tenants with DIFFERENT profiles, never one.** Four separate
   defects in this phase were "correct by luck" on the first tenant checked.

---

## Definition of done

- [x] Classification dictionary written + seeded (all 23 statuses)
- [x] Golden regression written + validated on 5 tenants
- [x] Scope resolution SQL written + validated
- [x] Entity hierarchy SQL written + validated
- [x] Entitlement gate SQL written
- [ ] .NET service wrappers around the SQL objects
- [ ] All components unit-tested in CI
- [ ] `usp_Insights_GoldenInvariants` passes on ≥5 production tenants
- [ ] CI fixture database (F-1 … F-10) built and green
- [ ] No `NOT IN (...)` status literal anywhere in the codebase
- [ ] No name-based status filtering anywhere in the codebase
- [ ] Scope service rejects out-of-scope categories, not just branches

**Next:** Phase 1b (dimension stored procedures, starting with Location — the
worked exemplar in §7.3), then Phase 1c (free tier, which ships first and
exercises this whole foundation with minimal LLM surface).
