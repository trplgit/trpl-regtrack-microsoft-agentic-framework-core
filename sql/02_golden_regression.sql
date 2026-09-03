/*===========================================================================
  RegTrack Insights - Phase 1a, Step 2
  GOLDEN-DATASET REGRESSION SUITE

  Spec reference : RegTrack_Insights_System_Design_v1.md Sec.6.6
  Purpose        : Catch a dictionary or procedure edit that silently moves a
                   status across buckets, BEFORE it ships.

  -- DESIGN REFINEMENT (important, supersedes the simpler Sec.6.6 sketch) -------
  Overdue is a FLOW metric and drifts continuously against live production
  (observed: 1,387 -> 1,398 -> 1,116 on identical SQL within one session).
  Therefore a regression test CANNOT assert an absolute overdue count against
  production - it would fail randomly and be switched off, which is worse than
  no test at all.

  Two tiers instead:

    TIER 1 - PRODUCTION INVARIANTS (this file, Sec.A)
             Relationship assertions that hold regardless of drift, because
             both sides of the equation move together. Run against live prod.

    TIER 2 - FROZEN FIXTURES (this file, Sec.B - seed script for a test database)
             Deterministic rows with hand-verified expected values. Run in CI.

  Run TIER 1 nightly against production and in CI before any dictionary change.
  Run TIER 2 on every build.
===========================================================================*/

SET NOCOUNT ON;
GO

/*===========================================================================
  Sec.A - TIER 1: PRODUCTION INVARIANTS (drift-proof)
===========================================================================*/

IF OBJECT_ID('dbo.usp_Insights_GoldenInvariants', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_GoldenInvariants;
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
        new = old - pastdue(7,9) - pastdue(17) + pastdue(18)
        Drift-proof: every term is measured in the same instant.            */
    DECLARE @old INT,@new INT,@c79 INT,@c17 INT,@c18 INT;
    SELECT @old = SUM(CASE WHEN rct.ComplianceStatusID NOT IN (4,5,15,18) THEN 1 ELSE 0 END),
           @new = SUM(CASE WHEN d.OverdueEligible=1 THEN 1 ELSE 0 END),
           @c79 = SUM(CASE WHEN rct.ComplianceStatusID IN (7,9) THEN 1 ELSE 0 END),
           @c17 = SUM(CASE WHEN rct.ComplianceStatusID=17 THEN 1 ELSE 0 END),
           @c18 = SUM(CASE WHEN rct.ComplianceStatusID=18 THEN 1 ELSE 0 END)
    FROM ComplianceScheduleOn cso
    JOIN ComplianceInstance i ON i.ID=cso.ComplianceInstanceID
    JOIN CustomerBranch cb ON cb.ID=i.CustomerBranchID
    JOIN RecentComplianceTransactionView rct ON rct.ComplianceScheduleOnID=cso.ID
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId=rct.ComplianceStatusID
    WHERE cb.CustomerID=@CustomerID AND cb.IsDeleted=0 AND cb.Status=1 AND i.IsDeleted=0
      AND cso.IsActive=1 AND cso.IsUpcomingNotDeleted=1 AND cso.ScheduleOn<=@AsOf;
    INSERT @results VALUES ('G-2', N'Overdue definition invariant (old to new reconciles)',
        CASE WHEN ISNULL(@new,0)=ISNULL(@old,0)-ISNULL(@c79,0)-ISNULL(@c17,0)+ISNULL(@c18,0) THEN 1 ELSE 0 END,
        CONCAT(N'new=',ISNULL(@new,0),N' old=',ISNULL(@old,0),N' pastdue(7,9)=',ISNULL(@c79,0),
               N' pastdue(17)=',ISNULL(@c17,0),N' pastdue(18)=',ISNULL(@c18,0)));

    /*-- G-3  completed items are never overdue ----------------------------*/
    DECLARE @compOvd INT = (SELECT COUNT(*) FROM dbo.tvfInsightsOverdueSchedules(@CustomerID,@AsOf) o
        JOIN dbo.vInsightsStatusCurrent d ON d.StatusId=o.StatusId WHERE d.ClosureClass='completed');
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
    WHERE cb.CustomerID=@CustomerID AND cb.IsDeleted=0 AND cb.Status=1 AND i.IsDeleted=0 AND c.IsDeleted=0;

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
    WHERE cb.CustomerID=@CustomerID AND cb.IsDeleted=0 AND cb.Status=1;
    INSERT @results VALUES ('G-7', N'All scope rows are category-specific (2-D)',
        CASE WHEN ISNULL(@scopeRows,0)=0 OR ISNULL(@scopeNoCat,0)=0 THEN 1 ELSE 0 END,
        CONCAT(N'scope rows=',ISNULL(@scopeRows,0),N' without category=',ISNULL(@scopeNoCat,0)));

    /*-- G-8  2-D scope never returns MORE than branch-only ----------------*/
    DECLARE @s2d INT,@sbr INT;
    ;WITH pairs AS (
        SELECT DISTINCT ea.BranchID, ea.ComplianceCatagoryID AS CategoryId
        FROM EntitiesAssignment ea JOIN CustomerBranch cb ON cb.ID=ea.BranchID
        WHERE cb.CustomerID=@CustomerID AND cb.IsDeleted=0 AND cb.Status=1),
    inst AS (
        SELECT i.ID, i.CustomerBranchID AS BranchID, a.ComplianceCategoryId AS CategoryId
        FROM ComplianceInstance i
        JOIN CustomerBranch cb ON cb.ID=i.CustomerBranchID AND cb.IsDeleted=0 AND cb.Status=1
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
    DECLARE @pdAll INT,@nullSt INT;
    SELECT @pdAll=COUNT(*), @nullSt=SUM(CASE WHEN rct.ComplianceStatusID IS NULL THEN 1 ELSE 0 END)
    FROM ComplianceScheduleOn cso
    JOIN ComplianceInstance i ON i.ID=cso.ComplianceInstanceID
    JOIN CustomerBranch cb ON cb.ID=i.CustomerBranchID
    JOIN RecentComplianceTransactionView rct ON rct.ComplianceScheduleOnID=cso.ID
    WHERE cb.CustomerID=@CustomerID AND cb.IsDeleted=0 AND cb.Status=1 AND i.IsDeleted=0
      AND cso.IsActive=1 AND cso.IsUpcomingNotDeleted=1 AND cso.ScheduleOn<=@AsOf;
    INSERT @results VALUES ('G-9', N'Unknown-status volume is immaterial and declared',
        CASE WHEN ISNULL(@pdAll,0)=0 OR ISNULL(@nullSt,0)*100.0/@pdAll<=0.10 THEN 1 ELSE 0 END,
        CONCAT(N'NULL-status past-due rows = ',ISNULL(@nullSt,0),N' of ',ISNULL(@pdAll,0)));

    SELECT TestId, TestName,
           CASE WHEN Passed=1 THEN 'PASS' ELSE '*** FAIL ***' END AS Result, Detail
    FROM @results ORDER BY TestId;

    IF EXISTS (SELECT 1 FROM @results WHERE Passed=0)
        THROW 51002, N'GOLDEN REGRESSION FAILED - do not ship. See result set for the failing assertion.', 1;
END
GO

/*===========================================================================
  Sec.B - TIER 2: FROZEN FIXTURE DESIGN (for a CI test database)

  Tier 1 proves the definitions are self-consistent against whatever data
  exists. Tier 2 proves they produce the RIGHT ABSOLUTE NUMBERS, which needs
  data that never moves.

  Build a small seeded test database containing exactly the cases below, with
  hand-verified expected values. These are the cases that actually broke, or
  nearly broke, during design.

  ------------------------------------------------------------------------
  | F# | Fixture                                   | Expected                 |
  ------------------------------------------------------------------------
  | F-1| 10 past-due schedules in status 7         | overdue = 0              |
  |    | (completed BEFORE due date)               | on_time completions = 10 |
  ------------------------------------------------------------------------
  | F-2| 10 past-due schedules in status 9         | overdue = 0              |
  |    | (completed AFTER due date)                | delayed completions = 10 |
  |    |  * THE LUCKY-ESCAPE CASE - the reference   |                          |
  |    |    tenants had none of these, so the old   |                          |
  |    |    definition was right by luck            |                          |
  ------------------------------------------------------------------------
  | F-3| 10 past-due schedules in status 2         | overdue = 10             |
  |    | (complied, pending review)                | completions = 0          |
  ------------------------------------------------------------------------
  | F-4| 10 past-due in 15, 10 past-due in 17      | overdue = 0              |
  |    | (reviewer-final NA / not-complied)        | on-time DENOMINATOR      |
  |    |                                           | excludes all 20          |
  ------------------------------------------------------------------------
  | F-5| 10 past-due in 18, 10 past-due in 16      | overdue = 20             |
  |    | (performer-proposed NA / not-complied)    | (proposed => still open)  |
  ------------------------------------------------------------------------
  | F-6| Entity tree: apex -> intermediate(holding  | rollup = 25, NOT 15      |
  |    | 10 instances) -> 2 leaves (10 + 5)         | * leaf-only rollup fails |
  ------------------------------------------------------------------------
  | F-7| A status ID present in data but ABSENT    | usp_..AssertStatus       |
  |    | from the dictionary                       | Coverage THROWS 51001    |
  ------------------------------------------------------------------------
  | F-8| User with EA rows for branch B category C | query for (B, other cat) |
  |    | only                                      | returns 0 rows           |
  ------------------------------------------------------------------------
  | F-9| Soft-deleted branch (IsDeleted=1) holding | excluded from all cuts;  |
  |    | 1 instance                                | reported as orphan       |
  ------------------------------------------------------------------------
  |F-10| Deactivated user (IsActive=0,IsDeleted=0) | user APPEARS, flagged;   |
  |    | holding 5 live assignments                | NOT hidden               |
  ------------------------------------------------------------------------

  Aggregate expected values for the fixture tenant:
      total past-due schedules      = 80
      overdue (dictionary)          = 40   (F-3: 10, F-5: 20, plus F-6 as configured)
      completed                     = 20   (F-1: 10 on_time, F-2: 10 delayed)
      resolved_terminal             = 20   (F-4)
      on-time %                     = 50.0 (10 of 20 completed - NOT 10 of 40)
      entity rollup                 = 25   (F-6)

  NOTE the on-time% assertion: 50% is only correct if resolved_terminal is
  excluded from the denominator. If a future edit lets 15/17 in, this becomes
  25% and the test fails - which is exactly the point.
===========================================================================*/

PRINT 'Golden regression suite installed. Run: EXEC dbo.usp_Insights_GoldenInvariants @CustomerID = <tenant>;';
GO
