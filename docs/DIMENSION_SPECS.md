# Dimension Specifications

Contracts for the eight dimensions beyond Location. **Location
(`sql/05_dimension_location.sql`) is the reference implementation** — read it
before building any of these. Every dimension follows its shape.

---

## Common contract

Every dimension proc takes `(@UserID, @CustomerID, @AsOf)` and emits six result
sets, which the .NET layer serialises into the JSON of design-spec §7.2:

| # | Result set | Notes |
|---|---|---|
| 1 | `control_totals` | **THROWs** if sums don't tie |
| 2 | `rows` | one per dimension member — built from the **member list**, not the fact set |
| 3 | `detector_policy` | flag counts + `EmitMode` per detector |
| 4 | `assertions` | typed facts **with computed comparatives** |
| 5 | `findings` | each backed by `assertion_ids` |
| 6 | `data_quality` | declared gaps |

**Mandatory in every dimension:**
- Pre-flight: scope check (THROW on empty) + `usp_Insights_AssertStatusCoverage`
- Scope constrained on **both** axes via `tvfInsightsScopedInstances`
- Overdue via `tvfInsightsOverdueSchedules` — never a status literal
- Reconciliation: `SUM(rows.Instances)` = scoped total, else THROW
- Every detector routed through the **emission policy** (`#detector`)
- Peer-relative thresholds only; handle empty sample / single member / zero rows
- Declare `#rows` explicitly with all columns (no `SELECT INTO` + `ALTER`)

---

## 1. Entity

**Grain:** one row per node in the entity tree.
**Source:** `tvfInsightsEntityTree` (apex-or-orphan anchored), `ComplianceInstance`.

**Columns:** `BranchID, BranchName, ParentID, ApexId, ApexName, RootKind
(apex|orphan), NodeType (leaf|intermediate), Depth, DirectInstances,
SubtreeInstances, SubtreeOverdue, SubtreeOwnerless, SubtreeImprisonment,
ActiveChildren`

**Extra outputs:**
- `tenant_shape` = apex count → `single_entity` | `multi_entity`
- `comparison_grain` = `locations` | `apex` | `descend_one_level`, with the reason
  (dominance >70%, or a childless holding shell)

**Detections:** `orphaned_parent_deleted`, `instances_on_intermediate_node`,
`dominant_apex` (>70% of estate), `childless_holding_shell`.

**Assertions:** subtree overdue% per apex with `rank`/`of`/`vs_tenant_avg_pp`;
apex share of estate.

> **[TRAP]** Count at **every** node, then reconcile subtree sums to the tenant
> control total. Leaf-only rollup lost 211 instances on one tenant and 141 on
> another. Anchor on apex **OR orphan** — apex-only lost 89% of tenant 29's estate.

> **[TRAP]** Do not compare at apex level when one apex dominates. Reference tenant:
> holding apex held 96.65% → descend one level to the division split.

---

## 2. Nature

**Grain:** one row per `NatureOfCompliance` value.
**Source:** `Compliance.NatureOfCompliance` (tinyint) → `NatureOfCompliance` master.

Known values: 0=Returns, 1=Payments, 3=Report/Intimation, 5=Inspection, 6=Meeting,
7=Registers & Records, 8=Certificate & License, 13=Examination & Testing,
15=Notice/Correspondence, 16=Safety & Welfare, 17=**Others**, 20=Audit,
21=Appointment, 22=Committees, 24=Display/Publishing.

**Columns:** `NatureId, NatureName, Instances, Overdue, OverduePct, Ownerless,
ImprisonmentInstances, ImprisonmentOverdue, CriticalInstances,
BranchesCovered, PenaltyBearingInstances`

**Second output — nature × penalty-type cross-tab.** Each location's risk *type*
differs (financial vs personal-liability vs inspector-visible); this is what makes
the Nature dimension actionable rather than descriptive.

**Detections:** `worst_nature_by_overdue` (peer-relative), `imprisonment_lineage`
(natures where imprisonment share is materially above tenant average),
`uncategorised_gap`.

**Known pattern:** Registers & Records tends to carry both the highest overdue
*and* an imprisonment lineage; Report/Intimation is usually near-perfect.

> **[TRAP] Declare the "Others" gap.** ~49% of the reference tenant's compliances
> are tagged Others (17). The dimension is **half-blind** until BA reclassification
> lands. A `data_quality` entry is **mandatory** — never present a nature chart
> whose largest segment is a meaningless bucket without it.

---

## 3. Users

**Grain:** one row per user with assignments.
**Source:** `ComplianceAssignment` (RoleID 3 = performer, 4 = reviewer),
`[User]`, `UserLoginTrack` (login events, keyed by **Email**).

### Facet group A — workload (three lenses)
`by_volume`, `by_overdue`, `by_imprisonment`.

> **[TRAP] Concentration must be a DISTINCT-INSTANCE UNION.** Summing per-user
> counts double-counts paired performer/reviewer instances and produced an
> impossible **155%**. Correct form gave top-10 = 250/320 = 78.1%.

### Facet group B — behaviour (two lenses, NOT one)

**B1 — Engagement spectrum** (from `UserLoginTrack`, 12-month window):
power (100+), frequent (26–100), moderate (6–25), seldom (1–5), never.

**B2 — Compliance quality:** each user's **performer on-time completion rate on
their own assigned work**.

> **[TRAP] DO NOT build a single "login = quality" ranking.** The data refutes the
> intuitive hypothesis. Measured on the reference tenant:
>
> | Band | Users | Instances | Overdue % |
> |---|---:|---:|---:|
> | Power (100+) | 8 | 3,533 | 21.7 |
> | Frequent | 15 | 3,866 | 26.4 |
> | Moderate | 16 | 1,614 | 17.0 |
> | Never | 7 | 366 | **0.3** |
>
> Never-login users had the **lowest** overdue — because they are nominal
> *reviewers* on pipelines an active performer keeps current. Shipping the naive
> version would tell a CCO that absent users are their best compliers.
>
> Login frequency measures **engagement/adoption**, not quality. Any assertion
> citing that figure **must** carry `caveat: "confounded_by_role_mix"`.

**The valuable synthesis is a 2×2 overlay:** engaged+quality (champions),
engaged+slipping (needs support), disengaged+current (**dependency risk** — someone
else is carrying them), disengaged+slipping (the real problem). Login alone cannot
find the third cell.

**Also carry:** deactivated users (`IsActive = 0`, `IsDeleted = 0`) still holding
live assignments — **keep and flag, never hide**. One inactive user held 310 review
instances on a dead-end pipeline.

**Detections:** `never_logged_in_holding_assignments`, `deactivated_holding_work`,
`overloaded_performer`, `single_reviewer_dependency`.

---

## 4. Departments

**Grain:** one row per department.
**Source:** `ComplianceInstance.DepartmentID` → `Department(ID, Name, IsDeleted,
CustomerID)`. Filter `Department.IsDeleted = 0`.

**Columns:** `DepartmentID, DepartmentName, Instances, Overdue, OverduePct,
Ownerless, ImprisonmentInstances, CriticalInstances, DistinctUsers, BranchesCovered`

**Detections:** `worst_department` (peer-relative), `high_ownerless`,
`single_user_department`, `unassigned_department` (instances with NULL
`DepartmentID` — a configuration gap, declare it).

> **[TRAP]** `DepartmentID` is on the **instance**, not the assignment. Build rows
> from the department list so departments with zero instances still appear.

---

## 5. Risk

**Grain:** one row per risk level.
**Mapping (LOCKED):** `3 = Critical, 0 = High, 1 = Medium, 2 = Low`.

**Columns:** `RiskType, RiskLabel, Instances, Overdue, OverduePct, Ownerless,
ImprisonmentInstances, ImprisonmentOverdue, BranchesCovered`

**Reference-tenant pattern (expect this shape):**

| Risk | Instances | Overdue % | Imprisonment | Ownerless |
|---|---:|---:|---:|---:|
| Critical (3) | 2,221 | 20.8 | 1,419 | 9 |
| High (0) | 1,132 | 29.7 | 3 | 31 |
| Medium (1) | 762 | 23.6 | 0 | 43 |
| Low (2) | 677 | 19.9 | 2 | 10 |

Two counter-intuitive findings the narrative must be able to express:
1. **Critical items are the BEST-managed** (20.8% < the ~29% tenant average) — the
   org triages correctly. Do not narrate Critical volume as a failure.
2. **The coverage gap hides in the MIDDLE tiers** — High 31 + Medium 43 ownerless
   vs Critical's 9.

> **[TRAP] Critical and imprisonment are ~95% the same population** (1,419 of
> 1,424). They are **not** independent axes. Do not present them as two separate
> findings — monetary exposure is the axis that genuinely diverges.

---

## 6. Act

**Grain:** one row per Act (optionally Act × State).
**Source:** `Act(ID, Name, State, StateID, RegulatorID, ComplianceCategoryId,
Act_DeptID, MinistryID)` ← `Compliance.ActID`.

**Columns:** `ActID, ActName, State, RegulatorID, CategoryId, Instances, Overdue,
OverduePct, ImprisonmentInstances, BranchesCovered`

**The headline pattern — Act × geography.** The *same* Act performs wildly
differently by state: Factories Act ran **11.7% overdue in one state vs 60.6% in
another**, across 951 instances in 3 states. This localises accountability in a way
a national Act-level view cannot. **Always cut Act by State when `Act.State` varies.**

Secondary patterns: emerging laws (DPDP, POSH, Apprentices) show adoption lag with
higher overdue; hard-money Acts (Income Tax, Companies, Gratuity) run near-spotless;
imprisonment lineage concentrates in labour-welfare and factory-safety Acts.

**Detections:** `act_state_divergence` (same Act, materially different overdue
across states), `emerging_law_adoption_lag`, `regulator_concentration`.

---

## 7. Statutory vs Internal

> **[LOCKED]** A **full dimension** — internal is a first-class population, not a
> comparison block.

**Source — complete parallel schema:** `InternalComplianceInstance`,
`InternalComplianceScheduledOn`, `InternalComplianceTransaction`,
`InternalComplianceAssignment`, `InternalCompliancesCategory`. Same conventions
(`IsActive`, `IsUpcomingNotDeleted`, `StatusId`, assignment by `RoleID`).

**Emit two populations side by side, plus per-branch and per-user cuts for each.**

Reference-tenant contrast:

| Metric | Statutory | Internal |
|---|---:|---:|
| Instances | 4,792 | 240 |
| Branches covered | 12 | **5** |
| Coverage (has performer) | 98.1% | **81.3%** |
| Ownerless | 1.9% | **18.8%** |
| Users | 46 | 16 |

**The finding is structural, not quantitative:** all internal instances sat on 5
branches of **one division**. The *other* division — higher overdue, most of the
monetary exposure — had **zero internal compliance configured**. That is a
governance-maturity signal invisible in any statutory-only report.

**Detections:** `internal_coverage_gap` (entities/divisions with statutory but no
internal), `internal_ownerless_rate` (vs statutory rate), `internal_absent_entirely`.

> **[TRAP]** Internal statuses may not use the same dictionary as statutory.
> **Verify before reusing** `vInsightsStatusCurrent` against `InternalComplianceTransaction`.
> If they differ, the dictionary needs a second facet — do not assume.

---

## 8. Task / Event

**Grain:** one row per event type, plus a per-branch coverage cut.
**Source:** `EventInstance(ID, EventID, StartDate, CustomerBranchID, IsDeleted)`,
`Event`, `EventComplianceMaster`, `EventScheduleOn`, `EventAssignment`, `EventCategory`.

**Columns:** `EventID, EventName, InstanceCount, BranchesCovered, EarliestStart,
LatestStart, InstancesSinceCutoff`

**Frame as compliance-MODE coverage** — periodic / event-triggered / internal —
answering *which modes are actually live*.

> **[TRAP] Configured ≠ operational.** On the reference tenant, nearly every event
> instance carried a single bulk-configuration date (~8 per event type ≈ one per
> branch) with **nothing logged in over a year**. That is **template scaffolding**,
> not live tracking.
>
> For a manufacturer with physical risk, a dormant event module means injury/death
> filing deadlines are unmanaged and invisible. Detection: `latest_start` far in the
> past + instance count ≈ event types × branches + near-zero recent activity.

**Detections:** `event_module_dormant` (tenant-level), `event_types_never_triggered`,
`branches_without_event_coverage`.

> **Verify before asserting dormancy to a customer** — events may be tracked
> off-system. Emit as a question, not an accusation (open item O-8).

---

## Build order for dimensions

1. **Entity** — shares tree logic with Location; validates the rollup independently
2. **Risk** — simplest grain, exercises the locked enum mapping
3. **Nature** — introduces the cross-tab and the "Others" data-quality path
4. **Departments** — simple, but exercises the NULL-member path
5. **Act** — introduces the Act × State second axis
6. **Users** — most complex (five facets); build last of the core six
7. **Statutory vs Internal** — needs the internal-status dictionary question resolved
8. **Task / Event** — separate subsystem; least coupled

Each must pass the golden regression on ≥5 tenants with different profiles, and be
boundary-tested (single member, zero members, empty peer sample) before the next.
