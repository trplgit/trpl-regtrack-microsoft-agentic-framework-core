# Paid-Tier Holistic Sample — Calculation Reference

**File:** `samples/paid_tier_holistic_sample.json` (522 KB)
**Schema:** `paid-holistic-1.0`
**Tenant:** `tnt_8f21a` — anonymised multi-location retailer, 632 stores
**Period:** FY2025-26 (previous) vs FY2026-27 (current), Indian FY basis

---

## 0. What this file is, and what it is not

This is a **design artifact**, produced before the SQL engine existed, to answer
*"what does a paid report actually look like?"* Every number in it was queried
from production and reconciled — it is real data, anonymised — but it was
assembled by hand-run SQL and a Python build script, **not** by the procedures in
`sql/`.

**Read it as the target output, not as engine output.** Some blocks map directly
onto the dimension procedures now built; others (the composite health score,
`priority_actions`, `ledger`) have **no SQL implementation yet** and are flagged
below. Anyone testing the engine against this file will find gaps — those gaps are
unbuilt features, not defects.

| Block | Status in the built engine |
|---|---|
| `kpis.coverage` | Covered by Location + Entity dimensions |
| `kpis.by_department` | Covered by Departments dimension |
| `kpis.by_user` | Covered by Users dimension |
| `kpis.risk_weighted` | Covered by Risk dimension |
| `kpis.timeliness`, `backlog` | Derivable from the dictionary metrics |
| `kpis.licence`, `forward_pipeline` | **Not yet built** |
| `kpis.evidence_integrity` | **Not yet built** — see §3.7, it is mostly a data gap |
| `overall_health` composite | **Not yet built** — model is illustrative, §2 |
| `priority_actions` | **Not yet built** — this is the composition agent's job |
| `ledger` | **Not yet built** — belongs with report persistence |

---

## 1. Document envelope

| Field | Meaning |
|---|---|
| `schema_version` | `paid-holistic-1.0`. Bump on any breaking shape change |
| `report_type` | `statutory_compliance_health` |
| `tenant.footprint_stores` | 632 — **leaf** stores, not all `CustomerBranch` rows. Grouping and rollup nodes are excluded |
| `period.basis` | `indian_fy` — 1 April to 31 March |
| `period.range_end` | The **data cut**, not the FY end. Current FY is partial |
| `credits_charged` | 1 — the cost unit shown to the customer |
| `domains_in_scope` | Statutory only. Internal compliance is a separate population |

> **Trap:** `previous_fy` is the last **complete** year and `current_fy` is
> **in progress**. Any comparison between them is complete-vs-partial and must
> say so. This is why `timeliness` carries both `fy_trend` and explicit
> `closures_*` counts rather than only a percentage.

---

## 2. `overall_health` — the composite score

```
score = round( SUM(component.score * component.weight) )
```

| Component | Score | Weight |
|---|---:|---:|
| Risk-weighted exposure | 45 | 0.20 |
| Timeliness | 53 | 0.15 |
| Coverage | 88 | 0.15 |
| Licence | 62 | 0.15 |
| People / continuity | 35 | 0.15 |
| Overdue / backlog health | 42 | 0.10 |
| Evidence integrity | 30 | 0.10 |
| **Composite** | **51** | **1.00** |

Weights are ordered by **legal exposure**, not by data availability: risk highest,
evidence and backlog lowest.

> **This is explicitly an illustrative model.** The `method` string says so, and
> that wording must survive into any customer-facing render. The weights were not
> derived empirically — they are a starting point to tune with a customer.

> **[OPEN] Two problems to resolve before building this in SQL.**
> 1. **Each component score is itself a model.** How "timeliness = 53" is derived
>    from an on-time percentage is not defined anywhere. Every one of the seven
>    needs a documented, testable formula.
> 2. **Naming inconsistency:** the `method` string lists a component called
>    `currency` weighted 0.10, but the `components` array has `overdue_backlog` at
>    0.10 and no `currency`. Same weight, different name — reconcile before this
>    is implemented.

> **A composite score is a computed index, not a raw fact.** Per the
> reconciliation discipline it must be labelled as such wherever it appears. A
> CCO seeing "51/100" will otherwise assume it is measured.

---

## 3. The ten KPI blocks

### 3.1 `timeliness`
`ontime_pct_current_fy`, `ontime_pct_previous_fy`, `yoy_change_pts`, `fy_trend`,
`closures_previous_fy`, `closures_current_fy`, `by_product`, `narrative`

On-time percentage of **completed** work, per FY, anchored on `ScheduleOn`.
Absolute closure counts sit alongside the percentages so a reader can see whether
a rate moved because behaviour changed or because volume did.

> **Trap:** the headline can be stable while the underlying discipline erodes. On
> another tenant on-time barely moved (99.83% -> 99.60%) while delayed closures
> rose 133% and never-closed rose 70%. Always publish the leading indicators
> beside the headline.

### 3.2 `backlog`
`total_overdue`, `overdue_previous_fy`, `overdue_current_fy`, `overdue_older`,
`closure_rate_pct`

Overdue split by the FY the obligation was **due in** — which separates "we are
behind this year" from "we have never dealt with this".

> **Trap:** overdue is a **flow** metric and drifts between runs (observed:
> 1,387 -> 1,398 -> 1,116 on identical SQL in one session, as
> `RecentComplianceTransactionView` refreshed). Stock metrics — total instances,
> critical count, imprisonment count — are stable. Never compare an overdue figure
> from one run against another run.

### 3.3 `risk_weighted`
`overdue_critical`, `critical_share_of_overdue_pct`,
`imprisonment_overdue_obligations`

Critical = `RiskType 3`. Imprisonment = `Compliance.Imprisonment = 1`.

> These two overlap ~95%. Present them as one story with two facets, not as two
> independent findings — doing the latter doubles the apparent problem count.

### 3.4 `coverage` — the largest block (632 store rows)

| Field | Derivation |
|---|---|
| `leaf_stores` | 632 — leaf nodes only |
| `stores_mapped` / `_pct` | Stores with at least one assigned performer |
| `ownerless_obligations` | 517 tenant-wide |
| `ownerless_obligations_leaf_scope` | 347, across 31 leaf stores |
| `peer_coverage_gaps` | Obligations peers have configured and this store does not |
| `under_configured_stores` | Materially fewer obligations than comparable peers |
| `status_counts` | healthy 530 / under_configured 46 / has_ownerless 26 / unmapped 30 |

> **Read `ownerless_note` carefully — it is the model for how to report a
> discrepancy.** The two ownerless figures differ (517 vs 347) because the largest
> single block, 170 obligations, sits on a **corporate rollup node that is not a
> leaf store** and therefore has no grid box. Rather than hide the difference or
> silently pick one number, the block states both and explains the gap. This is
> the same class of issue as the intermediate-node problem the Entity dimension
> now handles structurally.

`status_precedence` exists because a store can be in several states at once; it
fixes which one wins for the grid colour so the visual is deterministic.

### 3.5 `licence`
`corroborated_lapses`, `stores_affected`, `avg_days_overdue`, `expiring_90d`

**"Corroborated"** matters: a licence is only counted as lapsed when more than one
signal agrees. A single stale date is a data-quality problem, not a finding.
`expiring_90d` is the forward-looking hook — the number a CCO can still act on.

### 3.6 `people_continuity`
`performers`, `top3_share_pct`, `reviewer_concentration_pct`

Key-person risk. Concentration is a **distinct-instance union**, never a sum of
per-user counts — summing double-counts paired performer/reviewer instances and
produced an impossible **155%** during design.

### 3.7 `evidence_integrity`
`closures_with_review_trail_pct`, `evidence_in_sql` (boolean)

> **`evidence_in_sql: false` is the important field.** Document evidence lives in
> blob storage, not in SQL, so this block measures only whether a **review trail**
> exists — not whether evidence was actually attached. Scoring it at weight 0.10
> in the composite is therefore scoring a proxy. Say so, or drop the component.

### 3.8 `forward_pipeline`
`due_next_90d`, `predicted_at_risk`

> **`predicted_at_risk` is a projection**, not a measurement. It must be labelled
> as a computed index. No prediction model is specified in the file — if this is
> built, the method needs documenting and testing like any other metric.

### 3.9 `by_department`
`summary`, `rows`, `cross_department_top_performers`, `key_findings`, `caveats`,
`narrative` — superseded by the Departments dimension (`sql/10`).

### 3.10 `by_user` — the richest block

`summary`, `priority_users_lenses`, `compliance_type_definitions`, `role_summary`,
`priority_users`, `tail_summary`, `leaderboard`, `shame_list`, `paired_workflows`,
`risk_concentration`, `type_concentration`, `dept_head_pattern`, `key_findings`,
`caveats`

Notable design decisions worth carrying forward:

- **`priority_users_lenses`** — the same user list ranked three ways (volume,
  overdue, imprisonment). One ranking would hide people who hold few but
  catastrophic obligations.
- **`paired_workflows`** — performer/reviewer pairs, **queried per pair**, never
  inferred from volumes. This is what surfaced a pipeline where one performer's
  310 instances were all reviewed by a single *inactive* account.
- **`shame_list`** — retained here for internal analysis. **Do not ship this
  naming or framing to a customer.** Individual blame in a compliance report
  creates HR exposure and discourages honest reporting; the Users dimension
  reframes it as continuity and dependency risk.

---

## 4. `priority_actions`, `caveats`, `ledger`

**`priority_actions`** — 6 ranked items, each with `action`, `target_population`,
`effort`, `linked_kpi`, `outcome_metric`, `detail`. Every one names a **measurable
outcome** ("top-3 performer share below 40%"), not an aspiration. In the built
engine this is the **composition agent's** output, not a stored proc's.

**`caveats`** — four, each qualifying interpretation. The first is the most
important:

> *"All flags are review candidates, not confirmed violations — no
> statutory-applicability rules table exists."*

Without an applicability table the engine cannot know whether an obligation
genuinely applies to a given store. Everything is a **candidate for review**. This
caveat is non-negotiable in any customer-facing render.

**`ledger`** — `report_hash`, `novel`, `supersedes`. Report identity and lineage,
so a re-run can be recognised as superseding an earlier one. Maps onto the
`GeneratedReport` index table in the persistence design (spec §9.1).

---

## 5. How this maps to the built engine

| Sample block | Engine equivalent |
|---|---|
| `coverage.stores[]` | Location dimension `rows` |
| `coverage.summary.ownerless_*` | `Ownerless`, `OwnerlessPct` + Entity intermediate-node handling |
| `by_department` | Departments dimension |
| `by_user` | Users dimension (five facets) |
| `risk_weighted` | Risk dimension |
| `backlog`, `timeliness` | Dictionary-driven overdue / timeliness metrics |
| `priority_actions` | Composition + narrative agents |
| `caveats` | `data_quality` result set |
| `ledger` | `GeneratedReport` index row |
| `overall_health` | **Nothing — not yet specified** |
| `licence`, `forward_pipeline` | **Nothing — not yet built** |

**The three real gaps for the roadmap:** the composite health score (needs seven
component formulas defined), the licence block, and the forward pipeline.

---

## 6. What testers should check against this file

1. **Do not expect the engine to reproduce it.** Different period, different
   tenant state, and several blocks are unbuilt. Use it for **shape**, not values.
2. **Every percentage should have its numerator and denominator visible**
   somewhere in the block. Where it is not, that is a defect in the sample.
3. **Computed indices must be labelled** — `overall_health.score`,
   `forward_pipeline.predicted_at_risk`, and every component score are models, not
   measurements.
4. **Cross-check the ownerless discrepancy pattern** (§3.4). Any metric with two
   plausible denominators must state which one it used and why.
5. **Confirm the applicability caveat survives** into any render built from this
   shape. It is the difference between "197 violations" and "197 items to review",
   and only one of those is defensible.
