/*===========================================================================
  RegTrack Insights - v2 (2026-09-29): follow RegTrack's own Detailed Report
  Object : dbo.tvfInsightsOverdueSchedules
  Base   : the definition DEPLOYED on UAT on 2026-09-29 (not the repo copy)
  Change : overdue = latest status Open (dictionary v2) and due strictly before today; a due date with no transaction is no longer overdue; act not deleted; compliance visible.
  Why    : product decision 2026-09-29 - Insights must count what RegTrack's own
           Detailed Report (Kendo_DetailedReport_Pagination) counts. See
           sql/v2/README.md for the full rule list and the parity proof.
===========================================================================*/
SET NOCOUNT ON;
GO
IF OBJECT_ID('dbo.tvfInsightsOverdueSchedules', 'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsOverdueSchedules;
GO
CREATE FUNCTION dbo.tvfInsightsOverdueSchedules (@CustomerID INT, @AsOf DATETIME)
RETURNS TABLE
AS
RETURN
(
    /*  The canonical overdue predicate. Built on tvfInsightsLatestStatus.

        [v2 2026-09-29] Same rule as RegTrack's own Detailed Report
        (Kendo_DetailedReport_Pagination): a due date is 'Overdue' when its
        latest status is Open (dictionary v2 flags only status 1) AND it is due
        strictly before today. Due today is RegTrack's 'DueToday', not overdue.
        A due date with NO transaction at all is not overdue - RegTrack does not
        list it (replaces the earlier "never-touched = overdue" rule). Its
        obligation must have an active performer, as in RegTrack.

        Still FAIL-CLOSED on unknown: a status absent from the dictionary never
        joins, so it is excluded rather than guessed.                          */
    SELECT
        ls.ComplianceInstanceID,
        ls.ComplianceScheduleOnID,
        ls.CustomerBranchID,
        a.ComplianceCategoryId      AS CategoryId,
        ls.StatusId,
        ls.ScheduleOn,
        CAST(0 AS BIT) AS NeverTouched   -- [v2] never-touched due dates are no longer overdue (RegTrack does not list them)
    FROM dbo.tvfInsightsLatestStatus(@CustomerID, @AsOf) ls
    JOIN Compliance c ON c.ID = ls.ComplianceID          -- IsDeleted already applied upstream
    JOIN Act        a ON a.ID = c.ActID AND a.IsDeleted = 0
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = ls.StatusId
    -- [v2 2026-09-29] RegTrack: 'Overdue' = latest status Open (dictionary v2 flags only status 1)
    -- AND due strictly before today (due today is 'DueToday', not overdue).
    WHERE d.OverdueEligible = 1
      AND ls.ScheduleOn < CAST(@AsOf AS DATE)
      AND (c.ComplinceVisible = 1 OR c.ComplinceVisible IS NULL)
      -- [v2] RegTrack lists a due date only when its obligation has an active performer.
      -- Here as well as in tvfInsightsScopedInstances: some callers (BacklogAging) scope
      -- by (branch, category) pairs only and never read the scoped-instance function.
      AND EXISTS (SELECT 1 FROM ComplianceAssignment ca
                  JOIN [User] u ON u.ID = ca.UserID
                  WHERE ca.ComplianceInstanceID = ls.ComplianceInstanceID AND ca.RoleID = 3
                    AND u.CustomerID = @CustomerID AND u.IsDeleted = 0 AND u.IsActive = 1)
);
GO
PRINT 'tvfInsightsOverdueSchedules (v2) installed.';
GO