# v4 - Free tier follows RegTrack's Detailed Report (2026-10-06)

## What this changes, in plain words

The free monthly emails (RegInsights Basic: Overview, Users, Location, Act, Licence)
used to count compliance the way the **RegTrack 2.0 management dashboard** does.
The testing team checks numbers against the **Detailed Report Excel export**, which
counts differently. From this release the free emails count exactly like that export:

| | Before (dashboard) | Now (Detailed Report export) |
|---|---|---|
| Checklist compliances | left out | **included** (Statutory + Statutory Checklist tabs) |
| Event-based compliances | included | **left out** |
| Overdue | status 1, 12, 13, 14, 21, 22, 23 | **status Open (1) only**, due before today |
| "Approved" (7/9) on time or late | compared with time of day | compared by **date** (closed on the due day = on time) |
| What one "item" is | one due date | **one row of the Excel export** (a due date with 2 performers = 2 rows) |
| Licences | dashboard licence rules | RegTrack **licence report** rules (active performer, perpetual = no end date) |

**Update 2026-10-07 (licence end dates, merged into this pack):** the licence figures "reached
their end date last month / so far this month / between today and month end" count every licence
whose End Date falls in that period, whatever its status - exactly the licence report's End Date
column (Minda, September: report 2, earlier v4 said 1, now 2). Every "no renewal filed" figure and
every named licence still leaves out licences already Renewed / Terminated / Not Applicable, so the
email never calls a renewed licence "not renewed". Proven on prod read-only (1008, 1216, 1810): only
those window totals and the "of M" of named findings change; the same licences are named with the
same counts, and every detector and pattern is identical. Changes 3 procedures: Overview, Licence
(file 01) and LoadLicences (file 03).

Why: product decision 2026-10-06 - the Detailed Report is the testing team's source of truth.
This reverses the 2026-09-29 "match the dashboard" decision **for the free tier only**.
The paid tier is not touched.

Consequences to know:
- Free-tier numbers will **no longer match the management dashboard**. That is expected.
- Emails still say "obligations", but now count **export rows**. A compliance with two
  performers is counted twice, exactly as it appears twice in the Excel export.
- Some email facts do not exist in the Detailed Report at all - number of locations / laws in
  scope, people holding open work, deactivated owners, self-review. They are kept, counted
  from the same set of compliances, so they are consistent, but testers will not find them
  on the report.
- Known edge case: when one due date has two "latest" transactions at the same second with
  **different** statuses (measured 2026-10-06: 3 schedules on one prod tenant, none elsewhere),
  the export shows them under two statuses; the email puts both rows under one. Totals still match.

## Files - run in this order

| # | File | What it does | Changes data? |
|---|---|---|---|
| 0 | `00_precheck.sql` | Confirms the database still has exactly the procedures this pack was built from, plus 4 reference-data checks. Read-only. | No |
| 1 | `01_slot_procs.sql` | Overview, Users, Location, Act, Licence: count export rows (`ExportRows`) | Procedures only |
| 2 | `02_usp_Insights_FreeMonthly_MemberDetectors.sql` | Same, for the shared detector proc | Procedures only |
| 3 | `03_usp_Insights_FreeMonthly_LoadLicences.sql` | Licence loader follows the licence report | Procedures only |
| 4 | `04_usp_Insights_FreeMonthly_LoadFacts.sql` | Compliance loader follows the Detailed Report | Procedures only |
| - | `05_verify_deployed.sql` | Read-only status check, any time: per procedure NEW / OLD / OTHER, last line DEPLOYED / NOT DEPLOYED / MIXED | No |
| - | `99_rollback.sql` | Undo: restores all 8 procedures exactly as they were on 2026-10-06, re-applies their grants, verifies hashes | Procedures only |
| - | `_current_prod/` | Byte-exact copies of the 8 procedures as deployed on prod and UAT on 2026-10-06 (identical on both). Reference only - do not run. | - |

No table, column, index or data is changed. Files 1-4 use `CREATE OR ALTER`, so existing
grants (prod: `reginsights_sql_readonly_user`) are kept.

## Steps for Vinay (UAT first, then prod, then demo)

1. Pick a time outside the free-digest schedule (not Sunday night / Monday morning IST).
2. Run `00_precheck.sql`. It must end with `PRECHECK OK`. Its first grid shows each procedure's
   version: **OLD** (before v4 - expected on demo), **V4 EARLY** (v4 of 2026-10-06 without the
   licence fix - expected on prod and UAT) or **V4** (already done). If it THROWs (51181-51185),
   **stop** and send the message to the author - do not run anything else.
3. Run `01`, `02`, `03`, `04` in that order, in one sitting. Each ends with `... installed`.
   On prod/UAT only `01` and `03` actually change (Overview, Licence, LoadLicences); `02` and `04`
   re-install the identical text, which is harmless - running all four keeps one procedure for
   every environment. Any error: stop and run `99_rollback.sql`.
4. Run `05_verify_deployed.sql` - last line must be `DEPLOYED - all 8 procedures are this v4 pack`.
4. Run the encoding check (CLAUDE.md Sec.5a) - must return zero rows:
   ```sql
   SELECT o.name FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
   WHERE o.name LIKE '%Insights%'
     AND m.definition COLLATE Latin1_General_BIN2 LIKE N'%[^ -~' + NCHAR(9) + NCHAR(10) + NCHAR(13) + N']%';
   ```
5. Smoke test (read-only):
   ```sql
   EXEC dbo.usp_Insights_FreeMonthly_Overview @UserID = <MGMT user>, @CustomerID = <tenant>,
        @CurrMonthStart = '2026-09-01', @AsOf = '2026-09-27T23:59:59';
   ```
   UAT: 1285 / 11416. Prod: 1916 / 56009 - expect the obligations figure to be about 281, not 22.
6. Tell the team it is live on that environment. Prod only after UAT testing is signed off.

**Undo:** run `99_rollback.sql`. It must end with `ROLLBACK OK`. It restores the procedures as
they were BEFORE v4 (dashboard rules), on any environment.
Never use `sql/v2/99_rollback_v2.sql` for this - it restores an older LoadFacts.

## Proof before deploy (2026-10-06, prod read-only replica, session temp procs - nothing deployed)

- **Detailed Report parity** - new LoadFacts vs `Kendo_DetailedReport_Details_Pagination_2` (MGMT, both tabs),
  compared per due date (`ScheduledOnID`) for Sep 2026, 1-5 Oct, 6-31 Oct, plus overdue stock:

  | Tenant / user | Sep | 1-5 Oct | 6-31 Oct | Overdue | Per-due-date mismatches |
  |---|---|---|---|---|---|
  | 1916 / 56009 | 0 = 0 | 0 = 0 | 1 = 1 | 3 = 3 | 0 |
  | 1490 / 38406 | 193 = 193 | 343 = 343 | 116 = 116 | 219 = 219 | 0 |
  | 1403 / 33872 | 76 = 76 | 15 = 15 | 91 = 91 | 534 = 534 | 0 |

- **Licence parity** - new LoadLicences vs `SP_LicenseMyReport_V2`: tenant 1216 / user 20515 = 1530 = 1530.
  (Tenant 1810: RegTrack's own licence report fails with "String or binary data would be truncated"
  at its line 301 - a RegTrack bug; licence numbers for 1810 cannot be checked until it is fixed.)
- **Safe install order** - files 01 + 02 run with TODAY's loaders give byte-identical output to today's
  procedures (5 tenants x 5 emails, 25/25 identical), so a partial install changes nothing.
- **Insights intact** - all 5 emails on 1916, 1490, 1403, 29, 1216: no errors, every detector present.
  Some `pat_*` (aggregate pattern) facts change to named candidates and back: that is the emission
  policy (>20% flagged = one pattern, <=20% = top 5 named), not a lost insight.
- **Speed** - largest tenant tested (1216): ~20 s per email (was ~9 s). Others: 1-2 s.
- **Tenant 1916 now**: 281 obligations in scope (was 22), 3 overdue, findings appear in Users and Act.

## Other notes

- `sql/42_freetier_monthly_validation.sql` (repo script, not deployed) creates its own `#sched` and calls
  LoadFacts directly. After this deploy, add `ExportRows INT NOT NULL DEFAULT 1` as the last column of
  its `#sched` before running it. Nothing deployed calls the loaders except these 8 procedures (checked
  on prod and UAT).
- No application / worker redeploy is needed: the worker calls these procedures by name, and names,
  parameters and output columns are unchanged. The next run (scheduled or `run-preview.ps1`) uses them.

## For the testing team - how to compare

- Use the standard **Detailed Report -> Excel export**, as a **Management** user, tabs
  **Statutory** and **Statutory Checklist**, with **explicit From/To dates** (no dates = last 30 days only).
- Count data rows from Excel row 8, both tabs together.
- Run the export on the same day as the email's "as at" date.
- Which export range matches which email number:

| Email number | Export date range |
|---|---|
| Last month (due / closed / still open) | 1st to last day of last month, counted by Status |
| This month so far | 1st of this month to **yesterday** |
| Rest of this month | **today** to month end |
| Overdue now | a wide range up to yesterday, rows with Status = Overdue |
| Per location / per law | same rows grouped by Location / Act |

- Licence numbers: compare with the licence report export rows (Status, End Date - blank = perpetual).
- The "Dynamic Filters" Detailed Report uses a different procedure and is **not** covered.
- Never test on tenant 1082. UAT test tenant: 1285 (user 11416).
