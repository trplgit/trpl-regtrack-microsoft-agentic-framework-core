# FREE TIER MONTHLY SQL — Deployment and Reference

## 1. Summary

The free tier email is changing from one generic weekly digest to a **monthly schedule of topic-specific emails**. Each Sunday receives a different focus: Overview (1st), Users (2nd), Location (3rd), Act (4th), Licence (5th in 5-Sunday months). These eight new stored procedures produce the data for those emails; no data is written, and every existing table, view, function, and procedure remains unchanged. Install order is sql/34 through sql/41, followed by validation sql/42.


## 2. Install Order

| File | Object Created | Type | Error Codes |
|------|---|---|---|
| 34 | `usp_Insights_FreeMonthly_LoadFacts` | Helper (shared loader) | 51230-51239 |
| 35 | `usp_Insights_FreeMonthly_LoadLicences` | Helper (shared loader) | 51240-51249 |
| 36 | `usp_Insights_FreeMonthly_Overview` | Email procedure (Sunday 1) | 51250-51259 |
| 37 | `usp_Insights_FreeMonthly_MemberDetectors` | Helper (shared detector) | 51260-51269 |
| 38 | `usp_Insights_FreeMonthly_Users` | Email procedure (Sunday 2) | 51270-51279 |
| 39 | `usp_Insights_FreeMonthly_Location` | Email procedure (Sunday 3) | 51280-51289 |
| 40 | `usp_Insights_FreeMonthly_Act` | Email procedure (Sunday 4) | 51290-51299 |
| 41 | `usp_Insights_FreeMonthly_Licence` | Email procedure (Sunday 5) | 51300-51309 |
| 42 | — | Validation script (read-only, UAT only) | — |

**Install safety:** All eight procedures are new. No existing object is modified. No data is written during installation.


## 3. How the Pieces Fit

```
Two loaders (run once, fill temp tables):
  34 LoadFacts    ->  builds estate snapshot + ownership + risk
  35 LoadLicences ->  builds licence state + lapse tracking

One shared helper (called by multiple slots):
  37 MemberDetectors  ->  runs 4 common pattern checks on any entity type
                          (location, person, law)

Five email procedures (called once per Sunday):
  36 Overview   ->  calls 34, 35
  38 Users      ->  calls 34, 37
  39 Location   ->  calls 34, 37
  40 Act        ->  calls 34, 37
  41 Licence    ->  calls 35
```

Loaders return no result sets (only fill temp tables). Email procedures return exactly five result sets (see Section 5).


## 4. Input Parameters

Every email procedure (`usp_Insights_FreeMonthly_Overview`, `Users`, `Location`, `Act`, `Licence`) requires:

| Parameter | Type | Description |
|---|---|---|
| `@UserID` | INT | The recipient (a user in management role with entity scope) |
| `@CustomerID` | INT | The tenant ID (re-validated server-side against the user's access) |
| `@CurrMonthStart` | DATE | The 1st day of the calendar month for this email. Must be computed in C# from the edition; never from SQL GETDATE() |
| `@AsOf` | DATETIME | The instant the email is generated, in the same clock (UTC or IST) as ComplianceScheduleOn dates |

### Tuning parameters (optional; defaults suit most tenants)

| Parameter | Default | Email(s) | Meaning |
|---|---|---|---|
| `@RelativeRiskFactor` | 1.50 | Overview, Users, Location, Act, Licence | Flag a member if their rate is >= this multiple of the tenant's own rate |
| `@ConcentrationFactor` | 2.00 | Users, Location, Act | Flag a member if their share of all overdue work is >= this multiple of a fair share (1 / number of members) |
| `@MemberFloor` | 5 | Users, Location, Act | Minimum items a member must hold to be included in the comparison set |
| `@MaxPerDetector` | 5 | All | Maximum candidate names to carry per detector |
| `@AllowPersonNames` | 1 | Users | Set to 0 to withhold all person names (EntityLabel = NULL) if legal review required; no redeploy needed |
| `@SpofFloor` | 5 | Location | Minimum open items at a location to be a single-point-of-failure candidate |
| `@TypeFloor` | 10 | Licence | Minimum licences of a type to be included in lapse-rate comparison |
| `@CategoryFloor` | 10 | Overview | Minimum obligations in a category to be ranked |

These parameters exist so the email can be retuned by configuration change, not SQL redeploy. Defaults have been validated against production profiles.


## 5. What Every Email Procedure Returns

All five email procedures return exactly these result sets, **in this order**:

### Result set 1: `control_totals`
Reconciled counts and the time window, for provenance. Allows the email renderer to state "as at [date]" and helps a validator cross-check numbers.

| Column | Meaning |
|---|---|
| ScopedInstances | Obligations the user can see (intersection of their entity scope and all active branches/categories) |
| OverdueStockAsAt | Past-due, open items as of @AsOf |
| PrevMonthDueCount | Items whose due date fell in the previous calendar month |
| CurrMonthElapsedCount | Items due between month start and @AsOf |
| CurrMonthRemainingCount | Items due between @AsOf and month end |
| AsOfDate | @AsOf, for rendering |
| CurrMonthStart, CurrMonthEnd | The calendar month |

### Result set 2: `facts`
The **complete, closed set** the AI may cite. Numbers from this grid only; no other sources.

| Column | Meaning |
|---|---|
| FactKey | Unique identifier (e.g. `lm_due_count`, `overdue_liability_count`) |
| FactValue | The number (INT) |
| DisplayLabel | Plain-English label for the email (e.g. "Obligations due last month") |
| Section | Logical grouping (e.g. "last_month", "overdue_now", "licences") |
| DisplayOrder | Sort order within section |
| WindowScope | Which time window ("prev_month", "curr_elapsed", "curr_remaining", or NULL for totals) |
| ImpactClass | Why this number matters: `personal_liability` (officer criminal exposure) / `licence_continuity` (right to operate) / `operational_continuity` (work stalled) / `performance` (closure success) / `volume` (context) |
| SeverityTier | 1 (most severe) to 5 (context only) |
| AsAtRequired | 1 if this fact changes over time and needs an "as at" date stamp; 0 if stable |
| IsHeadline | 1 if this is the lead finding this month; 0 otherwise (exactly one per email, picked by explicit BA priority) |

### Result set 3: `detector_policy`
For each pattern check (detector), shows whether it is reporting named findings or an aggregate count.

| Column | Meaning |
|---|---|
| Detector | Check name (e.g. `overdue_concentration`, `single_point_of_failure`) |
| Eligible | Members (locations, people, laws) that met the materiality floor |
| Flagged | Members that triggered the rule (subset of Eligible) |
| FlaggedPct | (Flagged / Eligible) * 100, to nearest 0.1% |
| EmitMode | `individual` if flagged <= 20%, so names are listed; `aggregate` if > 20%, so a count is shown instead (CLAUDE.md Sec.4) |
| Note | Metadata: `degraded_peer_sample` if fewer than 2 members met the floor; `suppressed` if only 1 member exists in scope (comparatives are vacuous) |

### Result set 4: `candidates`
Named findings (up to 5 per detector), one per row. Only present if `EmitMode = 'individual'`. In aggregate mode, this grid is empty.

| Column | Meaning |
|---|---|
| Detector | Which check produced this candidate |
| Priority | Sort order (1 = led first in the email, by topic) |
| SeverityTier | 1-5, matching the severity of the problem (not the entity) |
| RankInDetector | Position within this detector's top 5 (e.g. 1-5) |
| EntityKind | `location` / `person` / `law` / `licence_type` |
| EntityId | The ID (BranchID, UserID, ActID, or LicenseTypeID) |
| EntityLabel | The name (location, person, law). May be NULL if `@AllowPersonNames = 0` |
| ProblemCount | Count of members with the same problem; used to compute "N of M" if aggregating later |
| ResidualCount | ProblemCount - 1 (the unranked members with this problem) |
| DefaultSlot | 1 or 2 if this is one of the top 2 by materiality (the email names at most 2 by default); NULL otherwise |
| TenantPct | The tenant's own rate on this measure (used for comparison phrasing: "location A is at X%, vs the tenant at TenantPct%") |

### Result set 5: `data_quality`
Declared gaps and structural notes. Never hidden; always visible so a recipient knows what they are NOT seeing.

| Column | Meaning |
|---|---|
| Category | Dimension or topic (e.g. `scope`, `ownership`, `licence_dictionary`) |
| Finding | Plain description of the gap (e.g. "3 obligations lack a ComplianceInstance entry", "no licence status rows for 15 licences") |
| Count | If applicable, the magnitude of the gap |
| Severity | `note` (informational) / `warn` (data unusual but not breaking) |


## 6. Per-Week Detail

### Sunday 1: Overview (usp_Insights_FreeMonthly_Overview)

**Question:** How is the compliance estate doing this month vs last month, and what is expiring?

**Detectors (pattern checks):**
- `liability_overdue_location` — Locations holding imprisonment-bearing obligations; flagged if that location's overdue rate >= 1.50x the tenant's average.
- `category_overdue_skew` — Compliance categories at or above the floor; flagged if overdue rate >= 1.50x the tenant's average.

**Main fact keys:**
| Group | Keys |
|---|---|
| Last month outcomes | `lm_due_count`, `lm_on_time_count`, `lm_late_count`, `lm_untimed_count`, `lm_terminal_count`, `lm_open_count`, `lm_completion_rate`, `lm_open_liability` |
| Current month (elapsed) | `tm_due_count`, `tm_completed_count`, `tm_open_count`, `tm_open_liability` |
| Rest of month (forward) | `rm_due_count`, `rm_liability_count`, `rm_critical_count`, `rm_no_owner_count` |
| Licence facts | `lic_valid_count`, `lic_expiring_unrenewed`, `lic_lapsed_recent_unrenewed` |
| Overdue stock | `overdue_count`, `overdue_liability`, `overdue_90_plus` |


### Sunday 2: Users (usp_Insights_FreeMonthly_Users)

**Question:** Who is carrying too much work, and are there control failures?

**Detectors (shared + local):**
- `last_month_slippage` (shared) — Share of last month's items still open; flagged if this person's share >= 1.50x the tenant's share.
- `liability_share` (shared) — Share of overdue work carrying personal criminal liability; flagged if >= 1.50x the tenant's share.
- `chronic_backlog` (shared) — Share of overdue work open > 90 days; flagged if >= 1.50x the tenant's share.
- `overdue_concentration` (shared) — This person's share of ALL overdue work; flagged if >= 2.00x a fair share.
- `deactivated_owner` (local) — Work assigned to deactivated user accounts (control failure: no one can act on it).
- `self_review` (local) — Performer and reviewer are the same person on an open item (four-eyes control missing).

**Main fact keys:**
| Group | Keys |
|---|---|
| Ownership facts | `performer_count`, `reviewer_count`, `no_owner_count`, `no_reviewer_count`, `deactivated_owner_count`, `self_review_count` |
| Concentration | `top_3_performers_share_pct` |


### Sunday 3: Location (usp_Insights_FreeMonthly_Location)

**Question:** Where is the risk concentrated, and where are things falling through cracks?

**Detectors (shared + local):**
- `last_month_slippage` (shared) — Locations where work is still open.
- `liability_share` (shared) — Locations holding high-liability overdue work.
- `chronic_backlog` (shared) — Locations where overdue work is aging past 90 days.
- `overdue_concentration` (shared) — Locations carrying disproportionate share of all overdue work.
- `single_point_of_failure` — Location where all open work has the same owner (zero redundancy). Flagged only if >= 5 open items and every one is owned by the same person.
- `ghost_location` — Location with no compliance obligations configured. Structural finding (often 50%+ of branches on some tenants).

**Main fact keys:**
| Group | Keys |
|---|---|
| Location facts | `branch_count`, `branch_in_scope`, `ghost_branch_count` |
| Overdue facts | `overdue_count`, `overdue_liability`, `overdue_90_plus`, `no_owner_count` |


### Sunday 4: Act (usp_Insights_FreeMonthly_Act)

**Question:** Which laws carry the most exposure, and which are failing at multiple sites?

**Detectors (shared + local):**
- `last_month_slippage` (shared) — Laws where work is still pending.
- `liability_share` (shared) — Laws where overdue work carries personal liability.
- `chronic_backlog` (shared) — Laws where overdue work is aging out.
- `overdue_concentration` (shared) — Laws holding disproportionate overdue share.
- `multi_location_pattern` — Law that is overdue at multiple locations (systemic, not local). Flagged if same law has overdue work at >= 2 locations AND the share of those locations with overdue work is >= 1.50x the average across all laws.

**Main fact keys:**
| Group | Keys |
|---|---|
| Law facts | `act_count`, `overdue_count`, `overdue_liability`, `overdue_90_plus` |


### Sunday 5: Licence (usp_Insights_FreeMonthly_Licence, 5-Sunday months only)

**Question:** What lapses, and what types are risky?

**Events (one-time findings, bounded by 2-name cap):**
- `licence_expiring_unrenewed` — The soonest licence expiring before month end with no renewal filed (dated fact).
- `licence_lapsed_recent_unrenewed` — Licence that lapsed in the past 2 months with no renewal filed.

**Detectors (pattern checks):**
- `expired_unrenewed_location` — Locations carrying expired, unrenewed licences; flagged if overdue rate >= 1.50x the tenant's average.
- `licence_type_lapse_rate` — Licence types with >= 10 licences in scope; flagged if lapse rate >= 1.50x the tenant's average.

**Main fact keys:**
| Group | Keys |
|---|---|
| Licence state | `lic_valid_count`, `lic_lapsed_count`, `lic_ended_other_count`, `lic_no_end_date_count` |
| Renewal status | `lic_renewal_in_progress_count`, `lic_unrenewed_count` |
| Recent activity | `lic_lapsed_this_month`, `lic_lapsed_last_month`, `lic_expiring_rest_month` |


## 7. Safety Guarantees

- **Fails closed, fails loudly.** Unknown tenant scope, missing dictionary entry, reconciliation mismatch, clock skew → `THROW` with a unique error code (51230-51309). Procedure never returns a result set on error; the error message names the problem.
- **Reconciliation before publish.** Window decomposition (Section 3 of 36) and per-member sums (sql/37) are verified before any row is emitted.
- **Scope is unchanged from the weekly digest.** Instance list is built from `tvfInsightsScopedInstances` (same 2-D filter: branch × category). Validation script (42) cross-checks against the weekly email's instance count to prove equivalence.
- **No hard-coded status or risk values.** All enums (status, risk class, licence status) come from the dictionary tables (`vInsightsStatusCurrent`, `InsightsEnumPolarity`). No `WHERE status NOT IN (4,5,15,18)` literals anywhere.
- **Ownership reads schedule-first.** 99.8% of schedules carry a PerformerID; the instance-level ComplianceAssignment (RoleID 3) is a fallback. The two-way split is preserved and declared in data_quality.
- **Pure ASCII source.** Every SQL file is ASCII-only; no multi-byte UTF-8, box-drawing, or em-dashes. The deployment path reads SQL as Windows-1252, so UTF-8 characters corrupt on re-save. Immune to the defect: ASCII.


## 8. Testing on UAT

### How to run

After installing sql/34 through sql/41 on UAT, run sql/42:

```sql
SQLCMD -S server -d vitComplianceSystem -i 42_freetier_monthly_validation.sql
```

### What the output means

The script produces one summary grid at the end. Read every row:

| Verdict | Meaning |
|---|---|
| **PASS** | Check succeeded; the procedure is working as built. All structural invariants are satisfied. |
| **WARN** | Data observation noted. The code is correct; the database contains something unusual (e.g. slow performance on a large tenant, or an empty peer sample on a small one). Read the Detail column; re-run if it's a transient timing difference. If it persists, investigate before shipping but do not stop the install. |
| **ERROR** | A structural invariant failed (a THROW inside a procedure). The Detail column names the error code (51230-51309). The code is broken, not the data. Stop and investigate. |
| **SKIP** | The tenant profile could not be tested (e.g. no management-role user with entity scope). Pick another tenant of the same profile from the list below. |

### Eight tenant profiles (why each one matters)

| Tenant | Profile | Tests |
|---|---|---|
| 1490 | Small, 2 lopsided apex entities | Baseline; comparatives and empty sets |
| 1403 | 6 balanced legal entities | Category/location spread; aggregation |
| 1472 | Large (819 branches), 24 orphan roots | Emission policy (aggregate mode) |
| 29 | 89% of estate under a soft-deleted parent | Scope must not lose the subtree; apex-or-orphan recursion |
| 522 | No branch >= 50 instances | Degraded peer sample (fallback when < 2 members meet floor) |
| 2480 | Single-branch | Comparatives suppressed ("rank 1 of 1" is vacuous) |
| 1807 | Single-branch | Comparatives suppressed |
| 1216 | 1.49M past-due schedules | Performance (watch the timing in load_facts_ms) |

Run on at least 5 different profiles before declaring the code validated.

### Negative tests (invariants that must fire)

The code checks these boundaries and should THROW if violated. Use them to spot-check during UAT:

- **51236** `@CurrMonthStart is not the first of a month` — Pass a date like 2026-02-15; must refuse.
- **51237** `@AsOf falls outside the edition month` (clock mismatch) — Pass a UTC @AsOf with an IST-derived @CurrMonthStart; must refuse.
- **51230** `SCOPE DENIED` — Pass a user who has no entity scope for the tenant; must refuse.

### What to eyeball in the grids

When you run sql/42 on a representative tenant, scan the final result sets for:

1. **control_totals grid:**
   - ScopedInstances > 0 (no empty scope)
   - OverdueStockAsAt <= ScopedInstances (sanity)
2. **facts grid:**
   - Exactly one IsHeadline = 1 (Overview) or one HeadlineSource row (other slots)
   - WindowScope is one of: 'prev_month', 'curr_elapsed', 'curr_remaining', NULL
3. **detector_policy grid:**
   - EmitMode is 'individual' or 'aggregate' (never NULL)
   - Single-branch tenants (2480, 1807): Location detectors show EmitMode = 'suppressed', Note = 'suppressed'; no location candidates
   - Tenant 522: At least one detector shows Note = 'degraded_peer_sample'
   - No detector shows Flagged > Eligible (sanity check)
4. **candidates grid:**
   - At most 2 rows with DefaultSlot = 1 or 2 per detector
   - DefaultSlot is 1 or 2, never other numbers
   - EntityLabel may be NULL if @AllowPersonNames = 0 (Users only)
5. **data_quality grid:**
   - Every row has Category, Finding, Severity
   - No row should have Count = 0 without explanation in Finding

**Important:** Nothing in sql/34-41 is considered validated until sql/42 runs clean on at least 5 of these profiles with different characteristics. Single-tenant testing has historically missed edge cases (CLAUDE.md Sec.11).


## 9. Rollback

All rollbacks live in the single file **99_rollback.sql**.

### To remove ONLY the monthly tier

In **99_rollback.sql**, run only the batch headed "Free-tier MONTHLY edition (sql/34 - sql/41)" - the eight `DROP PROCEDURE` lines up to the next `GO`. This drops LoadFacts, LoadLicences, Overview, Users, Location, Act, Licence and MemberDetectors and nothing else. The weekly digest and the paid tier keep working.

### To remove the entire Insights schema

Run **99_rollback.sql**:

```sql
SQLCMD -S server -d vitComplianceSystem -i sql/99_rollback.sql
```

This removes all objects created by sql/01 through sql/41, including these eight procedures, every dictionary table, and all indices. It is safe and idempotent.

**Note:** 99_rollback.sql was updated on 2026-09-18 to restore three objects that were inadvertently omitted from the working copy:
- `usp_Insights_FreeDigestArtifactClaim` through `usp_Insights_FreeDigestArtifactDelete` (7 procedures, sql/29)
- `InsightsFreeDigestArtifact` table (sql/29)
- `InsightsReportRequest` table (sql/30)

The rollback script verifies at the end that no Insights-related object remains; they RAISE an error if cleanup is incomplete.


## 10. Known Notes for Vinay

1. **Temp table maintenance trap (sql/37 header):** The five email procedures (36, 38, 39, 40, 41) each declare their own temp tables (`#mem`, `#smap`, `#detector`, `#cand`, `#facts`, etc.). T-SQL has no shared temp-table type, so they are declared inline in each file. Every shared declaration **must remain byte-identical** wherever it appears: `#inst`, `#sched`, `#mem`, `#smap`, `#mm` (in 38, 39, 40), and `#detector`, `#cand`, `#facts` (in all five). They were verified identical at hand-off. Adding a column to one of these tables is a five-file edit; SQL Server's deferred name resolution will not catch a missed file at CREATE time — the error only appears at **run time** when the column is accessed. Before deploying any change to a shared shape, diff all five files.

2. **Tenant 1216 performance baseline:** This tenant has 1.49M past-due schedules. Use it to measure the scale of sql/34's LoadFacts. **Nobody has measured it yet.** sql/42 marks LoadFacts over 30 seconds, and any email procedure over 60 seconds, as WARN - those are review thresholds, not predictions. If it is slow, report the timing back rather than changing anything: these procedures deliberately create no indexes on existing tables, and any such change is a separate decision.

3. **sql/06 note (documented defect, not fixed):** The existing weekly free-tier email (sql/06) counts "licences lapsing" as `Compliance.ComplianceType = 2` on schedules. This definition is stale; sql/21 replaced it with the licence register (`Lic_tbl_*`). The monthly procedures use the correct licence register (sql/35). sql/06 is left untouched on instruction and produces numbers that will not match the monthly Overview's licence facts. This is a known, documented gap — not a silent disagreement. See sql/35 header, "DEFECT NOTED, NOT FIXED HERE" for the original BA ruling.


---

**File location:** C:\Users\tanvig\Desktop\RegInsights\trpl-regtrack-microsoft-agentic-framework-core\docs\FREE_TIER_MONTHLY_SQL_HANDOFF.md

**Status:** Procedures are PROPOSED (not yet executed). This document is a deployment guide for UAT install and a reference for operation. Validation begins with sql/42 on UAT.
