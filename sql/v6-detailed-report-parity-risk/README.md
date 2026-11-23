# Detailed Report parity - 3 Nov 2026 (demo only so far)

Goal: every RegInsights Pro dimension counts exactly what RegTrack's own reports count
(Detailed Report = `dbo.Kendo_DetailedReport_Details_Pagination_2`, Licence report =
`dbo.SP_LicenseMyReport_V2`).

The SQL files live in `sql/v2/50` to `sql/v2/63` (install) and `sql/v2/99_rollback_v2_*.sql`
(rollback). They are not copied here, because the files refer to each other by those paths.

## Status (checked 2026-11-23, read-only)

| Environment | Status |
|---|---|
| DEMO | All 14 objects deployed 3 Nov 2026 (13:46 - 15:24 IST). Live definitions match these files byte for byte. |
| UAT | Not deployed. Not checked directly (no read access from the tool). |
| PROD | NOT deployed. Do not run the demo files on prod as they are (see below). |

## The 14 objects, install order (demo order)

50 tvfInsightsScopedInstances, 51 tvfInsightsOverdueSchedules, 52 usp_Insights_Dimension_Risk,
53 usp_Insights_Dimension_Nature, 54 usp_Insights_Dimension_Internal, 55 usp_Insights_Dimension_Event,
56 usp_Insights_Dimension_Location, 57 usp_Insights_Dimension_Entity, 58 usp_Insights_Dimension_Departments,
59 usp_Insights_Dimension_Act, 60 usp_Insights_Dimension_Users, 61 tvfInsightsForwardPipelineSchedules,
62 usp_Insights_Dimension_BacklogAging, 63 usp_Insights_Dimension_Licence.

Rollback: run in reverse (63 first, 50_52 last). Each rollback restores the DEMO definition from before 3 Nov.

## Why the demo files cannot go to prod as they are (prod compared 2026-11-23)

The demo files were built from the definition on DEMO. Prod is not the same as demo for 7 objects.

Prod is identical to the demo "before" version (safe to replace):
tvfInsightsOverdueSchedules, tvfInsightsForwardPipelineSchedules, usp_Insights_Dimension_Risk,
usp_Insights_Dimension_Nature, usp_Insights_Dimension_Internal, usp_Insights_Dimension_Event,
usp_Insights_Dimension_Licence.

Prod has DIFFERENT code (someone changed it by hand / for speed; not in git). Replacing these
with the demo files would overwrite that work:
tvfInsightsScopedInstances (EventFlag line commented out, u.IsActive = 1 kept),
usp_Insights_Dimension_Location, usp_Insights_Dimension_Entity, usp_Insights_Dimension_Departments,
usp_Insights_Dimension_Users, usp_Insights_Dimension_Act, usp_Insights_Dimension_BacklogAging.
These prod versions build their own temp tables instead of calling tvfInsightsScopedInstances.
They still call tvfInsightsOverdueSchedules.

Side effect if only part is installed: tvfInsightsOverdueSchedules (Fix 3: deactivated performers
count) is used by Location, Entity, Departments, Users and BacklogAging. If it changes but their
own scope logic (still IsActive = 1) does not, overdue and scope disagree.

tvfInsightsScopedInstances is also used by AuditScope, tvfInsightsOwnership, CoverageGaps,
EvidenceIntegrity, ForwardRisk, TimelinessFY and FreeDigestAggregates (the free weekly digest), so
Fix 2 and Fix 3 change those numbers too.

Also on prod: tvfInsightsLatestStatus was edited by hand (extra join to ComplianceAssignment,
RoleID = 3). It is not part of this package and was not touched.

## What can be deployed to prod on its own

Only objects that do not depend on the helper functions and that are identical on prod:
55 usp_Insights_Dimension_Event and 63 usp_Insights_Dimension_Licence.
Everything else needs a rebuild from the PROD definitions first (prod-specific files), then a
check against the Detailed Report on prod (read-only), before going live with the other dimensions.
