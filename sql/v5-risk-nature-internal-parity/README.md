# Risk / Nature / Internal - parity with the live dimensions (2026-10-27)

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
4. `99_rollback_restore_prod_2026-10-27.sql` - ONLY to undo: the exact production definitions of all three as dumped on 2026-10-27.

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
