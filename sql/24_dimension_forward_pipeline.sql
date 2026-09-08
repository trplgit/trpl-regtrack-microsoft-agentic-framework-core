/*===========================================================================
  RegTrack Insights - Phase 1b (extension)
  FORWARD PIPELINE DIMENSION - due-date histogram over the next 90 days

  Authored by Claude Code; deployed to production 2026-09-03. Pulled from
  sys.sql_modules and committed here so the repo matches what is running.

  Emits SIX result sets. Error codes 51173-51174 (shared block 5117x with
  sql/22, 23, 25 - Claude Code's allocation, retained because deployed).

  -- [FIX 2026-09-04] tvfInsightsForwardPipelineSchedules joined
     RecentComplianceTransactionView - a non-indexed view over 45.7M rows whose
     tenant filter is not pushed through. Replaced with the explicit TOP 1
     seek pattern from sql/01. Verified identical on 400 samples.

  -- [FIX 2026-09-04] The original used an INNER join to the status dictionary
     with OverdueEligible = 1 as the "still open" test. For FUTURE schedules
     that silently excluded anything with no transaction yet - which is the
     normal state of a schedule that is not yet due. Now: open = overdue-
     eligible status OR no transaction at all, consistent with the BA ruling
     applied to the past-due function.

  Companion: sql/26_dimension_forward_risk.sql answers a DIFFERENT question
  (which of this upcoming work carries present risk factors). This proc
  answers WHEN it is due; that one answers WHAT is likely to slip.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.tvfInsightsForwardPipelineSchedules', 'IF') IS NOT NULL
    DROP FUNCTION dbo.tvfInsightsForwardPipelineSchedules;
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
      AND cso.ScheduleOn >  @AsOf
      AND cso.ScheduleOn <= DATEADD(DAY, 90, @AsOf)
      AND (d.OverdueEligible = 1 OR lt.ID IS NULL)
);
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_ForwardPipeline', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_ForwardPipeline;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_ForwardPipeline
    @UserID INT, @CustomerID INT, @AsOf DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @AsOf IS NULL SET @AsOf = SYSDATETIME();

    /*-- 0. PRE-FLIGHT -------------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51173, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. SCOPED FORWARD SCHEDULES -----------------------------------------*/
    IF OBJECT_ID('tempdb..#fwd') IS NOT NULL DROP TABLE #fwd;
    /*  [PERF 2026-09-04] Same TVF-to-TVF shape as sql/22 - and it needs the same
        treatment. Measured on the largest tenant (1,709 branches):

            forward TVF alone            2,884 ms   (104,171 rows)
            scope pairs alone               78 ms   ( 13,672 rows)
            bare join between them       TIMED OUT  (1.4 BILLION row nested-loop
                                                     product - no cardinality
                                                     estimate on either side)
            materialised + indexed      11,186 ms

        A join hint is NOT the answer - it forces join order through the inlined
        function and was 30x worse again. Materialise each side, index it, then
        join. RECOMPILE guards against a bad cached plan (see sql/22).        */
    IF OBJECT_ID('tempdb..#sp') IS NOT NULL DROP TABLE #sp;
    SELECT DISTINCT BranchID, CategoryId INTO #sp
    FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID);
    CREATE CLUSTERED INDEX IX_sp ON #sp (BranchID, CategoryId);

    IF OBJECT_ID('tempdb..#raw') IS NOT NULL DROP TABLE #raw;
    SELECT f.ComplianceInstanceID, f.ComplianceScheduleOnID, f.ScheduleOn,
           f.CustomerBranchID, f.CategoryId
    INTO #raw
    FROM dbo.tvfInsightsForwardPipelineSchedules(@CustomerID, @AsOf) f
    OPTION (RECOMPILE);
    CREATE CLUSTERED INDEX IX_raw ON #raw (CustomerBranchID, CategoryId);

    IF OBJECT_ID('tempdb..#fwd') IS NOT NULL DROP TABLE #fwd;
    SELECT r.ComplianceInstanceID, r.ComplianceScheduleOnID, r.ScheduleOn,
           DATEDIFF(DAY, CAST(@AsOf AS DATE), CAST(r.ScheduleOn AS DATE)) AS DaysOut
    INTO #fwd
    FROM #raw r
    JOIN #sp sp ON sp.BranchID = r.CustomerBranchID AND sp.CategoryId = r.CategoryId;

    DROP TABLE #raw; DROP TABLE #sp;

    /*-- 2. THE 5 REAL TIME-WINDOW ROWS - always exactly 5, zero-filled when
       a window is genuinely empty (fixed list, never omit a member). ------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    SELECT b.WindowLabel, b.MinDaysOut, b.MaxDaysOut,
           ISNULL(x.DueCount, 0) AS DueCount
    INTO #rows
    FROM (VALUES
            ('0-7d',   0,  7),
            ('8-14d',  8,  14),
            ('15-30d', 15, 30),
            ('31-60d', 31, 60),
            ('61-90d', 61, 90)
         ) AS b(WindowLabel, MinDaysOut, MaxDaysOut)
    OUTER APPLY (
        /*  DISTINCT ComplianceScheduleOnID, not ComplianceInstanceID. One
            instance can own several forward-due schedules landing in different
            day-windows; only the schedule is single-window by construction. */
        SELECT COUNT(DISTINCT f.ComplianceScheduleOnID) AS DueCount
        FROM #fwd f WHERE f.DaysOut BETWEEN b.MinDaysOut AND b.MaxDaysOut
    ) x;

    /*-- 3. CONTROL TOTALS - same instant as the rows. Every forward SCHEDULE
       falls in exactly one window by construction. ----------------------*/
    DECLARE @sumOfRows INT = (SELECT SUM(DueCount) FROM #rows);
    DECLARE @distinctDueSchedules INT = (SELECT COUNT(DISTINCT ComplianceScheduleOnID) FROM #fwd);

    IF @sumOfRows <> @distinctDueSchedules
        THROW 51174, N'RECONCILIATION FAILED - window rows do not sum to the distinct due-next-90d schedule count. Refusing to emit.', 1;

    SELECT 'control_totals' AS ResultSet,
           @CustomerID AS CustomerID, @AsOf AS AsOfUtc,
           @sumOfRows AS DueNext90d, @sumOfRows AS SumOfRows,
           (SELECT COUNT(DISTINCT ComplianceInstanceID) FROM #fwd) AS DistinctInstancesDue;

    /*-- 4. THE 5 REAL ROWS ---------------------------------------------------*/
    SELECT 'rows' AS ResultSet, WindowLabel, MinDaysOut, MaxDaysOut, DueCount
    FROM #rows
    ORDER BY MinDaysOut;

    /*-- 5. DETECTOR EMISSION POLICY - is the pipeline front-loaded? "Near
       term" = 0-14 days, the horizon a CCO can no longer defer against. --*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector   VARCHAR(40) PRIMARY KEY,
        Eligible   INT,
        Flagged    INT,
        FlaggedPct DECIMAL(5,1),
        EmitMode   VARCHAR(12)
    );
    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'near_term_concentration', @sumOfRows,
           (SELECT ISNULL(SUM(DueCount), 0) FROM #rows WHERE WindowLabel IN ('0-7d', '8-14d'));

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 6. TYPED ASSERTIONS ----------------------------------------------*/
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
    SELECT 'A-' + REPLACE(UPPER(WindowLabel), '-', ''), 'forward_share_pct', WindowLabel,
           CASE WHEN @sumOfRows = 0 THEN 0 ELSE CAST(100.0 * DueCount / @sumOfRows AS DECIMAL(18,2)) END,
           NULL, NULL, NULL, NULL, NULL, NULL
    FROM #rows;

    IF (SELECT EmitMode FROM #detector WHERE Detector = 'near_term_concentration') = 'aggregate'
        INSERT #assert
        SELECT 'A-NEARTERM-AGG', 'forward_near_term_concentration', N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'due within 14 days - the horizon that can no longer be deferred'
        FROM #detector WHERE Detector = 'near_term_concentration';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 7. FINDINGS --------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (
        FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(300), AssertionIds VARCHAR(400),
        NarrativeGuard NVARCHAR(300) NULL
    );

    INSERT #find
    SELECT 'F-NEARTERM-AGG', 'medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' schedule(s) due in the next 90 days (', VsComparatorPP,
                  N'%) fall within the next 14 days'),
           AssertionId,
           N'A front-loaded pipeline, not an even 90-day spread. This is real due-count concentration, not a risk prediction.'
    FROM #assert WHERE AssertionId = 'A-NEARTERM-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 8. DATA-QUALITY NOTES -------------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail
    FROM (
        SELECT 'zero_due_next_90d' AS Issue,
               N'No schedules due in the next 90 days for this scope - the forward pipeline has nothing to show.' AS Detail
        WHERE @sumOfRows = 0
        UNION ALL
        SELECT 'risk_model_is_separate',
               N'This proc reports real due-next-90d counts by window only. Which of that work carries present risk factors is answered by usp_Insights_Dimension_ForwardRisk (sql/26), which is a count of observable facts, not a prediction.'
    ) q;

    DROP TABLE #fwd; DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Forward pipeline dimension installed.';
GO
