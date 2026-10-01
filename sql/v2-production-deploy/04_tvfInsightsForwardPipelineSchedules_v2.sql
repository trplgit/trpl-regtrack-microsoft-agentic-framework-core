/*===========================================================================
  RegTrack Insights - v2 (2026-09-29): follow RegTrack's own Detailed Report
  Object : dbo.tvfInsightsForwardPipelineSchedules
  Base   : the definition DEPLOYED on UAT on 2026-09-29 (not the repo copy)
  Change : upcoming = latest status Open only (no never-touched), and due today is included as upcoming.
  Why    : product decision 2026-09-29 - Insights must count what RegTrack's own
           Detailed Report (Kendo_DetailedReport_Pagination) counts. See
           sql/v2/README.md for the full rule list and the parity proof.
===========================================================================*/
SET NOCOUNT ON;
GO
IF OBJECT_ID('dbo.tvfInsightsForwardPipelineSchedules', 'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsForwardPipelineSchedules;
GO
CREATE FUNCTION dbo.tvfInsightsForwardPipelineSchedules (@CustomerID INT, @AsOf DATETIME)
RETURNS TABLE
AS
RETURN
(
    /*  Mirror of dbo.tvfInsightsOverdueSchedules in the opposite direction:
        same estate definition, ScheduleOn flips from "<= @AsOf" to a forward
        90-day window. A schedule is "still open" when its latest status is
        overdue-eligible OR it has never been touched.                        */
    SELECT
        i.ID                        AS ComplianceInstanceID,
        cso.ID                      AS ComplianceScheduleOnID,
        i.CustomerBranchID,
        a.ComplianceCategoryId      AS CategoryId,
        lt.StatusId,
        cso.ScheduleOn,
        CAST(CASE WHEN lt.ID IS NULL THEN 1 ELSE 0 END AS BIT) AS NeverTouched
    FROM ComplianceScheduleOn cso
    JOIN ComplianceInstance   i   ON i.ID  = cso.ComplianceInstanceID AND i.IsDeleted = 0
    JOIN CustomerBranch       cb  ON cb.ID = i.CustomerBranchID
                                 AND cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1
    JOIN Compliance           c   ON c.ID  = i.ComplianceID AND c.IsDeleted = 0
    JOIN Act                  a   ON a.ID  = c.ActID
    /*  [PERF] explicit latest-status seek on IX_CT_CSO_Dated_ID - never the view */
    OUTER APPLY (SELECT TOP 1 t.StatusId, t.ID
                 FROM ComplianceTransaction t
                 WHERE t.ComplianceScheduleOnID = cso.ID
                 ORDER BY t.Dated DESC, t.ID DESC) lt
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = lt.StatusId
    WHERE cso.IsActive  = 1
      AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn >= CAST(@AsOf AS DATE)   -- [v2] due today is upcoming (RegTrack DueToday), not overdue
      AND cso.ScheduleOn <= DATEADD(DAY, 90, @AsOf)
      AND d.OverdueEligible = 1   -- [v2] Open only; RegTrack does not list due dates with no transaction
);
GO
PRINT 'tvfInsightsForwardPipelineSchedules (v2) installed.';
GO