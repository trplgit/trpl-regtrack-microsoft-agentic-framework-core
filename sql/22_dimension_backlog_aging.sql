/*===========================================================================
  RegTrack Insights - Phase 1b (extension)
  BACKLOG AGING DIMENSION - overdue by the fiscal year it fell due in

  Authored by Claude Code; deployed to production 2026-09-03. Pulled from
  sys.sql_modules and committed here so the repo matches what is running.
  No code changes: it reads overdue through tvfInsightsOverdueSchedules, so
  the 2026-09-04 view fix in sql/01 repairs its production timeout with no
  change to this file.

  Emits SIX result sets. Error codes 51170-51171 (shared block 5117x).
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_BacklogAging', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_BacklogAging;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_BacklogAging
    @UserID INT, @CustomerID INT, @AsOf DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @AsOf IS NULL SET @AsOf = SYSDATETIME();

    /*-- 0. PRE-FLIGHT - same fail-closed posture as every other dimension --*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51170, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. FISCAL-YEAR BOUNDARIES, anchored on @AsOf -----------------------
       Indian FY: April 1 - March 31. "Current FY" is the one @AsOf falls in,
       "previous FY" the one immediately before it. ---------------------- */
    DECLARE @currentFyStartYear INT = CASE WHEN MONTH(@AsOf) >= 4 THEN YEAR(@AsOf) ELSE YEAR(@AsOf) - 1 END;
    DECLARE @currentFyStart DATE = DATEFROMPARTS(@currentFyStartYear, 4, 1);
    DECLARE @previousFyStart DATE = DATEADD(YEAR, -1, @currentFyStart);
    DECLARE @currentFyLabel VARCHAR(10) = CONCAT('FY', @currentFyStartYear, '-', RIGHT(CAST(@currentFyStartYear + 1 AS VARCHAR(4)), 2));
    DECLARE @previousFyLabel VARCHAR(10) = CONCAT('FY', @currentFyStartYear - 1, '-', RIGHT(CAST(@currentFyStartYear AS VARCHAR(4)), 2));

    /*-- 2. SCOPED OVERDUE SCHEDULES ---------------------------------------*/
    /*  [PERF - CORRECTED 2026-09-04] Materialise BOTH sides into indexed temp
        tables, then join. Joining two inline TVFs directly gives the optimizer
        no cardinality estimate; a join HINT is worse still, because it forces
        join order through the inlined function and stops the overdue TVF
        filtering by tenant before it touches ComplianceScheduleOn (29.4M rows).

        Measured on a 7-branch tenant:
            bare join                     877 ms
            INNER HASH JOIN            26,492 ms   <- what was deployed
            materialised + indexed        438 ms   <- this
        On V-Mart (494,032 overdue rows) the materialised pattern is 1.9 s.    */
    IF OBJECT_ID('tempdb..#sp') IS NOT NULL DROP TABLE #sp;
    SELECT DISTINCT BranchID, CategoryId INTO #sp
    FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID);
    CREATE CLUSTERED INDEX IX_sp ON #sp (BranchID, CategoryId);

    IF OBJECT_ID('tempdb..#raw') IS NOT NULL DROP TABLE #raw;
    /*  [PERF 2026-09-04] OPTION (RECOMPILE) is REQUIRED here, not decorative.
        Measured on production, tenant 1817 (7 branches, 1,039 overdue rows):

            inside this proc, cached plan   75,468 ms   125,825,937 logical reads
            same statement, fresh compile      752 ms

        126 million page reads to return 1,039 rows. The procedure had cached a
        pathological plan for the inlined TVF; every standalone and sp_executesql
        variant of the identical statement compiles to a good plan in under a
        second. RECOMPILE costs ~10 ms and removes the risk entirely.

        Do NOT replace this with a join hint - see the note below.            */
    SELECT o.ComplianceInstanceID, o.ComplianceScheduleOnID, o.ScheduleOn,
           o.CustomerBranchID, o.CategoryId
    INTO #raw
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    OPTION (RECOMPILE);
    CREATE CLUSTERED INDEX IX_raw ON #raw (CustomerBranchID, CategoryId);

    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT r.ComplianceInstanceID, r.ComplianceScheduleOnID, r.ScheduleOn,
           CASE
               WHEN r.ScheduleOn >= @currentFyStart THEN 'current_fy'
               WHEN r.ScheduleOn >= @previousFyStart THEN 'previous_fy'
               ELSE 'older'
           END AS Bucket
    INTO #ovd
    FROM #raw r
    JOIN #sp sp ON sp.BranchID = r.CustomerBranchID AND sp.CategoryId = r.CategoryId;

    DROP TABLE #raw; DROP TABLE #sp;

    CREATE CLUSTERED INDEX IX_ovd ON #ovd (Bucket);

    /*-- 3. THE 3 REAL BUCKET ROWS - always exactly 3, zero-filled ----------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        Bucket        VARCHAR(20)   NOT NULL,
        FYLabel       VARCHAR(10)   NULL,
        OverdueCount  INT           NOT NULL,
        OldestDueDate DATE          NULL,
        NewestDueDate DATE          NULL
    );
    INSERT #rows (Bucket, FYLabel, OverdueCount, OldestDueDate, NewestDueDate)
    SELECT b.Bucket, b.FYLabel,
           ISNULL(x.OverdueCount, 0), x.OldestDueDate, x.NewestDueDate
    FROM (VALUES ('current_fy', @currentFyLabel), ('previous_fy', @previousFyLabel), ('older', NULL))
         AS b(Bucket, FYLabel)
    OUTER APPLY (
        /*  DISTINCT ComplianceScheduleOnID, not ComplianceInstanceID - each
            SCHEDULE has exactly one ScheduleOn date, so it falls in exactly
            one bucket by construction. An INSTANCE can own several overdue
            schedules in different buckets; counting by instance breaks the
            sum-to-total identity. Confirmed against UAT tenant 29. */
        SELECT COUNT(DISTINCT o.ComplianceScheduleOnID) AS OverdueCount,
               CAST(MIN(o.ScheduleOn) AS DATE) AS OldestDueDate,
               CAST(MAX(o.ScheduleOn) AS DATE) AS NewestDueDate
        FROM #ovd o WHERE o.Bucket = b.Bucket
    ) x;

    /*-- 4. CONTROL TOTALS - exact identity: every overdue SCHEDULE is in
       exactly one bucket. ------------------------------------------------*/
    DECLARE @sumOfRows INT = (SELECT SUM(OverdueCount) FROM #rows);
    DECLARE @distinctOverdueSchedules INT = (SELECT COUNT(DISTINCT ComplianceScheduleOnID) FROM #ovd);

    IF @sumOfRows <> @distinctOverdueSchedules
        THROW 51171, N'RECONCILIATION FAILED - bucket rows do not sum to the distinct overdue schedule count. Refusing to emit.', 1;

    SELECT 'control_totals' AS ResultSet,
           @CustomerID AS CustomerID, @AsOf AS AsOfUtc,
           @currentFyLabel AS CurrentFyLabel, @previousFyLabel AS PreviousFyLabel,
           @sumOfRows AS SumOfRows, @distinctOverdueSchedules AS DistinctOverdueSchedules;

    /*-- 5. THE 3 REAL ROWS + computed share ----------------------------------*/
    SELECT 'rows' AS ResultSet,
           Bucket, FYLabel, OverdueCount, OldestDueDate, NewestDueDate,
           CASE WHEN @sumOfRows = 0 THEN NULL
                ELSE CAST(100.0 * OverdueCount / @sumOfRows AS DECIMAL(5,1)) END AS SharePct
    FROM #rows
    ORDER BY CASE Bucket WHEN 'current_fy' THEN 1 WHEN 'previous_fy' THEN 2 ELSE 3 END;

    /*-- 6. DETECTOR EMISSION POLICY - does the "older" bucket dominate? ---*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector   VARCHAR(40) PRIMARY KEY,
        Eligible   INT,
        Flagged    INT,
        FlaggedPct DECIMAL(5,1),
        EmitMode   VARCHAR(12)
    );
    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'older_bucket_dominates', @sumOfRows, (SELECT OverdueCount FROM #rows WHERE Bucket = 'older');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 7. TYPED ASSERTIONS ----------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId     VARCHAR(20),
        Metric          VARCHAR(60),
        ScopeLabel      NVARCHAR(200),
        Value           DECIMAL(18,2),
        Rank_           INT NULL,
        OfN             INT NULL,
        ComparatorValue DECIMAL(18,2) NULL,
        VsComparatorPP  DECIMAL(9,2) NULL,
        Direction       VARCHAR(10) NULL,
        Caveat          NVARCHAR(200) NULL
    );

    INSERT #assert
    SELECT 'A-' + UPPER(Bucket), 'backlog_share_pct', ISNULL(FYLabel, N'older'),
           CASE WHEN @sumOfRows = 0 THEN 0 ELSE CAST(100.0 * OverdueCount / @sumOfRows AS DECIMAL(18,2)) END,
           NULL, NULL, NULL, NULL, NULL, NULL
    FROM #rows;

    IF (SELECT EmitMode FROM #detector WHERE Detector = 'older_bucket_dominates') = 'aggregate'
        INSERT #assert
        SELECT 'A-OLDER-AGG', 'backlog_older_than_previous_fy', N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'predates the previous fiscal year - never dealt with, not "behind this year"'
        FROM #detector WHERE Detector = 'older_bucket_dominates';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 8. FINDINGS --------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (
        FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(300), AssertionIds VARCHAR(400),
        NarrativeGuard NVARCHAR(300) NULL
    );

    INSERT #find
    SELECT 'F-OLDER-AGG', 'medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' overdue schedule(s) (', VsComparatorPP,
                  N'%) predate the previous fiscal year'),
           AssertionId,
           N'This backlog has never been dealt with, not merely "behind this year". Overdue is a flow metric - do not compare this count against a different run.'
    FROM #assert WHERE AssertionId = 'A-OLDER-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 9. DATA-QUALITY NOTES ----------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail
    FROM (
        SELECT 'zero_overdue' AS Issue,
               N'No overdue schedules in scope as of this run - the aging bar has nothing to show.' AS Detail
        WHERE @sumOfRows = 0
        UNION ALL
        SELECT 'flow_metric_caveat',
               N'Overdue is a flow metric and moves between runs - never compare this figure against a different run, only report this single as-of snapshot.'
    ) q;

    DROP TABLE #ovd; DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Backlog aging dimension installed.';
GO
