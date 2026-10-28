# All dimensions on the live-dimension rules - Risk, Nature, Internal, Event, ForwardPipeline, CoverageGaps, TimelinessFY, ForwardRisk (2026-10-27/28)

Brings the three dimensions that did not go live up to the same SQL level as the seven that did
(Location, Departments, Act, Entity, Users, BacklogAging, Licence). Found by comparing the
definitions PRODUCTION actually runs (read-only dump, `regtech_dev01_readonly` on 10.224.254.4,
2026-10-27) against the repo and against each other.

## What production was missing

| # | Gap | Risk | Nature | Internal |
|---|---|---|---|---|
| 1 | Real count behind each tenant-wide % (2026-10-06, b62f65a) - in the repo, never deployed. The C# DTOs and v4/v5 prompts already expect these columns. | `ImprisonmentOnCriticalInstances` missing (DTO reads null) | `TenantImprisonmentInstances` missing - DTO is a non-nullable `int`, so every prod Nature report reads it as **0** while the prompt says to cite it verbatim | `StatutoryNoInstanceOwnerInstances` / `InternalNoInstanceOwnerInstances` missing |
| 2 | One row per DUE DATE, not per compliance (2026-10-01, sql/v2/37-43) | already done | already done | **never converted** - tenant 1926: Internal `SumOfRows` 812 vs Risk 1,897 for the same user and window |
| 3 | Internal overdue = LATEST status of each due date | n/a | n/a | **v2 counted a due date overdue if ANY transaction was ever Open** - tenant 55: all 81 internal due dates are closed (56 Closed-Timely, 25 Closed-Delayed), v2 reported 27 of 27 compliances overdue (100%), v3 reports 0 |
| 4 | Internal window counts active due dates only | n/a | n/a | v2's internal window gate ignored `IsActive`/`IsUpcomingNotDeleted` |

Speed: Risk/Nature/Internal run in 1-2 s on prod for tenant 1926 (same as Act/Location), so the
live dimensions' 2026-10-05/06 scope-inlining is NOT needed here for performance. Internal v3 does
use the live dimensions' latest-transaction lookup (`#lt`, sql/v2/37-43) instead of
`tvfInsightsOverdueSchedules`, because joining that whole-tenant function on the schedule id is
what the live dimensions measured as slow.

## Files - run in this order

1. `01_usp_Insights_Dimension_Risk_v3.sql` - identical to `sql/v2/38`. Versus production: only adds `ImprisonmentOnCriticalInstances`.
2. `02_usp_Insights_Dimension_Nature_v3.sql` - identical to `sql/v2/39`. Versus production: only adds `TenantImprisonmentInstances`.
3. `03_usp_Insights_Dimension_Internal_v3.sql` - identical to `sql/v2/44` (new). Changes 1-4 above for Internal.
4. `04_usp_Insights_Dimension_Event_v2.sql` - identical to `sql/v2/45` (new). Event rebuilt on RegTrack's Event Based tabs - see "Event" below.
5. `05_tvfInsightsForwardPipelineSchedules_v3.sql` - identical to `sql/v2/46` (new). ForwardPipeline's estate - see below.
6. `06_usp_Insights_Dimension_CoverageGaps_v2.sql` - identical to `sql/v2/47` (new). CoverageGaps on the shared estate - see below.
7. `07_usp_Insights_Dimension_TimelinessFY_v3.sql` - identical to `sql/v2/49` (new). One row per due date - see below.
8. `08_usp_Insights_Dimension_ForwardRisk_v2.sql` - identical to `sql/v2/48` (new). Live-dimension rules - see below.
9. `99_rollback_restore_prod_2026-10-27.sql` - ONLY to undo: the exact production definitions of all eight objects as dumped on 2026-10-27.

EvidenceIntegrity needs no change (already on the shared estate, latest status per due date, active due dates) - checked
against an independent count: 1300 91 closures / 90 with a review trail, 691 31 / 31, both equal to the deployed procedure.

All files are pure ASCII (CLAUDE.md Sec. 5a). No C# change is needed for the SQL: every column
name is unchanged or already mapped in `DimensionShapes.cs`.

## Verification already done (read-only, prod replica, no objects created)

Each repo procedure body was run as an ad-hoc batch next to the deployed procedure:

- Risk / Nature (tenant 1926): every result set, every shared column identical to production; the
  only difference is the new column. 504 / 1,897 = 26.6% = `TenantImprisonmentSharePct`.
- Internal v3, statutory side - must equal Risk for the same user and window (same population):

  | Tenant / user | Window | `SumOfRows` v2 -> v3 | Risk `SumOfRows` | Stat. overdue v3 | Risk overdue |
  |---|---|---|---|---|---|
  | 1926 / 56762 | 2026-07-07..10-04 | 812 -> 1,897 | 1,897 | 1,897 | 1,897 |
  | 29 / 9310 | 2026-07-01..10-01 | 8 -> 35 | 35 | 34 | 34 |
  | 691 / 6256 | 2026-07-01..10-01 | 26 -> 50 | 50 | 42 | 42 |
  | 55 / 1824 | 2026-07-01..10-01 | 126 -> 190 | 190 | 94 | 94 |

- Internal v3, internal side - checked against an independent raw-table query (active internal due
  dates in the user's branches, active performer, latest transaction):
  691: 1,355 due dates, 527 overdue (v3: 1,355 / 527). 55: 81 due dates, 0 overdue (v3: 81 / 0).
- Rows sum to totals on every tenant (reconciliation THROW 51111 not hit).

Ad-hoc batches are slow for BOTH v2 and v3 (local variables instead of parameters - v2 run the same
way took 143 s vs 0.4 s installed), so timing was measured on UAT after install (below).

## UAT - deployed 2026-10-27 16:26 IST

01-03 installed on UAT (10.13.0.6) and confirmed by definition, not by the PRINT. UAT's previous
definitions were dumped first (none had the real-count columns). Installed timings, q2 window
(1 Jul - 30 Sep 2026), Pro tenants:

| Tenant / user | Risk | Nature | Internal | Internal = Risk? |
|---|---|---|---|---|
| 1285 / 11416 | 10.4 s | 10.9 s | 10.8 s | `SumOfRows` 255 = 255, overdue 180 = 180 |
| 29 / 645 | 37.9 s | 37.8 s | 37.7 s | 3 = 3, 3 = 3 |

Internal v3 costs the same as Risk/Nature on each tenant (the absolute numbers are UAT's).

Real reports (in-process compose -> narrate -> render with the new prompts, composition v5 /
render v6, UAT data, tenant 1285, q2): all three rendered. Nature: 7 percentage hover-links, 0
untraced numbers. Risk: 10 hover-links (percentage and point-difference, every formula checked
against the rows - e.g. 180 / 255 = 70.6%, 100.0% - 70.6% = 29.4 points), 1 untraced number, "27",
which came from the period label "Q2 FY2026-27" - a number-gate false alarm fixed in
`ReportClaimExtractor` in the same commit (it affected every dimension's FY2026-27 reports whose
data held no 27). Internal: 0 untraced; no hover-link that run because its prose stated no rate
(section 7b then produces nothing, by design).

## Still open (not in this package, needs a decision)

- **Production's `tvfInsightsScopedInstances` has its EventFlag filter commented out**
  (`--AND c.EventFlag IS NULL`, object modified 2026-10-06). The repo (`sql/v2/36`) has it active.
  So every dimension on production counts event-triggered compliance as statutory again. Not
  touched here.
- The live dimensions on production run a scope-inlined version (temp tables `#cbbranch`,
  `#entassign`, `#assignedInst`, dated 2026-10-05/06) that exists nowhere in the repo, and do NOT
  have the repo's 2026-10-06 real-count columns either. Deploying `sql/v2/37,40-43` from the repo
  would add the columns but replace that production-only version.

## Event (04) - rebuilt on RegTrack's Event Based tabs (product decision 2026-10-27)

v1 counted event SET-UPS (`EventInstance`) whose start date fell in the period. No RegTrack report
shows that figure, and it was almost always empty. RegTrack shows events as the Detailed Report's
"Event Based" and "Event Based Checklist" tabs: compliance with `EventFlag = 1`, due once an event
is triggered. v2 counts exactly those rows with the same rules as every other dimension (scope pairs,
active performer, visible, Act not deleted, active due dates, latest status Open + past due =
overdue), grouped by event type (`EventID` 0 = no event recorded). The old "event types not
triggered" observation is kept as a side fact (`EventTypesNotTriggeredRecently`) with its
off-system caveat. Contract change: `EventControlTotals` / `EventRow` (DimensionShapes.cs), the
source map `source-maps/event.json`, prompts composition v4 / render v5.

Verified read-only on production, q2 FY26-27, against an independent raw-table count of the tabs:

| Tenant / user | v1 (prod today) | Event Based tabs (independent) | v2 |
|---|---|---|---|
| 1300 / 22426 | 0 events | 34 due dates, 28 overdue, 15 checklist | 34 / 28 / 15, 10 event types + 4 not recorded |
| 1926 / 56762 | 0 | 3 due dates, 3 overdue | 3 / 3 (all "not recorded") |
| 29 / 9310 | - | 0 | 0 |

Every check in the new source map (15) returns the procedure's own value on 1300. Deployed to UAT
2026-10-27 17:22 IST: 0.3 s on Pro tenants 5 (330 due dates, 328 overdue) and 1285 (5 due dates,
3 event types).

## ForwardPipeline (05) and CoverageGaps (06) - the shared estate

Both read compliance outside the shared scope function, so neither applied the RegTrack rules the
live dimensions use (Act not deleted, compliance visible, statutory only, active performer).
Both feed the holistic report's panels on every run, so the difference already reached customers.

**ForwardPipeline** - `tvfInsightsForwardPipelineSchedules` v3 adds the four rules (only caller:
usp_Insights_Dimension_ForwardPipeline, checked in sys.sql_modules). Due in the next 90 days,
production replica, 2026-10-27, versus an independent raw-table count with the same rules:

| Tenant / user | Old | New | Independent |
|---|---|---|---|
| 1300 / 22426 | 4,243 | 4,230 | 4,230 |
| 1926 / 56762 | 2,349 | 2,347 | 2,347 |
| 29 / 9310 | 40 | 29 | 29 |
| 691 / 6256 | 55 | 55 | 55 |

**CoverageGaps** - the per-branch Labour list (`#obl`) now comes from tvfInsightsScopedInstances.
Same result sets and columns. Production replica, 2026-10-27:

| Tenant / user | Labour compliances counted | Peer set | Gaps | Under-configured |
|---|---|---|---|---|
| 1216 / 44064 (1,521 leaf branches) | 146,159 -> 117,613 | 1,373 -> 1,373 | 767 -> 729 | 175 -> 151 |
| 1926 / 56762 | 603 -> 398 (13 branches whose Labour work has nobody assigned -> 0) | 22 -> 9 | 0 -> 0 | 0 -> 0 |
| 1300 / 22426 | 1,234 -> 1,229 | 13 -> 13 | 0 -> 0 | 0 -> 0 |

Independent check, tenant 1216 branch 56190: raw-table Labour count with all rules = 76; v2 run on
the production replica = 86, and the 10 extra are exactly the branch's event-based Labour
compliances - because production's tvfInsightsScopedInstances has its EventFlag filter commented
out (see "Still open"). With the repo/UAT function, v2 gives 76. This is the first measured effect of
that production change on a report figure.

## TimelinessFY (07) and ForwardRisk (08) - 2026-10-28

**TimelinessFY v3** - on-time share per DUE DATE from its latest transaction. v2 counted every completed
transaction (a due date closed twice counted twice; one closed then reopened still counted). Production
replica, q2 window vs the same window last year: tenant 1300 closures 369 / 4,006 -> 368 / 3,857 (on time
67.2% / 81.3% -> 67.1% / 83.3%), equal to an independent latest-status count; 691 unchanged (6 / 11).

**ForwardRisk v2** - (1) forward window = ForwardPipeline's rule (from today, latest status Open only);
(2) overdue via the per-instance latest-transaction lookup (#lt), no whole-tenant tvfInsightsOverdueSchedules
(v1 called it twice); (3) ownership factors F1/F1b/F2 retired - never scored, flagged or narrated, columns
stay at 0; (4) BranchName NVARCHAR(600); (5) detector `majority_at_risk` fixed (searched a flag name that
never existed). Production replica: 1300 window 4,219 -> 4,234 due dates = independent 4,234 (3,014
compliances); CleanAtRisk unchanged (40) - the ownership factors never fired there. UAT, installed: now equal
to ForwardPipeline for the same user (1285: 38 / 34, was 40 / 36; tenant 5: 22 / 21, was 27 / 25), and about
twice as fast (5.6 s vs 10.7 s; 31 s vs 62 s). First UAT install failed (UNION column name) and was caught by
checking the definition - CREATE OR ALTER kept the old procedure until the fixed file installed.

Deployed to UAT 2026-10-28 10:38 / 10:40 IST. Real reports (UAT 1285): TimelinessFY, EvidenceIntegrity,
ForwardRisk all rendered, 0 untraced numbers. ForwardRisk composition prompt v2 describes the one remaining factor.
