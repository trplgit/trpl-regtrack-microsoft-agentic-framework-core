# RegTrack Insights v3 - PRODUCTION deploy package

Incremental on top of `sql/v2-production-deploy/`, which is already live in production
(deployed 2026-10-01 ~14:00-14:16 IST, confirmed via the "installed" messages in the deploy
channel). This package contains ONLY what changed since that deploy - nothing here needs
`sql/v2-production-deploy/` re-run first, and nothing in that package needs to change.

## What this fixes

1. **`36_tvfInsightsScopedInstances_eventflag_v2.sql`** - a gap in what's live in production
   right now. Production's `tvfInsightsScopedInstances` (from `sql/v2-production-deploy/02`)
   has no `EventFlag` filter, so event-triggered compliance (e.g. POSH complaint-handling
   clauses) is still being counted as ordinary statutory obligations in every dimension that
   reads this shared function - Location/Risk/Nature/Departments/Act/Users/Entity, all of
   them. Confirmed against the real RegTrack report SP's own classification logic. Found live
   on tenant 1285: 31 of 173 Location-dimension instances were misclassified before this fix.

2. **`37`-`41` (Location/Risk/Nature/Departments/Act)** - `Instances`/`Overdue`/`OverduePct`
   now count every real scheduled OCCURRENCE in the period window (one row per real due date),
   not one row per distinct obligation - matching RegTrack's own Detailed Report row shape
   exactly. Verified against a real RegTrack Detailed Report export (tenant 1285, Apr-Jun
   2026): exact match on every branch, all 5 dimensions, after this change plus `36`.

   Same package also fixes a defect these 5 files' PRIOR UAT-only iteration had (never
   deployed to production, so nothing in production regresses): `ScopedInstances` had been
   accidentally redefined to report the OCCURRENCE count instead of the DISTINCT OBLIGATION
   count its name promises. `SumOfRows`/`OverdueInstances` stay occurrence-grain (correct,
   RegTrack parity); `ScopedInstances` now correctly reports the distinct count again.

3. **`42` (Entity)** - same occurrence-level conversion as `37`-`41`, applied to the
   ancestor/descendant entity-tree rollup. `ScopedInstances=142` (distinct), `SumOfRows=280`
   (occurrences), both tying to the same tenant-wide totals as `37`-`41`.

4. **`43` (Users)** - same occurrence-level conversion, PLUS a real, separately-confirmed
   defect: `PerformerInstances`/`ReviewerInstances` were built from `ComplianceAssignment`
   (a static, instance-level label) instead of `ComplianceScheduleOn.Performerid`/`Reviewerid`
   (the actual per-occurrence performer/reviewer of record). Confirmed against the real
   RegTrack export: the per-occurrence columns match its Performer/Reviewer columns EXACTLY
   (156/119/3/1/1 and 273/4/3 respectively); `ComplianceAssignment` did not (it was off by
   large margins on Performer specifically - 61 vs the real 156 on one user, 216 vs the real
   119 on another). `AssignedInstancesDistinct`/`UnassignedInstances` deliberately stay on
   `ComplianceAssignment` (a different question - "is anyone formally assigned" - not changed).
   New output columns `ScopedOccurrences`/`AssignedOccurrencesDistinct` expose the
   occurrence-grain totals explicitly so nothing is left ambiguous.

## Deploy order

Run in this exact order. All files are pure ASCII.

1. `36_tvfInsightsScopedInstances_eventflag_v2.sql` - must run first; 37-43 all read through it
2. `37_usp_Insights_Dimension_Location_v3.sql`
3. `38_usp_Insights_Dimension_Risk_v3.sql`
4. `39_usp_Insights_Dimension_Nature_v3.sql`
5. `40_usp_Insights_Dimension_Departments_v3.sql`
6. `41_usp_Insights_Dimension_Act_v3.sql`
7. `42_usp_Insights_Dimension_Entity_v3.sql`
8. `43_usp_Insights_Dimension_Users_v3.sql`

Each file is a complete, standalone `CREATE OR ALTER PROCEDURE`/`CREATE OR ALTER FUNCTION` -
no file needs anything from this package run before it except `36`. Not included: Licence
(deliberately excluded from this effort, untouched), Internal/TimelinessFY/FreeMonthly_LoadFacts/
GoldenInvariants (not part of today's work, already correct in production from the `v2` package).

## Verification

All 7 files in this package (`37`-`43`) were confirmed byte-identical between what is in this
folder and what is live and tested on UAT at package time (2026-10-01) - diffed directly
against `sys.sql_modules`, not just "should match". `36`-`43` were all verified against the
real RegTrack Detailed Report export (tenant 1285, Apr-Jun 2026) after deployment, with exact
or near-exact (within 1 row, explained, not a defect) matches on every dimension checked.

No rollback script in this package, same as `sql/v2-production-deploy/` (per that deploy's own
note - a `DROP` is not a real rollback for a `CREATE OR ALTER` on an existing production
object; if a revert is ever needed, restore the specific object's prior definition from
`sql/v2-production-deploy/`'s own files instead).
