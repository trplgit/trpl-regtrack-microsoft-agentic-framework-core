/*===========================================================================
  RegTrack Insights - v2 (2026-10-01): EventFlag parity fix
  Object : dbo.tvfInsightsScopedInstances
  Base   : the definition DEPLOYED on UAT (sql/v2/02, 2026-09-29 RegTrack-parity revision)
  Change : excludes Compliance.EventFlag = 1 (event-triggered compliance).

  Why: confirmed against the real RegTrack SP (Kendo_DetailedReport_Details_Pagination_2.sql,
  D:\trpl-reginsights-dev\regtrack-report-sps\) - its own classification:
      ComplianceType=0, EventFlag IS NULL -> 'Statutory'
      ComplianceType=0, EventFlag=1       -> 'Event Based'
      ComplianceType=1, EventFlag IS NULL -> 'Statutory Checklist'
      ComplianceType=1, EventFlag=1       -> 'Event Based Checklist'
  and its own filter for a 'Statutory'/'StatutoryChecklist' report requires
  `EventFlag IS NULL` in both cases. tvfInsightsScopedInstances (the shared base
  every real dimension - Location/Risk/Nature/Departments/Act/Users/Entity -
  reads through) had no EventFlag filter at all, so event-triggered compliance
  (e.g. POSH complaint-handling clauses, real but conditional/event-driven) was
  silently counted as ordinary statutory obligations.

  Found live, tenant 1285 (Adi Demo Customer), Q1 FY26-27: 31 of 173 Location-
  scoped instances (17.9%) were EventFlag=1 - concentrated on two branches
  (Maharashtra 19, Sikkim 9), both of which disagreed badly with RegTrack's own
  Detailed Report export until this fix. Verified after the fix: all 18
  obligation-carrying branches match RegTrack's real export exactly.

  This does NOT touch ComplianceType (Checklist stays included on purpose -
  RegTrack's own 'Statutory, Statutory CheckList' filter combines both).

  Error block: none (this function throws nothing; see sql/v2/02's own header).
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
      -- [ADDED 2026-10-01] RegTrack excludes EventFlag=1 compliance from both 'Statutory' and
      -- 'Statutory Checklist' (it becomes 'Event Based'/'Event Based Checklist' instead) - see
      -- this file's own header for the real RegTrack SP reference and the live tenant proof.
      AND c.EventFlag IS NULL
      AND EXISTS (SELECT 1 FROM ComplianceAssignment ca
                  JOIN [User] u ON u.ID = ca.UserID
                  WHERE ca.ComplianceInstanceID = i.ID AND ca.RoleID = 3
                    AND u.CustomerID = @CustomerID AND u.IsDeleted = 0 AND u.IsActive = 1)
);
GO
PRINT 'tvfInsightsScopedInstances (v2, EventFlag fix) installed.';
GO
