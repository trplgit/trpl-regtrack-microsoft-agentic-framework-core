# Deployment Instructions - RegTrack Insights SQL

**Target:** PRODUCTION `vitComplianceSystem` on `TRPL-Prod-VM04`
**Nature of change:** replaces stored procedures and functions, re-seeds a
reference table. **No table is dropped. No business data is touched.**
**Window:** low-traffic. Each object is briefly absent while it is replaced.

---

## 1. Before you start

**Encoding.** The files are pure ASCII, so they are safe with any tool. But the
deployment path here has previously read `.sql` as ANSI and corrupted stored
procedure text, including a pre-existing RegTrack procedure. Use the UTF-8 flag
anyway - it costs nothing and protects anything added later:

```
sqlcmd -f 65001 ...
```

**`01` is now atomic.** The dictionary seed is wrapped in a transaction with
`SET XACT_ABORT ON`. If any row fails, the whole seed rolls back and the
dictionary is left **unchanged** - never empty. It also re-raises the error, so
the caller sees a failure instead of a false "installed" message.

> This matters because it already went wrong once: on 4 Sep an over-length
> value aborted the INSERT after the DELETE had committed, leaving
> `InsightsEnumPolarity` empty - which silently disables the RiskType mapping,
> the inverted `IsActive` convention, `CustomerBranch.Status`, the licence
> classification and `NodeType`. Verified fixed: a deliberate failure now
> leaves all 64 rows intact.

**Use `-b`.** Without it sqlcmd continues past an error and prints the
trailing "installed" message anyway, which is how that failure went unnoticed.

**Take a script of the current procedures** so you can restore without a
redeploy if needed:

```sql
-- run and save the output before deploying
SELECT o.name, OBJECT_DEFINITION(o.object_id) AS definition
FROM sys.objects o
WHERE (o.name LIKE '%Insights%' OR o.name LIKE 'tvfInsights%')
  AND o.type IN ('P','IF','V')
ORDER BY o.name;
```

A full database backup is not required - nothing structural changes - but take
one if your change process calls for it.

---

## 2. Run these files, in this order

> **`05_dimension_location.sql` is HELD BACK.** The deployed version is AHEAD of
> the repo file - it adds within-state peer comparison (`StateID`, `StateName`,
> `PeerStateOverduePct`, `VsPeerStateNormPP`) that the file does not contain.
> Deploying it would drop that capability. Reconcile the file against the live
> definition first. Production still needs the `cb.Status = 1` filter applied to
> that procedure - do it as a targeted change, not by running this file.


Order matters: `01` creates `tvfInsightsLatestStatus`, which eight of the
others depend on.

```
01_classification_dictionary.sql
02_golden_regression.sql
06_freetier_aggregates.sql
09_dimension_nature.sql
10_dimension_departments.sql
21_dimension_licence.sql
24_dimension_forward_pipeline.sql
25_dimension_evidence_integrity.sql
26_dimension_forward_risk.sql        <- new, never deployed
27_dimension_coverage_gaps.sql       <- new, never deployed
```

`99_rollback.sql` has also been updated. **Do not execute it** - it drops
everything. Keep it in the repo, current, for emergency teardown only.

**Do NOT redeploy** (unchanged): `03`, `04`, `07`, `08`, `11`, `12`, `13`, `14`,
`15`, `16`, `17`, `22`, `23`.

### Command line

```bash
cd sql
for f in 01_classification_dictionary.sql \
         02_golden_regression.sql \
         06_freetier_aggregates.sql \
         09_dimension_nature.sql \
         10_dimension_departments.sql \
         21_dimension_licence.sql \
         24_dimension_forward_pipeline.sql \
         25_dimension_evidence_integrity.sql \
         26_dimension_forward_risk.sql \
         27_dimension_coverage_gaps.sql
do
  echo "=== $f"
  sqlcmd -S TRPL-Prod-VM04 -d vitComplianceSystem -f 65001 -b -I -i "$f" || { echo "FAILED: $f"; break; }
done
```

`-b` stops on error, `-I` enables quoted identifiers. **If any file fails, stop
and report it** - do not continue past a failure, because later files depend on
earlier ones.

### SSMS

Open each file and execute in the order above. Confirm the `PRINT` message at
the end of each ("... installed.") before moving to the next.

---

## 3. What each script does

| Object type | Behaviour |
|---|---|
| Tables | `IF OBJECT_ID(...) IS NULL CREATE TABLE` - **skipped entirely if the table exists.** No drop, no schema change |
| Procedures, functions, views | `DROP` then `CREATE` - normal for code objects, they hold no data |
| Dictionary rows | `DELETE WHERE VersionId = 1` then re-`INSERT` - **intended**, see below |

**The dictionary re-seed is the point of this deployment.** Production currently
holds 16 rows in `InsightsEnumPolarity`; these files seed **64** - the licence
status classification, `NodeType`, `ComType`, and `CustomerBranch.Status`.
Without it, `27` fails on install and `21` runs outdated lapse logic.

`InsightsFreeDigestLog` and `InsightsDigestSuppression` are **not touched** -
their scripts are not in the deploy list. No opt-out or send-history is lost.

> **Check first:** if anyone has hand-edited a row in
> `InsightsStatusClassification` or `InsightsEnumPolarity` directly in
> production, that edit will be lost. Nothing suggests anyone has, but it is
> worth confirming before you run `01`.

---

## 3a. Expected values - UAT tenant 29, user 645

Every one of these was measured on UAT on 4 Sep 2026 with these exact files'
logic in place. After deploying to UAT, re-run and compare. A deviation is a
deployment problem, not a data problem - UAT data did not change between runs.

| Check | Expected |
|---|---|
| `usp_Insights_GoldenInvariants @CustomerID=29` | 8 rows, **all PASS** |
| G-2 detail | `new=21955 old=21906 pastdue(7,9)=13 pastdue(17)=0 pastdue(18)=62` |
| G-5 detail | `control=3737 rollup=3737 gap=0` |
| Dictionary | 23 status rows, **64 enum rows**, 0 unmapped |
| Location | ScopedInstances **3737**, reconciled, 177/177 branches, 62 ghosts |
| Entity | 3737, 104 apexes, grain `descend_one_level` |
| Nature | Categorised **2857** + Untagged **880** = 3737 |
| Departments | Assigned **622** + Unassigned **3115** = 3737 |
| Users | Distinct **3064** + Unassigned **673** = 3737, 93 users |
| Act | 3737, 252 acts, 13 states, **0 unlinked** |
| Risk | 3737, 4 levels populated |
| Internal | statutory 3737, internal **661**, 59 of 99 branches |
| Event | **363**, 145 of 177 branches uncovered |
| BacklogAging | **22070 = 22070** (this one used to time out) |
| EvidenceIntegrity | **1151**, 90.7% with a review trail |
| Licence | Scoped **443**, typed 440, lapsed **65.7%**, excluded 23 |
| CoverageGaps | 0 gaps - peer groups below the 10-member minimum on this tenant |
| ForwardPipeline / ForwardRisk | DueNext90d **1** - UAT schedule data is frozen |
| `usp_Insights_EligibleTenants @UserID=645` | tenant 29, tier `pro`, `tenant_wide` |

> **Two of these are UAT artifacts, not bugs.** The forward window is nearly
> empty because UAT's schedules are frozen in the past, and CoverageGaps finds
> nothing because tenant 29's branches are spread too thinly across states to
> form a 10-member peer group. Both behave correctly on production data.

---

## 3b. Run every dimension in SSMS, not through a single-result-set client

A dimension emits **six** result sets. A client that reads only the first will
show `control_totals` and report success even when a later statement fails.

That is not hypothetical - it hid a live defect. `usp_Insights_Dimension_Location`
raised **"Ambiguous column name 'VsPeerStateNormPP'"** at the assertions step,
after `control_totals`, `rows` and `detector_policy` had already been returned.
Findings and data_quality never emitted, and every check that read only the first
result set reported PASS.

So verify in SSMS, which shows all six and every error. Or wrap each call:

```sql
BEGIN TRY
    EXEC dbo.usp_Insights_Dimension_Location @UserID = 38, @CustomerID = 29;
    PRINT 'Location: all result sets emitted';
END TRY
BEGIN CATCH
    PRINT CONCAT('Location FAILED at line ', ERROR_LINE(), ': ', ERROR_MESSAGE());
END CATCH
```

**A dimension is verified only when its SIXTH result set (`data_quality`) arrives.**

---

## 4. Post-deployment verification

Run all five. Report the output of each.

```sql
-- 1. Object count: expect procedures 34, functions 6, view 1, tables 6
--    (repo defines 5 tables; the 6th, InsightsTenantTokenUsage, is created by
--     the .NET cost instrumentation and is not in these scripts)
SELECT type_desc, COUNT(*) AS n
FROM sys.objects
WHERE (name LIKE '%Insights%' OR name LIKE 'tvfInsights%')
  AND type IN ('P','U','IF','V')
GROUP BY type_desc ORDER BY type_desc;

-- 2. Dictionary re-seeded: expect 23 statuses and 64 enum rows
SELECT (SELECT COUNT(*) FROM dbo.vInsightsStatusCurrent)   AS statuses_expect_23,
       (SELECT COUNT(*) FROM dbo.InsightsEnumPolarity)     AS enum_rows_expect_64;

-- 3. Encoding intact: expect ZERO rows
SELECT o.name
FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
WHERE o.name LIKE '%Insights%'
  AND m.definition COLLATE Latin1_General_BIN2
      LIKE N'%[^ -~' + NCHAR(9) + NCHAR(10) + NCHAR(13) + N']%';

-- 4. The performance fix works: expect a result in about 1 second, not 4 minutes
EXEC dbo.usp_Insights_GoldenInvariants @CustomerID = 1490;
-- expect 8 rows, every Result = 'PASS'

-- 5. The new function exists and returns rows
SELECT COUNT(*) AS past_due_schedules,
       SUM(CASE WHEN LatestTransactionId IS NULL THEN 1 ELSE 0 END) AS never_touched
FROM dbo.tvfInsightsLatestStatus(1490, GETDATE());
-- expect roughly 46,000 and 22
```

**If check 4 times out**, `01` did not deploy correctly - the old
`tvfInsightsOverdueSchedules` is still joining `RecentComplianceTransactionView`.

**If check 2 shows 16 enum rows**, `01` ran but the seed section did not.

---

## 5. If something goes wrong

Nothing here is destructive to business data, so the recovery is simply to
restore the previous procedure text from the script taken in step 1, or to
re-run the previous version of the affected file.

**Do not run `99_rollback.sql` to "start clean"** - it drops the dictionary
tables and `InsightsFreeDigestLog`, which discards the weekly-once guarantee
and can cause duplicate digest emails to real recipients.
