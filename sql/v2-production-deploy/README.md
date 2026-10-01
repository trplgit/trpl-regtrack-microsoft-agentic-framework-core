# RegTrack Insights v2 - PRODUCTION deploy package

This folder is the curated, production-ready subset of `sql/v2/`. That folder keeps every
file written during development, including iterations that were later superseded within the
same effort before anything was deployed to production. This folder keeps only the FINAL
version of each object - the one actually proven on UAT - so production is installed once,
directly to the correct definitions, with no intermediate state.

**Full history, context and the parity proof live in `sql/v2/README.md` - read that first if
you want the "why" behind any rule.** This README is only the "what to run, in what order."

## Why some numbers are missing

`sql/v2/` numbers files in the order they were written, not the order they supersede each
other. Several objects were revised more than once during UAT testing:

| Object | Superseded (UAT-only, do NOT deploy) | Final (deploy this one) |
|---|---|---|
| usp_Insights_Dimension_Location | 10 | **27** |
| usp_Insights_Dimension_Entity | 11 | **28** |
| usp_Insights_Dimension_Risk | 12 | **29** |
| usp_Insights_Dimension_Nature | 13 | **30** |
| usp_Insights_Dimension_Departments | 14 | **31** |
| usp_Insights_Dimension_Act | 15 | **32** |
| usp_Insights_Dimension_Users | 16 | **33** |
| usp_Insights_Dimension_Internal | 17 | **34** |
| usp_Insights_Dimension_Licence | 23, 24, 25 | **26** |

Each "final" file is a complete, standalone `CREATE OR ALTER PROCEDURE` for the object (built
from the UAT-deployed definition, not a patch against the superseded file) - confirmed by
checking every pair targets the identical object name. You do not need to run the superseded
file first; running only the final file is sufficient and is what this package contains.

Files 02-04, 18, 20, 21, 22 have no later revision - only one version of each was ever written,
so they appear here unchanged.

## Deploy order

Run in this exact order. All files are pure ASCII; run each with `SQLCMD`/SSMS against the
production database, checking for errors after every file before moving to the next.

1. `01_classification_dictionary_v2.sql` - dictionary v2 (must run first; everything below reads it)
2. `02_tvfInsightsScopedInstances_v2.sql`
3. `03_tvfInsightsOverdueSchedules_v2.sql`
4. `04_tvfInsightsForwardPipelineSchedules_v2.sql`
5. `27_usp_Insights_Dimension_Location_v2.sql`
6. `28_usp_Insights_Dimension_Entity_v2.sql`
7. `29_usp_Insights_Dimension_Risk_v2.sql`
8. `30_usp_Insights_Dimension_Nature_v2.sql`
9. `31_usp_Insights_Dimension_Departments_v2.sql`
10. `32_usp_Insights_Dimension_Act_v2.sql`
11. `33_usp_Insights_Dimension_Users_v2.sql`
12. `34_usp_Insights_Dimension_Internal_v2.sql`
13. `18_usp_Insights_Dimension_TimelinessFY_v2.sql`
14. `20_usp_Insights_FreeMonthly_LoadFacts_v2.sql`
15. `21_usp_Insights_GoldenInvariants_v2.sql`
16. `22_licence_report_status_dictionary_v2.sql` (must run after 01)
17. `26_usp_Insights_Dimension_Licence_status_totals_v2.sql` (must run after 22)

After 17/17 complete with no errors, run `EXEC dbo.usp_Insights_GoldenInvariants_v2;` once by
hand and confirm it does not `THROW` - this is the same structural-invariant suite described in
CLAUDE.md section 11, now checking the v2 overdue rule.

## Rollback

`99_rollback_v2.sql` restores every object this package changed to its pre-v2 definition (as
deployed before 2026-09-29) and makes dictionary v1 current again. It is independent of which
v2 iteration was last installed (10-17/23-25 vs 27-34/26) - it restores to the same pre-v2
baseline either way, so it is safe to run even though it predates files 26-34. Run it as a
single script if a rollback is needed; do not run it alongside the forward files above in the
same session.

## What changes for users after this deploys

- Overdue counts and rates drop across every dimension (tenant 1285 backlog example: 3,846 ->
  2,926) - this is intentional, it is RegTrack's own counting rule, not a regression.
- "No owner" / ownerless findings disappear everywhere (every obligation now requires an active
  performer to be counted at all, so the figure is always 0).
- Weekly/monthly free digests change the same way, since they share these functions.
- Licence dimension shows RegTrack's own status labels (Active/Expired/Applied/Pending for
  review/Rejected/Application rejected/Terminated/Not applicable/Other), windowed by the report
  period exactly as `SP_LicenseMyReport_V2` windows it, plus the 8 new pre-summed tenant-wide
  total columns from file 26.

## Not included here

`sql/v2/` also contains `sql/v2/README.md` itself and the superseded files listed in the table
above - kept there as the historical record of how each final definition was reached, not
needed for this deploy.
