# sql/v2 - Insights follows RegTrack's own Detailed Report (2026-09-29)

Product decision (2026-09-29): Insights must count exactly what RegTrack's own Detailed Report /
My Report shows - RegTrack has used these rules for years; no BA ruling was ever given for the
old v1 rules. The original `sql/` files are left untouched; every changed object lives here.

Reference procedure: `Kendo_DetailedReport_Pagination` (called by `ReportController` /
`DetailedReportService` in trpl-regtrack-dot-net-core-api, branch `staging`), run as
`@FlagIsApp = 'MGMT'`. Export: `D:\trpl-reginsights-dev\regtrack-report-sps\`.

## The rules copied from RegTrack

| Rule | v1 (old) | v2 (RegTrack) |
|---|---|---|
| Overdue | past due and in any of 17 not-closed statuses, or never touched | latest status **Open (1)** and due **before today** |
| Due today | overdue | not overdue (RegTrack "DueToday"); upcoming in the forward pipeline |
| Due date with no transaction | overdue ("never touched") | not overdue, not listed |
| Obligation needs an active performer | no | yes - `ComplianceAssignment.RoleID = 3`, active non-deleted user of the tenant (internal: `InternalComplianceAssignment`) |
| Obligation under a deleted act | counted | not counted (`Act.IsDeleted = 0`) |
| Hidden compliance (`ComplinceVisible = 0`) | counted | not counted |
| Switched-off due dates in a date range | counted | not counted (`IsActive = 1 AND IsUpcomingNotDeleted = 1`) |

Unchanged on purpose: branch rules (already identical), scope (`EntitiesAssignment`, already identical),
ClosureClass / Timeliness (open/closed/on-time metrics keep their meaning).

## Files, in install order

| File | Object | Change |
|---|---|---|
| 01_classification_dictionary_v2.sql | dictionary | relaxes CK_ISC_Coherent to CK_ISC_NoOverdueWhenClosed; adds dictionary version 2 (copy of v1, only status 1 overdue-eligible); makes v2 current |
| 02_tvfInsightsScopedInstances_v2.sql | tvfInsightsScopedInstances | active performer, act not deleted, compliance visible |
| 03_tvfInsightsOverdueSchedules_v2.sql | tvfInsightsOverdueSchedules | Open only (via dictionary), due before today, no never-touched, act not deleted, visible, active performer (BacklogAging scopes by pairs only, so the rule has to live here too) |
| 04_tvfInsightsForwardPipelineSchedules_v2.sql | tvfInsightsForwardPipelineSchedules | Open only, no never-touched, due today counts as upcoming |
| 10-16_usp_Insights_Dimension_{Location,Entity,Risk,Nature,Departments,Act,Users}_v2.sql | the 7 windowed dimensions | window gate counts only active due dates |
| 17_usp_Insights_Dimension_Internal_v2.sql | Internal | active due dates; internal obligations need an active performer; overdue before today |
| 18_usp_Insights_Dimension_TimelinessFY_v2.sql | TimelinessFY | closure events only on active due dates |
| 20_usp_Insights_FreeMonthly_LoadFacts_v2.sql | monthly free digest facts | same overdue rule |
| 21_usp_Insights_GoldenInvariants_v2.sql | golden invariants | G-2 checks the v2 overdue rule (only status 1 Open is overdue-eligible); the v1 identity it used to check no longer applies |
| 22_licence_report_status_dictionary_v2.sql | dictionary | Semantic `LicenceReportStatus`: each `Lic_tbl_StatusMaster` id -> the label RegTrack's licence report shows (seeded into dictionary v1 and v2, so re-running 01 keeps it) |
| 23_usp_Insights_Dimension_Licence_v2.sql | usp_Insights_Dimension_Licence | Active / Expired / ... = the licence's latest RegTrack status, one column per label (was: worked out from EndDate) |
| 99_rollback_v2.sql | all of the above | restores the pre-v2 definitions (as deployed 2026-09-29) and makes dictionary v1 current |

Every procedure/function file is built from the definition **deployed on UAT** on 2026-09-29 (not the
repo copy - e.g. repo `sql/05` is known to be stale), with only the edits above; the generator asserted
each edit matched exactly the expected number of times. All files are pure ASCII.

Everything else that reads overdue or the obligation list (BacklogAging, ForwardRisk, EvidenceIntegrity,
ownership, free digests, fixed_holistic) picks the new rules up through these shared functions and the
dictionary - no further file changes needed.

## Proof (read-only, before deploying) - user 11416 / tenant 1285

Written as plain queries against RegTrack's own output (all four statutory tabs, `MGMT`):

| | v2 rules | RegTrack | In both |
|---|---|---|---|
| Q1 2026 (1 Apr - 30 Jun) obligations | 173* | 173 | 173 |
| Overdue due dates, as of 2026-09-29 | 2,926* | 2,926 | 2,926 |

\* 174 and 2,932 before the deleted-act rule was added; the 1 + 6 extra were all under a deleted act.

## Licence status (added 2026-09-29, raised by the head of testing)

RegTrack's licence report (`SP_LicenseMyReport_V2`, the Excel testers use) shows each licence's latest status
(the row at MAX(CreatedOn), `RecentLicenseTransactionView`) through a CASE on the status name. v1 decided
Active / Lapsed from EndDate instead, so tenant 1285's Transport showed 9 Active where RegTrack shows 5.
v2 counts RegTrack's label per licence (mapped by status id in the dictionary - the names have whitespace
duplicates). Rows: `ActiveLicences, Expiring, Expired, Applied, PendingForReview, Rejected,
ApplicationRejected, Terminated, NotApplicable, OtherStatus` (sum = `TotalLicences`, THROW 51163 otherwise),
`EndingNext30` (date-based, unchanged rule), `ExpiredPct`. Totals: `TenantActiveLicences`,
`TenantExpiredLicences`, `TenantExpiredPct`. Removed: `Lapsed`, `ExcludedTerminalState`, `LapsingNext30`,
`LapsedPct`, `TenantLapsedPct`, `ExcludedTerminalStateLicences`.

Proof (session temp proc, before deploying): 14 users on tenants 1285, 1355, 5 and 23 - every
(licence type x status) cell equal to RegTrack's own report. 1285 / user 11416: 180 licences, Transport
Active 5, Expired 2, Applied 24, PendingForReview 38, Not Applicable 15, Rejected 7, Terminated 3,
Application Rejected 3 - identical to the Excel.

Install order: 22 then 23 (22 must run after 01).

## Consequences

- Overdue counts and rates drop everywhere (tenant 1285 backlog: 3,846 -> 2,926).
- Obligations with no active performer no longer exist in any dimension, so every "no owner" /
  ownerless figure now reads 0 (prompts still mention it - to be decided).
- Weekly/monthly free digests change the same way.
