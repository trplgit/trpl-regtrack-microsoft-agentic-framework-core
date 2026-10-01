/*===========================================================================
  RegTrack Insights - v2 (2026-09-29): follow RegTrack's own Detailed Report
  Object : dbo.usp_Insights_GoldenInvariants
  Base   : the definition DEPLOYED on UAT on 2026-09-29 (not the repo copy)
  Change : G-2 now checks the v2 overdue rule (only status 1 Open is overdue-eligible) instead of the v1 identity; never-touched due dates are reported, not counted.
  Why    : product decision 2026-09-29 - Insights must count what RegTrack's own
           Detailed Report (Kendo_DetailedReport_Pagination) counts. See
           sql/v2/README.md for the full rule list and the parity proof.
===========================================================================*/
SET NOCOUNT ON;
GO
IF OBJECT_ID('dbo.usp_Insights_GoldenInvariants', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_GoldenInvariants;
GO
CREATE PROCEDURE dbo.usp_Insights_GoldenInvariants
    @CustomerID INT,
    @AsOf       DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*  SCOPE OF THIS SUITE: STRUCTURAL INVARIANTS ONLY.

        Every assertion here must hold REGARDLESS of what the data contains -
        they are algebra and graph traversal, not observations about a
        particular dataset. That is why they can THROW: a failure means the
        CODE is wrong.

        Data-sanity checks (e.g. "imprisonment items should concentrate on
        RiskType 3") do NOT belong here. They can legitimately fail on
        legitimate data - a test environment with arbitrary values will fail
        them forever, and a permanently-red suite gets disabled, which costs
        far more than the check was worth. Those live in
        usp_Insights_StatusDataQuality and WARN.                              */

    DECLARE @results TABLE (TestId VARCHAR(10), TestName NVARCHAR(140),
                            Passed BIT, Detail NVARCHAR(400));

    /*-- G-1  dictionary coverage ------------------------------------------*/
    DECLARE @unmapped INT = (SELECT COUNT(*) FROM ComplianceStatus cs
        WHERE NOT EXISTS (SELECT 1 FROM dbo.vInsightsStatusCurrent d WHERE d.StatusId=cs.ID));
    INSERT @results VALUES ('G-1', N'All ComplianceStatus values are mapped',
        CASE WHEN @unmapped=0 THEN 1 ELSE 0 END,
        CONCAT(N'Unmapped status count = ', @unmapped, N' (must be 0)'));

    /*-- G-2  overdue definition invariant ---------------------------------
        [v2 2026-09-29] Dictionary v2 (RegTrack parity): the ONLY overdue-eligible
        status is 1 (Open). Checked from both sides in the same instant: the
        dictionary flags exactly one status, it is status 1, and the rows it
        flags equal the rows whose latest status is Open.                     */
    /*  [PERF] Latest status is resolved ONCE into #latest and reused by G-2,
        G-3 and G-9. Three separate joins to RecentComplianceTransactionView
        timed out in production (45.7M-row non-indexed view; tenant filter not
        pushed through). The explicit function runs in ~0.6 s for 52K schedules. */
    IF OBJECT_ID('tempdb..#latest') IS NOT NULL DROP TABLE #latest;
    SELECT ls.ComplianceScheduleOnID, ls.ComplianceInstanceID, ls.StatusId, ls.ScheduleOn,
           ls.LatestTransactionId, d.OverdueEligible, d.ClosureClass
    INTO #latest
    FROM dbo.tvfInsightsLatestStatus(@CustomerID, @AsOf) ls
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = ls.StatusId;

    /*  Overdue now has two sources (see tvfInsightsOverdueSchedules):
          (a) overdue-eligible latest status   - the identity below holds for these
          (b) no transaction at all            - BA ruling; added as a separate term
        The identity is checked over (a) only, then (b) is added to both sides,
        so it stays an exact algebraic check rather than being loosened.       */
    DECLARE @old INT,@new INT,@c79 INT,@c17 INT,@c18 INT,@never INT;
    SELECT @old = SUM(CASE WHEN StatusId NOT IN (4,5,15,18) THEN 1 ELSE 0 END),
           @new = SUM(CASE WHEN OverdueEligible=1 THEN 1 ELSE 0 END),
           @c79 = SUM(CASE WHEN StatusId IN (7,9) THEN 1 ELSE 0 END),
           @c17 = SUM(CASE WHEN StatusId=17 THEN 1 ELSE 0 END),
           @c18 = SUM(CASE WHEN StatusId=18 THEN 1 ELSE 0 END)
    FROM #latest WHERE StatusId IS NOT NULL;
    SET @never = (SELECT COUNT(*) FROM #latest WHERE LatestTransactionId IS NULL);
    INSERT @results VALUES ('G-2', N'Overdue = latest status Open only (dictionary v2, RegTrack parity)',
        CASE WHEN ISNULL(@new,0) = (SELECT COUNT(*) FROM #latest WHERE StatusId = 1)
              AND (SELECT COUNT(*) FROM dbo.vInsightsStatusCurrent WHERE OverdueEligible = 1) = 1
              AND (SELECT COUNT(*) FROM dbo.vInsightsStatusCurrent WHERE OverdueEligible = 1 AND StatusId = 1) = 1
             THEN 1 ELSE 0 END,
        CONCAT(N'overdue-eligible latest statuses=', ISNULL(@new,0), N' | latest status Open=',
               (SELECT COUNT(*) FROM #latest WHERE StatusId = 1),
               N' | never-touched (not overdue in v2)=', ISNULL(@never,0)));

    /*-- G-3  completed items are never overdue ----------------------------*/
    DECLARE @compOvd INT = (SELECT COUNT(*) FROM #latest WHERE OverdueEligible=1 AND ClosureClass='completed');
    INSERT @results VALUES ('G-3', N'No completed item is counted overdue',
        CASE WHEN @compOvd=0 THEN 1 ELSE 0 END,
        CONCAT(N'Completed-but-overdue rows = ',@compOvd,N' (must be 0)'));

    /*-- G-4  resolved_terminal carries no timeliness ----------------------*/
    DECLARE @termTime INT = (SELECT COUNT(*) FROM dbo.vInsightsStatusCurrent
        WHERE ClosureClass='resolved_terminal' AND Timeliness IS NOT NULL);
    INSERT @results VALUES ('G-4', N'resolved_terminal carries no timeliness',
        CASE WHEN @termTime=0 THEN 1 ELSE 0 END,
        CONCAT(N'resolved_terminal rows with timeliness = ',@termTime,N' (must be 0)'));

    /*-- G-5  recursive rollup ties to the control total -------------------
        [FIX - found in UAT] The control total MUST use the same estate
        definition as the dimension procedures, which exclude instances whose
        Compliance master is soft-deleted. Without the c.IsDeleted filter this
        reported 4,884 against a dimension total of 4,814 on a real tenant - a
        70-instance disagreement between two parts of the same system, with
        each side reconciling internally, which is what made it invisible.  */
    DECLARE @control INT,@rollup INT;
    SELECT @control=COUNT(*)
    FROM ComplianceInstance i
    JOIN CustomerBranch cb ON cb.ID=i.CustomerBranchID
    JOIN Compliance c ON c.ID=i.ComplianceID
    WHERE cb.CustomerID=@CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1 AND i.IsDeleted=0 AND c.IsDeleted=0;

    ;WITH tree AS (SELECT BranchID FROM dbo.tvfInsightsEntityTree(@CustomerID))
    SELECT @rollup=COUNT(i.ID)
    FROM tree
    LEFT JOIN ComplianceInstance i ON i.CustomerBranchID=tree.BranchID AND i.IsDeleted=0
    LEFT JOIN Compliance c ON c.ID=i.ComplianceID AND c.IsDeleted=0
    WHERE i.ID IS NULL OR c.ID IS NOT NULL;

    INSERT @results VALUES ('G-5', N'Recursive rollup ties to tenant control total',
        CASE WHEN ISNULL(@control,0)=ISNULL(@rollup,0) THEN 1 ELSE 0 END,
        CONCAT(N'control=',ISNULL(@control,0),N' rollup=',ISNULL(@rollup,0),
               N' gap=',ISNULL(@control,0)-ISNULL(@rollup,0),
               N' (a gap means a node was dropped)'));

    /*-- G-6 REMOVED - moved to usp_Insights_StatusDataQuality as a WARNING.
        It asserted that imprisonment items concentrate on RiskType 3. That is
        TRUE of production (98.7% across 528 tenants) but is a property of the
        DATA, not the code. A test environment with arbitrary values inverted
        it completely (96.8% on RiskType 0) and turned this whole suite red
        permanently - which is how regression suites get switched off.      */

    /*-- G-7  scope is two-dimensional ------------------------------------*/
    DECLARE @scopeRows INT,@scopeNoCat INT;
    SELECT @scopeRows=COUNT(*),
           @scopeNoCat=SUM(CASE WHEN ea.ComplianceCatagoryID IS NULL OR ea.ComplianceCatagoryID=0 THEN 1 ELSE 0 END)
    FROM EntitiesAssignment ea JOIN CustomerBranch cb ON cb.ID=ea.BranchID
    WHERE cb.CustomerID=@CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1;
    INSERT @results VALUES ('G-7', N'All scope rows are category-specific (2-D)',
        CASE WHEN ISNULL(@scopeRows,0)=0 OR ISNULL(@scopeNoCat,0)=0 THEN 1 ELSE 0 END,
        CONCAT(N'scope rows=',ISNULL(@scopeRows,0),N' without category=',ISNULL(@scopeNoCat,0)));

    /*-- G-8  2-D scope never returns MORE than branch-only ----------------*/
    DECLARE @s2d INT,@sbr INT;
    ;WITH pairs AS (
        SELECT DISTINCT ea.BranchID, ea.ComplianceCatagoryID AS CategoryId
        FROM EntitiesAssignment ea JOIN CustomerBranch cb ON cb.ID=ea.BranchID
        WHERE cb.CustomerID=@CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1),
    inst AS (
        SELECT i.ID, i.CustomerBranchID AS BranchID, a.ComplianceCategoryId AS CategoryId
        FROM ComplianceInstance i
        JOIN CustomerBranch cb ON cb.ID=i.CustomerBranchID AND cb.IsDeleted = 0 AND cb.Status = 1
        JOIN Compliance c ON c.ID=i.ComplianceID AND c.IsDeleted=0
        JOIN Act a ON a.ID=c.ActID
        WHERE cb.CustomerID=@CustomerID AND i.IsDeleted=0)
    SELECT @s2d=(SELECT COUNT(DISTINCT i.ID) FROM pairs p JOIN inst i
                   ON i.BranchID=p.BranchID AND i.CategoryId=p.CategoryId),
           @sbr=(SELECT COUNT(DISTINCT i.ID) FROM (SELECT DISTINCT BranchID FROM pairs) p
                   JOIN inst i ON i.BranchID=p.BranchID);
    INSERT @results VALUES ('G-8', N'2-D scope never returns more than branch-only',
        CASE WHEN ISNULL(@s2d,0)<=ISNULL(@sbr,0) THEN 1 ELSE 0 END,
        CONCAT(N'2-D=',ISNULL(@s2d,0),N' branch-only=',ISNULL(@sbr,0),
               N' | branch-only would leak ',ISNULL(@sbr,0)-ISNULL(@s2d,0),N' instances'));

    /*-- G-9  unknown-status volume is immaterial --------------------------*/
    /*  G-9 measures UNKNOWN status among schedules that HAVE a transaction. A
        schedule with no transaction at all is an absence, not an unknown, and
        is declared by usp_Insights_StatusDataQuality instead. Conflating the
        two made this invariant fail on 115 bulk-created, never-touched
        schedules that the old view had silently hidden.                     */
    DECLARE @pdAll INT,@nullSt INT;
    SELECT @pdAll=COUNT(*), @nullSt=SUM(CASE WHEN StatusId IS NULL THEN 1 ELSE 0 END)
    FROM #latest WHERE LatestTransactionId IS NOT NULL;
    INSERT @results VALUES ('G-9', N'Unknown-status volume is immaterial and declared',
        CASE WHEN ISNULL(@pdAll,0)=0 OR ISNULL(@nullSt,0)*100.0/@pdAll<=0.10 THEN 1 ELSE 0 END,
        CONCAT(N'NULL-status past-due rows = ',ISNULL(@nullSt,0),N' of ',ISNULL(@pdAll,0)));

    DROP TABLE #latest;

    SELECT TestId, TestName,
           CASE WHEN Passed=1 THEN 'PASS' ELSE '*** FAIL ***' END AS Result, Detail
    FROM @results ORDER BY TestId;

    IF EXISTS (SELECT 1 FROM @results WHERE Passed=0)
        THROW 51002, N'GOLDEN REGRESSION FAILED - do not ship. See result set for the failing assertion.', 1;
END
GO
PRINT 'usp_Insights_GoldenInvariants (v2) installed.';
GO