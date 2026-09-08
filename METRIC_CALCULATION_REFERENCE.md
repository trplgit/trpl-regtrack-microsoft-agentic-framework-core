# Metric Calculation Reference

**For:** developers implementing the .NET layer, and QA validating output.
**Scope:** every data point produced by the nine dimension procedures — what it
means, how it is derived, why it is defined that way, and how to test it.

> Read §1 before anything else. Almost every field depends on the three
> foundational definitions there, and most misunderstandings come from assuming
> a different one.

---

## 1. Foundations — three definitions everything rests on

### 1.1 "The estate" — which instances count

Every dimension counts the same population. Getting this wrong is how two
components end up reporting different totals while each looks internally correct.

```sql
FROM ComplianceInstance i
JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID
JOIN Compliance     c  ON c.ID = i.ComplianceID
WHERE cb.CustomerID = @CustomerID
  AND cb.IsDeleted  = 0     -- branch not deleted
  AND cb.Status     = 1     -- branch OPERATING (see below)
  AND i.IsDeleted   = 0     -- instance is active
  AND c.IsDeleted   = 0     -- MASTER compliance is active
```

**`CustomerBranch.Status` is a SECOND active flag, separate from `IsDeleted`.**
BA ruling: `Status = 0` means the location is **deactivated**. Its obligations
remain tagged to it, but they are **not reported to users**, and no schedules,
notifications, alerts or escalations are generated for them.

So they are not live obligations, and an "overdue" item on a deactivated
location **is not overdue** - nobody is being asked to do it. Every metric must
exclude them.

> Measured impact on one test tenant: branches 200 -> 177, estate 4,814 -> 3,737,
> and **overdue 30,738 -> 22,117 - 28% of the reported overdue was on deactivated
> locations.** That is a very large phantom number to put in front of a CCO.

> **Report the count as data quality, not as silence:** "23 deactivated locations
> still carry 1,077 obligations" is a useful configuration-drift finding.

**Why the third filter matters.** An instance can point at a soft-deleted
`Compliance` master — an obligation whose definition was retired. It is not a
live obligation. Measured on a real tenant: **70 such instances**. Omitting the
filter gave a control total of 4,884 where every dimension reported 4,814.

> **Test:** run the query with and without `c.IsDeleted = 0`. The difference is
> the retired-master count. Both numbers must never appear in the same report.

### 1.2 Scope — what this user may see

Scope is **two-dimensional**: `(BranchID, ComplianceCategoryID)` pairs from
`EntitiesAssignment`. Every dimension constrains on **both** axes.

```sql
JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp
     ON sp.BranchID   = i.CustomerBranchID
    AND sp.CategoryId = a.ComplianceCategoryId      -- from Act, see below
```

The category of an instance comes **only** via `Act`:
`ComplianceInstance -> Compliance -> Act.ComplianceCategoryId`.
`Compliance`, `ComplianceInstance` and `ComplianceSubType` have no category column.

**Why both axes.** Branch-only filtering would show an EHS manager the Labour and
Secretarial data they are not authorised for — a leak *inside* a single tenant.
Measured: branch-only scoping would expose up to **119,797 extra instances** on
one tenant, and **31,659 to a single user** on another.

> **Test:** take a user with `(branch B, category C)` only. Query for
> `(B, any other category)`. Must return zero rows.

### 1.3 Overdue — the dictionary decides, never a literal

```sql
overdue  <=>  ScheduleOn <= @AsOf
              AND cso.IsActive = 1
              AND cso.IsUpcomingNotDeleted = 1
              AND (  status.OverdueEligible = 1          -- (a) from the dictionary
                  OR LatestTransactionId IS NULL )       -- (b) never touched - BA ruling
```

**Two sources of overdue.** (a) is dictionary-driven. (b) is the BA ruling of
4 Sep 2026: *"a past-due schedule with no transaction is to be considered
overdue."* Nobody has ever touched it - the purest form. `NeverTouched` on the
overdue function distinguishes them. Unknown statuses are still **excluded**
(neither a nor b), so a dictionary gap still surfaces through reconciliation.

Open (overdue-eligible) statuses: `1,2,3,6,8,10,11,12,13,14,16,18,19,20,21,22,23`.

**Never write `status NOT IN (4,5,15,18)`.** That older form was wrong twice over:
it counted **completed** items (7, 9) as overdue, and it defaulted any *unknown*
status to overdue. Measured error on real tenants: **overstated by up to 1,886**
and **understated by up to 1,571**, depending on the tenant's status mix.

**Latest status is resolved by `tvfInsightsLatestStatus`, never by joining the
view.** `RecentComplianceTransactionView` is non-indexed over 45.7M rows and the
tenant filter is not pushed through it; the function filters to the tenant's
schedules first, then seeks the latest transaction per schedule. Production:
> 4 minutes via the view, **611 ms** via the function, identical results.

> **[FOUND] Schedules with no transaction at all.** The view's inner join hid
> them; the function surfaces them with `LatestTransactionId = NULL`. On the
> reference tenant all 22 shared a single 2023 timestamp - a bulk-creation
> artifact. **BA ruling: they count as overdue.** Declared separately in
> `StatusDataQuality.SchedulesWithNoTransaction` so the count stays visible.

The join to the dictionary is an **INNER** join deliberately: an unmapped status
produces no row, so reconciliation fails loudly instead of silently mis-bucketing.

> **Test:** `overdue_new = overdue_old - pastdue(7,9) - pastdue(17) + pastdue(18)`
> must hold exactly, on several tenants. It is an algebraic identity.

---

### 1.4 Product / category - and one flag to ignore

Category resolves **only** via `ComplianceInstance -> Compliance -> Act.ComplianceCategoryId`
(2 = Labour, 15 = EHS, 5 = Finance & Taxation, 20 = Secretarial, ...).

> **[TRAP] `ComplianceInstance.IsAvantis` is obsolete - ignore it.** It is set on
> **97.6%** of active instances, so it separates nothing, and 1.9M of those are
> not Labour. The canonical view `vw_ci_ActiveInstance` documents
> `IsAvantis -> Labour`; that mapping is stale and will mislead anyone who
> trusts it. A peer-gap figure of 613 in earlier analysis was produced with it
> and does not reproduce under a correct category join - the true figure on the
> same tenant is 380.

---

## 2. Core metrics — used across dimensions

| Field | Derivation | Notes / traps |
|---|---|---|
| `Instances` | Count of estate rows belonging to the member | Grain differs per dimension (branch, act, user...) |
| `Overdue` | Distinct **instances** with >=1 overdue schedule | Instance-level, not schedule-level. An instance with 5 overdue schedules counts **once** |
| `OverduePct` | `100.0 * Overdue / Instances` | Guard `Instances = 0` |
| `Ownerless` | Instances with **no** `ComplianceAssignment` where `RoleID = 3` and `UserID > 0` | RoleID 3 = performer, 4 = reviewer. Ownerless means no *performer* |
| `ImprisonmentInstances` | `Compliance.Imprisonment = 1` | The personal-liability lens |
| `ImprisonmentOverdue` | Both conditions | The sharpest single number in the product |
| `CriticalInstances` | `Compliance.RiskType = 3` | **3 = Critical, 0 = High, 1 = Medium, 2 = Low** |
| `BranchesCovered` | Distinct active branches the member appears on | Spread, not volume |
| `ClosureEventsLifetime` | Count of `ComplianceTransaction` rows whose status has `ClosureClass = 'completed'` | **Events, not instances** — an instance closed monthly for 3 years contributes ~36 |
| `ClosureRatio` | `ClosureEventsLifetime / Instances` | Proxy for "has this member actually been operating?" |

### Why overdue is instance-level, not schedule-level

A CCO asks "how many obligations are behind?", not "how many occurrences". A
monthly obligation unattended for a year is **one** problem, not twelve.
Schedule-level counting would make long-neglected recurring items dominate every
report and drown out breadth.

> **Test:** for a member, `Overdue <= Instances` always. If a dimension ever
> reports otherwise it is counting schedules.

---

## 3. Derived comparatives — computed in SQL, never phrased by the LLM

These exist so the narrative agent can say "worst", "above average", "half of"
**only** when a field supports it (design spec §6.10).

| Field | Derivation |
|---|---|
| `OverdueRank` | `RANK() OVER (ORDER BY OverduePct DESC)` over members **with obligations** |
| `VsTenantPP` / `VsComparatorPP` | `member_pct - tenant_pct`, in percentage **points** |
| `Direction` | `worse` / `better`, from the sign of the delta **and** the metric's polarity |
| `ApexSharePct` | `100.0 * SubtreeInstances / tenant_total` |
| `OfN` | Population size the rank is drawn from |

> **Trap — polarity.** "Higher" is not always "worse". For `OverduePct` higher is
> worse; for `OnTimePct` higher is better. `Direction` must be set from the
> metric, not the arithmetic sign.

> **Trap — vacuous comparatives.** Ranks are suppressed below **2** rankable
> members. Production has many single-branch tenants; "rank 1 of 1" would tell a
> customer their only site is their worst.

---

## 4. Per-dimension fields

### 4.1 Location (`05`) — grain: one row per active branch

`BranchID, BranchName, NodeType, RootKind, ApexName, Instances, Overdue,
Ownerless, ImprisonmentInstances, ImprisonmentOverdue, CriticalInstances,
DistinctPerformers, DistinctReviewers, ClosureEventsLifetime, ActiveChildren,
OverduePct, OwnerlessPct, ClosureRatio, OverdueRank, Flags`

- `NodeType` — `leaf` | `intermediate` (has active children). **Intermediate
  nodes can hold instances directly** — 211 on one tenant. A leaf-only rollup
  silently drops them.
- `RootKind` — `apex` (`ParentID IS NULL`) | `orphan` (active branch whose parent
  is soft-deleted). See §4.2.
- `DistinctPerformers` / `DistinctReviewers` — distinct `UserID` by `RoleID` on
  that branch. Either `<= 1` flags a single point of failure.

**Rows are built from the branch list, not the instance set.** Otherwise branches
with zero obligations vanish — and one of those is a genuine finding (§5.2).

### 4.2 Entity (`07`) — grain: one row per node in the hierarchy

`BranchID, ParentID, ApexId, ApexName, RootKind, NodeType, Depth,
DirectInstances, SubtreeInstances, SubtreeOverdue, SubtreeOwnerless,
SubtreeImprisonment, ActiveChildren, SubtreeOverduePct, ApexSharePct,
SubtreeOverdueRank`

- `DirectInstances` — held **at** this node. `SubtreeInstances` — this node plus
  all descendants.
- **Recursion anchors on apex OR orphan.** Anchoring only on `ParentID IS NULL`
  cannot traverse *through* a soft-deleted parent, so the whole subtree beneath it
  disappears. Measured across all tenants: **114 affected, 8 losing data, 3 would
  have received a report showing ZERO obligations.**
- `ComparisonGrain` (control totals) — `apex` | `descend_one_level` | `locations`.
  Descends when one apex holds >70% of the estate or is a childless holding shell,
  because comparing a 96%-dominant holding node against its sibling says nothing.

> **Test:** sum of `DirectInstances` across all nodes must equal the estate total.
> A gap means a node was dropped — almost always an instance-bearing intermediate
> or an orphaned subtree.

### 4.3 Risk (`08`) — grain: one row per risk level

`RiskType, RiskLabel, Instances, Overdue, OverduePct, Ownerless,
ImprisonmentInstances, ImprisonmentOverdue, BranchesCovered, VsTenantPP`

**Mapping is `3=Critical, 0=High, 1=Medium, 2=Low`** — not 1..4, and not ordinal.
Corroborated in production: 98.7% of imprisonment-bearing instances carry
`RiskType 3`, across 528 tenants.

> **Expect the counter-intuitive result:** Critical items are usually the
> *best*-managed (lower overdue than the tenant average) because organisations
> triage correctly. Do not treat that as a bug.

> **Do not present Critical and imprisonment as independent findings** — they
> overlap ~95%. Monetary exposure is the axis that genuinely diverges.

### 4.4 Nature (`09`) — grain: one row per `NatureOfCompliance`

`NatureId, NatureName, IsRetired, Instances, Overdue, OverduePct, Ownerless,
ImprisonmentInstances, ImprisonmentOverdue, CriticalInstances, BranchesCovered,
PenaltyBearingInstances, FinancialPenaltyInstances, ClosureRiskInstances,
ImprisonmentSharePct, OverdueRank`

- `FinancialPenaltyInstances` — has any of `VariableAmountPerDay/PerMonth/
  PerInstance/Percent`.
- `ClosureRiskInstances` — `IsForcefulClosure = 1` (plant shutdown risk).
  **Under-populated in production** — treat 0 as a data gap, not as "no risk".
- `CategorisedInstances` + `UntaggedInstances` = estate. Rows cover only
  instances **with** a nature (§5.1).

> **Mandatory data-quality declaration:** the "Others" bucket plus untagged
> instances reached **38%** on one tenant and ~49% on another. Never present a
> nature chart whose largest segment is a meaningless bucket without saying so.

### 4.5 Departments (`10`) — grain: one row per department

`DepartmentID, DepartmentName, Instances, Overdue, OverduePct, Ownerless,
OwnerlessPct, ImprisonmentInstances, CriticalInstances, DistinctUsers,
BranchesCovered, OverdueRank`

`DepartmentID` lives on **`ComplianceInstance`**, not on the assignment.
`AssignedInstances` + `UnassignedInstances` = estate. On one tenant **82%** were
unassigned — a real configuration gap, declared rather than hidden.

### 4.6 Act (`11`) — grain: one row per Act (optionally Act x State)

`ActID, ActName, State, RegulatorID, CategoryId, Instances, Overdue, OverduePct,
ImprisonmentInstances, BranchesCovered, StartDate, OverdueRank`

**Always cut by State when `Act.State` varies.** The headline pattern: the *same*
Act ran **11.7% overdue in one state and 60.6% in another**, across 951 instances.
A national Act-level view hides this entirely; Act x geography localises
accountability.

`UnlinkedInstances` (control totals) — instances whose Act cannot be resolved.
Must be zero; non-zero means the category join is also broken.

### 4.7 Users (`12`) — grain: one row per user with assignments

`UserID, UserName, IsActive, Instances, PerformerInstances, ReviewerInstances,
Overdue, OverduePct, ImprisonmentInstances, BranchesCovered, EngagementBand,
CompletedEvents, OnTimeEvents, OnTimePct, QuadrantOverlay`

**Three counts that must not be conflated:**

| Field | Meaning |
|---|---|
| `AssignedInstancesDistinct` | Distinct instances with any assignee — **<= estate** |
| `SumOfPerUserInstances` | Sum across users — **deliberately > estate**, since an instance has both a performer and a reviewer |
| `PerformerInstances` / `ReviewerInstances` | Split by role |

> **Trap:** summing per-user counts to measure concentration double-counts paired
> instances and produced an impossible **155%**. Concentration must be a
> **distinct-instance union**.

- `EngagementBand` — from `UserLoginTrack` (keyed by **Email**), 12-month window:
  power 100+, frequent 26-100, moderate 6-25, seldom 1-5, never 0.
- `OnTimePct` — the user's **own performer** work: `OnTimeEvents / CompletedEvents`.
- `QuadrantOverlay` — engagement x quality 2x2. Users with **no** completed work
  are left **unclassified**, never assumed.

> **The single most important trap in this dimension.** Never-login users showed
> the *lowest* overdue (0.3%). The inference "absent users are the best
> compliers" is **false** — they are nominal reviewers on pipelines active
> performers keep current. Login frequency measures **engagement**, not quality.
> Any assertion citing it must carry `caveat: confounded_by_role_mix`.

- Deactivated users (`IsActive = 0`, `IsDeleted = 0`) holding live work are
  **kept and flagged**, never filtered out. That is the finding.

### 4.8 Statutory vs Internal (`13`) — grain: one row per branch, both populations

`BranchID, BranchName, ApexName, StatutoryInstances, StatutoryOverdue,
StatutoryOwnerless, InternalInstances, InternalOverdue, InternalOwnerless,
StatutoryOwnerlessPct, InternalOwnerlessPct`

Internal has a full parallel schema (`InternalComplianceInstance`,
`...ScheduledOn`, `...Transaction`, `...Assignment`).

**The finding is structural, not quantitative:** on the reference tenant all
internal compliance sat on 5 branches of **one division** — the other division,
with higher overdue and most of the monetary exposure, had **none configured**.

> **Open item:** verify whether `InternalComplianceTransaction` uses the same
> status codes as the statutory dictionary. Do not assume. `InternalUnmappedStatusRows`
> in control totals exists to detect this.

### 4.9 Task / Event (`14`) — grain: one row per event type

`EventID, EventName, InstanceCount, BranchesCovered, EarliestStart, LatestStart,
InstancesSinceCutoff, DistinctStartDates`

Separate population (`EventInstance`), so its `ScopedInstances` is **not** the
compliance estate. Scoped by **branch only** — `EventInstance` has no category, so
there is no second axis.

**Dormancy detection** uses `DistinctStartDates` and `InstancesSinceCutoff`.
Template scaffolding looks like: one bulk-configuration date, instance count ~=
event types x branches, near-zero recent activity. That pattern means
injury/death filing deadlines are unmanaged and invisible.

> Verify with the tenant before asserting dormancy — events may be tracked
> off-system.

---

## 5. Cross-cutting rules

### 5.1 The residual rule

Some dimensions have members not every instance belongs to. Rows then cover part
of the estate, which is correct — but must be legible:

| Dimension | Rows field | Residual | Sum |
|---|---|---|---|
| Nature | `CategorisedInstances` | `UntaggedInstances` | = estate |
| Departments | `AssignedInstances` | `UnassignedInstances` | = estate |
| Users | `AssignedInstancesDistinct` | `UnassignedInstances` | = estate |
| Location, Entity, Risk, Act, Internal, Event | `SumOfRows` | none | = estate |

A field called `SumOfRows` that does not equal `ScopedInstances` reads as a bug.

### 5.2 Detections and their thresholds

| Flag | Condition | Why relative |
|---|---|---|
| `onboarding_artifact` | `ClosureRatio < 20%` of the **tenant median**, and `Instances >= 50` | Baseline ratio depends on tenure and frequency mix. An absolute threshold flagged **60%** of one tenant's sites |
| `no_obligations_configured` | Leaf, no children, zero instances | A ghost entity — a configured location tracking nothing |
| `grouping_node` | Zero instances **but** has children | Normal. Not a finding |
| `single_point_of_failure` | `DistinctPerformers <= 1` OR `DistinctReviewers <= 1` | — |
| `high_ownerless` | `OwnerlessPct >= 10%` | — |
| `instances_on_intermediate_node` | `NodeType = intermediate` AND `Instances > 0` | Would vanish from a leaf-only rollup |
| `orphaned_parent_deleted` | `RootKind = orphan` | Declared, never silently re-parented |

**Tenant-level guard:** if the tenant's own median closure ratio is `< 1.0` the
whole tenant is newly onboarded — per-site artifact flags are suppressed and a
tenant-level note is emitted instead.

**Degraded sample:** if no member reaches the 50-instance floor, the peer baseline
falls back to all members with obligations and emits `degraded_peer_sample`.
Never infer a verdict from an empty sample — one tenant had 446 branches whose
largest held 34.

### 5.3 The emission policy — why you rarely see many findings

```
flagged_pct >  20%  ->  ONE aggregate finding, individuals suppressed
flagged_pct <= 20%  ->  individual findings, capped at TOP 5 by materiality
```

A detector firing on 84% of a tenant's locations is not a finding — it is a
description of how that tenant operates. Without this policy one tenant would
have produced **247 findings**; it now produces 7.

> **Test:** no dimension may emit more than 5 findings per detector, on any
> tenant, ever.

---

## 5.4 Performance patterns that are not optional

Two shapes make procedures time out on production-sized tenants:

1. **Joining `RecentComplianceTransactionView`** - non-indexed view over 45.7M
   rows, tenant filter not pushed through. Use `tvfInsightsLatestStatus`.
2. **Joining two inline TVFs** - no cardinality estimate, so the optimizer may
   pick nested loops. On one tenant that was 22,070 x 4,758 rows and the
   procedure never returned. Force `INNER HASH JOIN`, or materialise each side
   into an indexed temp table (377 ms measured).

Both were found by running against production, not by reading the code.

---

## 6. What QA should verify

**Reconciliation (every dimension, every tenant)**
- Rows (plus declared residual) sum to `ScopedInstances`
- `Reconciled = true`, and the procedure `THROW`s when it is not
- Member count equals the active member count (branches, departments, users...)

**Definitions**
- `Overdue <= Instances` always (instance-level, not schedule-level)
- No completed item (`ClosureClass = 'completed'`) ever counted overdue
- Statuses 15 and 17 excluded from the on-time **denominator**
- The overdue identity in §1.3 holds exactly

**Scope**
- A user scoped to `(B, C)` gets zero rows for `(B, other category)`
- Empty scope returns **deny**, not an empty report
- All-branches-but-not-all-categories classifies as `functional`, not `tenant_wide`

**Boundaries** (these break naive implementations)
- Single-member tenant -> comparatives suppressed
- Zero-obligation member -> appears, flagged, not dropped
- No member above the materiality floor -> `degraded_peer_sample`
- Tenant with no internal compliance / no events -> handled, not an error

**Findings**
- Never more than 5 per detector
- Every finding references at least one assertion id
- `narrative_guard` present on onboarding-artifact and ghost-entity findings

> **Do not validate on one tenant.** Fourteen defects were found building this;
> **every one passed on the first tenant checked.** Use tenants with deliberately
> different profiles — see §11 of `CLAUDE.md` for the list.
