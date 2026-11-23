/*===========================================================================
  RegTrack Insights - v3 (2026-11-03): Detailed Report parity, Risk step
  Object : dbo.tvfInsightsScopedInstances
  Base   : sql/v2/36_tvfInsightsScopedInstances_eventflag_v2.sql (byte-identical to the definition DEPLOYED on demo 2026-10-29)
  Ref    : Kendo_DetailedReport_Details_Pagination_2 as DEPLOYED on demo
           (modified 2026-10-29 19:54), tabs Statutory + StatutoryChecklist, MGMT.
  Change [FIX 2] RiskType = COALESCE(i.Risk, c.RiskType). A compliance instance
           can carry its own risk (ComplianceInstance.Risk) that overrides the
           compliance's default; RegTrack's report selects
           COALESCE(CI.Risk, CM.RiskType). Every consumer of RiskType moves with
           it (Risk, ForwardRisk, free-tier loaders).
         [FIX 3] Performer only has to be a NOT-DELETED user of the tenant.
           RegTrack commented out its own "AND C.IsActive = 1" on #tempUser, so a
           due date whose performer was deactivated still appears in the report
           (prod example: tenant 1300, Amrut Patil, user 75234, deactivated
           2026-08-27, due date 53111548). Removing IsActive = 1 here brings back
           exactly those due dates; IsDeleted = 0 and CustomerID stay.
  Install: CREATE OR ALTER (keeps existing GRANTs; earlier files used DROP + CREATE).
  Verify : sql/v6-detailed-report-parity-risk/README.md
===========================================================================*/
SET NOCOUNT ON;
GO
CREATE OR ALTER FUNCTION dbo.tvfInsightsScopedInstances (@UserID INT, @CustomerID INT)
RETURNS TABLE
AS
RETURN
(
    SELECT
        i.ID                    AS ComplianceInstanceID,
        i.CustomerBranchID      AS BranchID,
        a.ComplianceCategoryId  AS CategoryId,
        i.ComplianceID,
        COALESCE(i.Risk, c.RiskType) AS RiskType,  -- [FIX 2] instance risk wins (DR: COALESCE(CI.Risk, CM.RiskType))
        c.Imprisonment,
        c.NatureOfCompliance,
        c.ComplianceType,
        a.ID                    AS ActID
    FROM ComplianceInstance i
    JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID
    JOIN Compliance     c  ON c.ID  = i.ComplianceID
    JOIN Act            a  ON a.ID  = c.ActID
    -- 2-D scope constraint: BOTH branch AND category must match
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp
         ON sp.BranchID   = i.CustomerBranchID
        AND sp.CategoryId = a.ComplianceCategoryId
    WHERE cb.CustomerID = @CustomerID
      AND cb.IsDeleted = 0 AND cb.Status = 1
      AND i.IsDeleted   = 0
      AND c.IsDeleted   = 0
      -- [v2 2026-09-29] RegTrack parity: act not deleted, compliance visible, active performer
      AND a.IsDeleted   = 0
      AND (c.ComplinceVisible = 1 OR c.ComplinceVisible IS NULL)   -- [TRAP] column misspelled in the schema
      -- [ADDED 2026-10-01] RegTrack excludes EventFlag=1 compliance from both 'Statutory' and
      -- 'Statutory Checklist' (it becomes 'Event Based'/'Event Based Checklist' instead) - see
      -- this file's own header for the real RegTrack SP reference and the live tenant proof.
      AND c.EventFlag IS NULL
      AND EXISTS (SELECT 1 FROM ComplianceAssignment ca
                  JOIN [User] u ON u.ID = ca.UserID
                  WHERE ca.ComplianceInstanceID = i.ID AND ca.RoleID = 3
                    AND u.CustomerID = @CustomerID AND u.IsDeleted = 0)  -- [FIX 3] IsActive dropped (DR commented it out)
);
GO
PRINT 'tvfInsightsScopedInstances (v3, Detailed Report parity) installed.';
GO
