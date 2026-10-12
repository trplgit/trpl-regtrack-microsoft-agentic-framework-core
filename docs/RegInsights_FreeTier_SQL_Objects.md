# RegTrack Insights Free Tier - SQL Objects and Deployment Handoff (v4)

Status as of 12 Oct 2026: the v4 free-tier procedures are deployed and verified identical on UAT, demo and prod.

---

## 1. What the free tier is

RegInsights Basic is product 18 in the Product table (RegInsights Pro, the paid tier, is product 19). It sends one email per week to management (MGMT) users. The subject rotates by the Sunday of the month (MonthlyDigestCalendar): 1st Sunday = Overview, 2nd = Users, 3rd = Location, 4th = Act, 5th = Licence (a month with only four Sundays has no Licence email). Three Durable Task orchestrators run it:

- `FreeDigestGenerateOrchestrator`: Sunday from 00:00 IST - builds the email per scope group, encrypts it, stores it in blob, writes an artifact row.
- `FreeDigestInsightJsonOrchestrator`: also Sunday (same tick as generate), only when `FreeDigest:InsightApi:Enabled = true` - posts one JSON card per user to the Insight API.
- `FreeDigestSendOrchestrator`: Monday from 08:00 IST - sends the stored emails.

A "scope group" is the set of users with an identical (branch, category[, licence type]) scope; data is fetched and the LLM is called once per group, using a representative user.

Everything is off unless `FreeDigest:Schedule:Enabled = true`. `Email:RecipientOverride` (key under the "Email" section, NOT under "FreeDigest") redirects every email to one address - use it in non-production.

---

## 2. Entitlement - who gets the email

- A tenant is eligible when `ProductMapping` has `ProductID 18` with `IsActive = 0`. `IsActive` is INVERTED: 0 means enabled, 1 means disabled. Never "fix" this.
- Paid supersedes free: if the tenant ALSO has `ProductID 19` with `IsActive = 0`, `usp_Insights_FreeDigestGate` returns superseded and nothing is generated or sent. This is checked on both generate and send days.
- Recipients = management users from `dbo.tvfInsightsManagementUsers`, not deleted, with a non-blank email, not in `dbo.InsightsDigestSuppression`.
- The scheduler's tenant list is an inline query in `SqlFreeDigestRepository.GetEntitledTenantsAsync` (`ProductID 18`, `IsActive 0`, customer not deleted). `usp_Insights_EligibleTenants` (sql/17) is NOT the scheduler's list - it is the paid API's IDOR guard (`SqlTenantDirectoryRepository`).
- Current state (checked 11 Oct 2026): prod has 2 tenants on product 18 - 1916 Experian (free only, receives emails, 7 recipients) and 1300 TeamLease (also has product 19, so superseded). Demo has 3 tenants on product 18 (1300, 1897, 1899) and all three also have product 19, so demo sends nothing.

---

## 3. Where the code lives

The current free-tier procedures are in `sql/v4-free-tier-detailed-report/`. That folder is the single source of truth for the 8 monthly procedures. The main-folder files `sql/34` to `sql/41` with the same names are the OLDER, pre-v4 versions - do not deploy them over v4 (the one exception is the first-time install in section 7).

v4 = the free tier counts at the grain of RegTrack's Detailed Report Excel export (Statutory + Statutory Checklist), and licence figures follow RegTrack's licence report (`SP_LicenseMyReport_V2`). Overdue = latest status Open (`ComplianceStatus 1`) with due date before the as-of date; a due date with no transaction is not overdue; only active schedules, active performer (`RoleID 3`), non-deleted Act, visible compliance.

---

## 4. The v4 folder - file by file

| File | Object | Type | Called by | What it does | Error codes |
|---|---|---|---|---|---|
| `00_precheck.sql` | (checks only) | Read-only script | Run by hand before install | Confirms all 8 procedures are a known version (OLD / V4 EARLY / V4) and 4 reference-data checks: licence report labels present (needs sql/v2/22), `InsightsFreeDashboardStatusRule` `timed_by_close_date = {7, 9}`, overdue-eligible status set = `{1}` (needs sql/v2/01), `ComplianceStatus` names as expected. Ends with "PRECHECK OK". Changes nothing. | 51181-51185 |
| `36_freetier_monthly_overview.sql` | `usp_Insights_FreeMonthly_Overview` | Slot proc | `SqlFreeMonthlyDigestRepository` (email via `FreeMonthlyDigestComposer`, card via `InsightCardComposer`) | 1st Sunday: last month, this month so far, all-months overdue, locations and categories above average, licences, what falls due before month end. Calls `LoadFacts` and `LoadLicences`. | 51251-51256 |
| `38_freetier_monthly_users.sql` | `usp_Insights_FreeMonthly_Users` | Slot proc | same | 2nd Sunday: people - slippage, self-review, liability share, concentration of overdue work. Calls `LoadFacts` and `MemberDetectors`. | 51271, 51272, 51274 |
| `39_freetier_monthly_location.sql` | `usp_Insights_FreeMonthly_Location` | Slot proc | same | 3rd Sunday: sites - slippage, one-person sites, liability share, concentration, top-3 share. Calls `LoadFacts` and `MemberDetectors`. | 51281-51285 |
| `40_freetier_monthly_act.sql` | `usp_Insights_FreeMonthly_Act` | Slot proc | same | 4th Sunday: Acts - slippage, liability, concentration, Acts overdue across many locations. Calls `LoadFacts` and `MemberDetectors`. | 51291-51295 |
| `41_freetier_monthly_licence.sql` | `usp_Insights_FreeMonthly_Licence` | Slot proc | same | 5th Sunday: licences - expired with no renewal, locations and licence types holding them, end dates this/last month, expiring before month end. Calls `LoadLicences`. | 51301-51305 |
| `37_freetier_monthly_member_detectors.sql` | `usp_Insights_FreeMonthly_MemberDetectors` | Helper proc | Users, Location, Act | Peer-relative detectors (last-month slippage, liability share, chronic backlog, overdue concentration) through the emission policy: more than 20% flagged = one aggregate finding, otherwise top 5 individual findings. Reads its callers' temp tables. | 51261-51265 |
| `35_freetier_monthly_licence_loader.sql` | `usp_Insights_FreeMonthly_LoadLicences` | Helper proc | Overview, Licence | Loads licences in the user's licence scope with the licence report's latest status and labels (dictionary `LicenceReportStatus`). Reads RegTrack tables `Lic_tbl_LicenseInstance`, `Lic_tbl_LicenseStatusTransaction`, `Lic_tbl_StatusMaster`, `Lic_tbl_LicenseType_Master`, `Lic_tbl_LicenseComplianceInstanceScheduleOnMapping`, `LIC_EntitiesAssignment`, `RecentLicenseTransactionView`. | 51240, 51241, 51244-51249 |
| `34_freetier_monthly_core.sql` | `usp_Insights_FreeMonthly_LoadFacts` | Helper proc | Overview, Users, Location, Act | Loads every in-scope schedule with its latest status and export-row weight (`ExportRows`) into the caller's temp tables. Uses `vInsightsStatusCurrent`, `tvfInsightsScopePairs`, `InsightsDictionaryVersion`, `InsightsEnumPolarity`, `InsightsFreeDashboardStatusRule` and runs `usp_Insights_AssertStatusCoverage`. | 51230-51239 |
| `05_verify_deployed.sql` | (checks only) | Read-only script | Run by hand any time | Compares a fingerprint (SHA-256) of each deployed procedure with this pack: NEW (this pack), V4 EARLY, OLD (pre-v4) or OTHER. Last line "DEPLOYED - all 8 procedures are this v4 pack" when complete. Changes nothing. | none |
| `99_rollback.sql` | all 8 | Install script | Run by hand only to undo | Restores all 8 procedures exactly as they were before v4 (2026-10-06), re-applies grants, verifies hashes. Ends with "ROLLBACK OK". | - |
| `README.md` | - | Doc | - | Pack notes, proof of Detailed Report parity, steps. | |

`05_verify_deployed.sql` is a deployment-version check; `sql/42_freetier_monthly_validation.sql` (main folder) is a different thing - a data-validation harness that runs the procs on sample tenants. The current `sql/42` builds its `#sched` without the v4 `ExportRows` column, so it does not work against v4 `LoadFacts` yet.

---

## 5. Call graph

```
C# SqlFreeMonthlyDigestRepository (timeout 300 s)
  -> Overview  -> LoadFacts, LoadLicences
  -> Users     -> LoadFacts, MemberDetectors
  -> Location  -> LoadFacts, MemberDetectors
  -> Act       -> LoadFacts, MemberDetectors
  -> Licence   -> LoadLicences
LoadFacts -> usp_Insights_AssertStatusCoverage
```

A THROW from any of these refuses that scope group: nothing is sent to that group that week (fail closed).

---

## 6. Every other SQL object the free tier needs (already deployed, identical on UAT, demo, prod)

**Shared / dictionary:**

| Object | Type | File | Used for |
|---|---|---|---|
| `tvfInsightsManagementUsers` | inline TVF | sql/01_classification_dictionary.sql | recipient list (generate, send, cards) and inside the gate |
| `vInsightsStatusCurrent` | view | sql/01_classification_dictionary.sql | status semantics for `LoadFacts` |
| `usp_Insights_AssertStatusCoverage` | proc | sql/01_classification_dictionary.sql | fail-closed check that every status is classified; returns no result set |
| `InsightsStatusClassification`, `InsightsDictionaryVersion`, `InsightsEnumPolarity` | tables | sql/01 + sql/v2/01_classification_dictionary_v2.sql (dictionary v2: overdue-eligible = status 1 only) + sql/v2/22_licence_report_status_dictionary_v2.sql (`LicenceReportStatus` labels) | status and licence-label dictionary |
| `InsightsFreeDashboardStatusRule` | table + seed | sql/34_freetier_monthly_core.sql (MAIN folder, batches before its proc) | the `timed_by_close_date` rule `{7, 9}` `LoadFacts` reads; `LoadFacts` THROWs 51234 if it is missing |
| `tvfInsightsScopePairs` | inline TVF | sql/03_scope_resolution.sql | a user's (branch, category) scope; also feeds the scope signature |

**Gate and logs:**

| Object | Type | File | Used for |
|---|---|---|---|
| `usp_Insights_FreeDigestGate` | proc | sql/06_freetier_aggregates.sql | product 18 enabled, product 19 NOT enabled (else superseded), at least one recipient without opt-out; called by `ResolveDigestRecipientsActivity` and `ResolveDigestDispatchActivity` |
| `usp_Insights_FreeDigestAggregates` | proc | sql/06_freetier_aggregates.sql | legacy weekly digest aggregates, kept for backward compatibility |
| Artifacts: `InsightsFreeDigestArtifact` table, procs `_ArtifactClaim`, `_ArtifactComplete`, `_ArtifactRelease`, `_ArtifactsForDispatch`, `_ArtifactMarkDispatched`, `_ArtifactsForPurge`, `_ArtifactDelete` | table + procs | sql/29_freetier_digest_artifact.sql | one row per (CustomerID, WeekEnding, ScopeSignature). `_ArtifactClaim` via `ClaimDigestArtifactActivity`, `_ArtifactComplete` via `PersistDigestArtifactActivity`, `_ArtifactRelease` via `ReleaseDigestArtifactActivity`, `_ArtifactsForDispatch` via `ResolveDigestDispatchActivity` (only complete, not-yet-dispatched, within `FreshnessDays = 3`), `_ArtifactMarkDispatched` via `MarkDigestArtifactDispatchedActivity`, `_ArtifactsForPurge` and `_ArtifactDelete` exist; no purge job scheduled yet. Blob path `insights-digests/{CustomerID}/free_digest/{yyyy}/{MM}/{ArtifactId}.html.enc`, envelope-encrypted. |
| Send log: `InsightsFreeDigestLog` table, PK (CustomerID, UserID, WeekEnding), procs `_ClaimSend`, `_RecordOutcome`, `_ReleaseClaim` | table + procs | sql/15_freetier_digest_log.sql | `_ClaimSend` one send per user per week; `_RecordOutcome` records result; `_ReleaseClaim` on transient errors. |
| JSON card log: `InsightsFreeDigestJsonLog` table, PK (CustomerID, UserID, WeekEnding), procs `_InsightJsonClaimPost`, `_InsightJsonRecordOutcome`, `_InsightJsonReleaseClaim` | table + procs | sql/31_freetier_insight_json.sql | Card JSON itself is not stored. |
| Opt-outs: `InsightsDigestSuppression` table, PK (CustomerID, UserID), procs `_DigestSuppress`, `_DigestSuppressionList`, `_DigestUnsuppress` | table + procs | sql/16_freetier_suppression.sql | `_DigestSuppress` via API unsubscribe/bounce, `_DigestSuppressionList` reads it, `_DigestUnsuppress` manual only. Note: the unsubscribe link is currently commented out of templates/digest.html, so it is not reachable from emails. |

**RegTrack tables read (not ours, read-only):** `ProductMapping`, `Customer`, `CustomerBranch`, `User`, `ComplianceStatus`, `LIC_EntitiesAssignment`, `EmailDeliveryGatewayCustomization` and `EmailGatewayMaster` (email provider choice: 1 = Elastic Email, 2 = SendGrid), plus the licence tables listed in section 4.

**NOT used by the free tier (paid tier only):** `tvfInsightsLatestStatus`, `tvfInsightsOverdueSchedules`, `tvfInsightsScopedInstances`, all `usp_Insights_Dimension_*` procs, and every sql/v2 file except v2/01 and v2/22.

---

## 7. Deployment sequence

**7a.** UAT, demo and prod today: nothing to deploy. All 8 v4 procedures verified NEW on all three (05_verify_deployed.sql) and line-by-line identical to the files on 12 Oct 2026.

**7b.** Updating the 8 procedures on an existing environment (the normal case), in one sitting, outside Sunday night / Monday morning IST:

1. `00_precheck.sql` - must end "PRECHECK OK"; if it THROWs (51181-51185), stop.
2. `36`, `38`, `39`, `40`, `41` (the five slot procs).
3. `37` (MemberDetectors).
4. `35` (LoadLicences).
5. `34` (LoadFacts).
6. `05_verify_deployed.sql` - last line must be "DEPLOYED - all 8 procedures are this v4 pack".
7. Encoding check (must return zero rows):

```sql
SELECT o.name FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
WHERE o.name LIKE '%Insights%'
  AND m.definition COLLATE Latin1_General_BIN2 LIKE N'%[^ -~' + NCHAR(9) + NCHAR(10) + NCHAR(13) + N']%';
```

Why this order and not numeric: `37`, `35` and `34` read temp tables created by the slot procs, and the v4 versions use the `ExportRows` column that only the v4 slot procs declare. Running `34`/`35` first on a database with older slot procs makes those runs fail until the slot procs are updated. All files use `CREATE OR ALTER`, so grants are kept. Any error: run `99_rollback.sql` (must end "ROLLBACK OK").

**7c.** A brand-new database (first-time install), in this order:

1. sql/01_classification_dictionary.sql
2. sql/v2/01_classification_dictionary_v2.sql
3. sql/v2/22_licence_report_status_dictionary_v2.sql
4. sql/03_scope_resolution.sql
5. sql/06_freetier_aggregates.sql, sql/15_freetier_digest_log.sql, sql/16_freetier_suppression.sql, sql/17_eligible_tenants.sql, sql/29_freetier_digest_artifact.sql, sql/31_freetier_insight_json.sql
6. MAIN folder sql/34 to sql/41 (pre-v4). Needed because sql/34 creates and seeds `InsightsFreeDashboardStatusRule`, and because `00_precheck` requires the 8 procedures to exist in a known version.
7. Then the v4 pack exactly as in 7b.

---

## 8. Rules


- SQL files stay pure ASCII (the deployment path reads them as Windows-1252; multi-byte characters get corrupted). Use `=`, `-`, `->`, `*`, `Sec.`, `!=`, `<=`, `>=`, `x`.
- Never change text inside a procedure body without updating `05_verify_deployed.sql`: the fingerprints are computed from the stored definition, so any edit (even a comment inside the procedure) makes 05 report OTHER. Header comments above the first `GO` are not stored and are safe to edit.
- Every THROW code is unique and identifies one condition. Blocks: sql/06 gate 51000-51009; sql/15 51130-51139; sql/16 51140-51149; sql/29 51210-51219; sql/31 51220-51229; free-tier procs 51230-51309; v4 precheck 51181-51185.
- `ProductMapping.IsActive` is inverted (0 = enabled).
- Every `CREATE` in a sql/ file needs a matching `DROP` in the rollback.

---

## 9. Quick checks

Deployed version of the 8 procs: run `sql/v4-free-tier-detailed-report/05_verify_deployed.sql`.

List free-tier procs:

```sql
SELECT name, modify_date FROM sys.procedures
WHERE name LIKE 'usp_Insights_FreeDigest%' OR name LIKE 'usp_Insights_FreeMonthly%'
   OR name LIKE 'usp_Insights_InsightJson%' OR name LIKE 'usp_Insights_Digest%'
ORDER BY name;
```

Which tenants will get the free email this week (read-only):

```sql
SELECT c.ID, c.Name,
       CASE WHEN EXISTS (SELECT 1 FROM ProductMapping p WHERE p.CustomerID = c.ID AND p.ProductID = 19 AND p.IsActive = 0) THEN 'superseded by paid' ELSE 'eligible' END AS FreeTier
FROM ProductMapping pm JOIN Customer c ON c.ID = pm.CustomerID
WHERE pm.ProductID = 18 AND pm.IsActive = 0 AND c.IsDeleted = 0;
```

---

## 10. Summary counts

- 8 v4 procedures in `sql/v4-free-tier-detailed-report` (5 slot procs, 3 helpers), plus 00 precheck, 05 verify, 99 rollback.
- 5 free-tier tables: `InsightsFreeDigestArtifact`, `InsightsFreeDigestLog`, `InsightsFreeDigestJsonLog`, `InsightsDigestSuppression`, `InsightsFreeDashboardStatusRule`.
- Plus the shared dictionary tables, view and TVFs in section 6.
