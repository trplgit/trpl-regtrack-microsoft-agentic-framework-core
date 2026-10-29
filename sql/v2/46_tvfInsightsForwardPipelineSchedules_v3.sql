/*===========================================================================
  RegTrack Insights - v3 (2026-10-27): forward pipeline = same estate as
  every other dimension
  Object : dbo.tvfInsightsForwardPipelineSchedules
  Base   : sql/v2/04 (identical to what production runs, checked 2026-10-27).
  Change : adds the four RegTrack Detailed Report rules the shared scope
           function (sql/v2/36 tvfInsightsScopedInstances) already applies
           and this function never had:
             - Act not deleted
             - compliance visible (ComplinceVisible - misspelled in the schema)
             - statutory only: EventFlag IS NULL (event-triggered compliance is
               RegTrack's Event Based tabs, counted by the Event dimension)
             - an active performer (ComplianceAssignment RoleID 3, active user)
           Without them the "due in the next 90 days" figures counted
           compliances RegTrack's own report does not list - so they could not
           match an export, and disagreed with every other dimension.
  Only caller: dbo.usp_Insights_Dimension_ForwardPipeline (checked on the
  production replica, sys.sql_modules, 2026-10-27).
===========================================================================*/
SET NOCOUNT ON;
GO
CREATE OR ALTER FUNCTION dbo.tvfInsightsForwardPipelineSchedules (@CustomerID INT, @AsOf DATETIME)
RETURNS TABLE
AS
RETURN
(
    /*  Due dates in the next 90 days (today included - RegTrack's DueToday is
        upcoming, not overdue) whose latest status is Open, over the same estate
        as tvfInsightsScopedInstances. The caller adds the user's (branch,
        category) scope pairs. */
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
      -- [v3 2026-10-27] the same estate as sql/v2/36 tvfInsightsScopedInstances
      AND a.IsDeleted = 0
      AND (c.ComplinceVisible = 1 OR c.ComplinceVisible IS NULL)   -- [TRAP] column misspelled in the schema
      AND c.EventFlag IS NULL
      AND EXISTS (SELECT 1 FROM ComplianceAssignment ca
                  JOIN [User] u ON u.ID = ca.UserID
                  WHERE ca.ComplianceInstanceID = i.ID AND ca.RoleID = 3
                    AND u.CustomerID = @CustomerID AND u.IsDeleted = 0 AND u.IsActive = 1)
);
GO
PRINT 'tvfInsightsForwardPipelineSchedules (v3, same estate as tvfInsightsScopedInstances) installed.';
GO
