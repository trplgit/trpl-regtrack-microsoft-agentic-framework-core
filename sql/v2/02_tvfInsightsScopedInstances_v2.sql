/*===========================================================================
  RegTrack Insights - v2 (2026-09-29): follow RegTrack's own Detailed Report
  Object : dbo.tvfInsightsScopedInstances
  Base   : the definition DEPLOYED on UAT on 2026-09-29 (not the repo copy)
  Change : an obligation counts only if its act is not deleted, the compliance is visible, and it has an active performer (ComplianceAssignment RoleID 3, active non-deleted user of this tenant).
  Why    : product decision 2026-09-29 - Insights must count what RegTrack's own
           Detailed Report (Kendo_DetailedReport_Pagination) counts. See
           sql/v2/README.md for the full rule list and the parity proof.
===========================================================================*/
SET NOCOUNT ON;
GO
IF OBJECT_ID('dbo.tvfInsightsScopedInstances', 'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsScopedInstances;
GO
CREATE FUNCTION dbo.tvfInsightsScopedInstances (@UserID INT, @CustomerID INT)
RETURNS TABLE
AS
RETURN
(
    SELECT
        i.ID                    AS ComplianceInstanceID,
        i.CustomerBranchID      AS BranchID,
        a.ComplianceCategoryId  AS CategoryId,
        i.ComplianceID,
        c.RiskType,
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
      AND EXISTS (SELECT 1 FROM ComplianceAssignment ca
                  JOIN [User] u ON u.ID = ca.UserID
                  WHERE ca.ComplianceInstanceID = i.ID AND ca.RoleID = 3
                    AND u.CustomerID = @CustomerID AND u.IsDeleted = 0 AND u.IsActive = 1)
);
GO
PRINT 'tvfInsightsScopedInstances (v2) installed.';
GO