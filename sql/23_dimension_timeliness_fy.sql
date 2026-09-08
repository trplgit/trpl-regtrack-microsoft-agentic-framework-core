/*===========================================================================
  RegTrack Insights - Phase 1b (extension)
  TIMELINESS BY FISCAL YEAR - on-time closure rate, current vs previous FY

  Authored by Claude Code; deployed to production 2026-09-03. Pulled from
  sys.sql_modules and committed here so the repo matches what is running.
  No code changes needed: it counts closure EVENTS directly from
  ComplianceTransaction (every row, not just the latest), which is correct
  for this measure and never touches the view.

  Emits SIX result sets. Error code 51172 (shared block 5117x).

  Handles the complete-vs-partial-year trap: comparatives are suppressed
  when either FY has no completed events (never a fabricated comparative).
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_TimelinessFY', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_TimelinessFY;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_TimelinessFY
    @UserID INT, @CustomerID INT, @AsOf DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @AsOf IS NULL SET @AsOf = SYSDATETIME();

    /*-- 0. PRE-FLIGHT ------------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51172, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. FISCAL-YEAR BOUNDARIES ------------------------------------------*/
    DECLARE @currentFyStartYear INT = CASE WHEN MONTH(@AsOf) >= 4 THEN YEAR(@AsOf) ELSE YEAR(@AsOf) - 1 END;
    DECLARE @currentFyStart DATE = DATEFROMPARTS(@currentFyStartYear, 4, 1);
    DECLARE @previousFyStart DATE = DATEADD(YEAR, -1, @currentFyStart);
    DECLARE @previousFyEnd DATE = DATEADD(DAY, -1, @currentFyStart);
    DECLARE @currentFyLabel VARCHAR(10) = CONCAT('FY', @currentFyStartYear, '-', RIGHT(CAST(@currentFyStartYear + 1 AS VARCHAR(4)), 2));
    DECLARE @previousFyLabel VARCHAR(10) = CONCAT('FY', @currentFyStartYear - 1, '-', RIGHT(CAST(@currentFyStartYear AS VARCHAR(4)), 2));

    /*-- 2. SCOPED CLOSURE EVENTS WITH A TIMELINESS CLASSIFICATION ---------
       Every completed transaction (not just the latest), bucketed by the
       SCHEDULE's FY. resolved_terminal carries no Timeliness and is excluded
       from the denominator by construction (spec Sec.6.4). --------------*/
    /*  [PERF - CORRECTED 2026-09-04] SCOPE FIRST, then reach for the big tables.

        The original joined ComplianceScheduleOn (29.4M) and ComplianceTransaction
        (45.7M) with the tenant filter three joins deep, so neither table could be
        seeked. Measured on a 7-branch tenant:
            deployed (INNER HASH JOIN)  183,502 ms
            hint removed                 14,472 ms
            scope-first (this)              157 ms
        Same 1,181 events every time.

        Do NOT reintroduce a join hint - it forces join ORDER for the whole
        statement, including inside the inlined TVF, which is what made the
        deployed version pathological.                                        */
    IF OBJECT_ID('tempdb..#scoped') IS NOT NULL DROP TABLE #scoped;
    SELECT s.ComplianceInstanceID
    INTO #scoped
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;
    CREATE CLUSTERED INDEX IX_scoped ON #scoped (ComplianceInstanceID);

    IF OBJECT_ID('tempdb..#events') IS NOT NULL DROP TABLE #events;
    SELECT cso.ComplianceInstanceID, cso.ScheduleOn, d.Timeliness,
           CASE
               WHEN cso.ScheduleOn >= @currentFyStart THEN 'current_fy'
               WHEN cso.ScheduleOn >= @previousFyStart AND cso.ScheduleOn <= @previousFyEnd THEN 'previous_fy'
               ELSE 'outside_window'
           END AS FyBucket
    INTO #events
    FROM #scoped sc
    JOIN ComplianceScheduleOn  cso ON cso.ComplianceInstanceID = sc.ComplianceInstanceID
    JOIN ComplianceTransaction t   ON t.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = t.StatusId
    WHERE d.ClosureClass = 'completed' AND d.Timeliness IS NOT NULL
      AND cso.ScheduleOn >= @previousFyStart AND cso.ScheduleOn <= @AsOf;

    /*-- 3. THE 2 REAL FY ROWS ----------------------------------------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    SELECT b.FyBucket, b.FYLabel,
           ISNULL(x.CompletedEvents, 0) AS CompletedEvents,
           ISNULL(x.OnTimeEvents, 0) AS OnTimeEvents
    INTO #rows
    FROM (VALUES ('current_fy', @currentFyLabel), ('previous_fy', @previousFyLabel)) AS b(FyBucket, FYLabel)
    OUTER APPLY (
        SELECT COUNT(*) AS CompletedEvents,
               SUM(CASE WHEN e.Timeliness = 'on_time' THEN 1 ELSE 0 END) AS OnTimeEvents
        FROM #events e WHERE e.FyBucket = b.FyBucket
    ) x;

    DECLARE @currentCompleted INT = (SELECT CompletedEvents FROM #rows WHERE FyBucket = 'current_fy');
    DECLARE @currentOnTime    INT = (SELECT OnTimeEvents    FROM #rows WHERE FyBucket = 'current_fy');
    DECLARE @previousCompleted INT = (SELECT CompletedEvents FROM #rows WHERE FyBucket = 'previous_fy');
    DECLARE @previousOnTime    INT = (SELECT OnTimeEvents    FROM #rows WHERE FyBucket = 'previous_fy');

    DECLARE @currentOnTimePct DECIMAL(5,1) = CASE WHEN @currentCompleted = 0 THEN NULL ELSE CAST(100.0 * @currentOnTime / @currentCompleted AS DECIMAL(5,1)) END;
    DECLARE @previousOnTimePct DECIMAL(5,1) = CASE WHEN @previousCompleted = 0 THEN NULL ELSE CAST(100.0 * @previousOnTime / @previousCompleted AS DECIMAL(5,1)) END;

    /*-- Comparatives suppressed when either side has nothing to compare ---*/
    DECLARE @yoyChangePP DECIMAL(5,1) = CASE WHEN @currentOnTimePct IS NULL OR @previousOnTimePct IS NULL THEN NULL
                                              ELSE @currentOnTimePct - @previousOnTimePct END;
    DECLARE @fyTrend VARCHAR(12) = CASE WHEN @yoyChangePP IS NULL THEN NULL
                                         WHEN @yoyChangePP > 1.0 THEN 'improving'
                                         WHEN @yoyChangePP < -1.0 THEN 'declining'
                                         ELSE 'flat' END;

    /*-- 4. CONTROL TOTALS --------------------------------------------------*/
    SELECT 'control_totals' AS ResultSet,
           @CustomerID AS CustomerID, @AsOf AS AsOfUtc,
           @currentFyLabel AS CurrentFyLabel, @previousFyLabel AS PreviousFyLabel,
           @currentCompleted AS ClosuresCurrentFY, @previousCompleted AS ClosuresPreviousFY,
           @currentOnTimePct AS OnTimePctCurrentFY, @previousOnTimePct AS OnTimePctPreviousFY,
           @yoyChangePP AS YoyChangePP, @fyTrend AS FyTrend;

    /*-- 5. THE 2 REAL ROWS -----------------------------------------------*/
    SELECT 'rows' AS ResultSet,
           FyBucket, FYLabel, CompletedEvents, OnTimeEvents,
           CASE WHEN CompletedEvents = 0 THEN NULL
                ELSE CAST(100.0 * OnTimeEvents / CompletedEvents AS DECIMAL(5,1)) END AS OnTimePct
    FROM #rows
    ORDER BY CASE FyBucket WHEN 'current_fy' THEN 1 ELSE 2 END;

    /*-- 6. DETECTOR EMISSION POLICY - single-fact dimension, Eligible = 1 --*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector   VARCHAR(40) PRIMARY KEY,
        Eligible   INT,
        Flagged    INT,
        FlaggedPct DECIMAL(5,1),
        EmitMode   VARCHAR(12)
    );
    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'ontime_declining', 1, CASE WHEN @fyTrend = 'declining' THEN 1 ELSE 0 END;

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector SET EmitMode = CASE WHEN Flagged = 0 THEN 'none' ELSE 'individual' END;

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

    IF @currentOnTimePct IS NOT NULL
    INSERT #assert
    SELECT 'A-CURRENT', 'ontime_pct', @currentFyLabel, @currentOnTimePct, NULL, NULL,
           @previousOnTimePct, @yoyChangePP,
           CASE WHEN @yoyChangePP > 0 THEN 'better' WHEN @yoyChangePP < 0 THEN 'worse' ELSE NULL END,
           CASE WHEN @previousOnTimePct IS NULL THEN N'no previous-FY comparator - previous FY has no completed events with a known Timeliness classification' ELSE NULL END;

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 8. FINDINGS --------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (
        FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(300), AssertionIds VARCHAR(400),
        NarrativeGuard NVARCHAR(300) NULL
    );

    INSERT #find
    SELECT 'F-DECLINE', 'medium',
           CONCAT(N'On-time closure rate fell ', ABS(@yoyChangePP), N'pp year-over-year, from ',
                  @previousOnTimePct, N'% (', @previousFyLabel, N') to ', @currentOnTimePct, N'% (', @currentFyLabel, N')'),
           'A-CURRENT', NULL
    WHERE (SELECT EmitMode FROM #detector WHERE Detector = 'ontime_declining') = 'individual';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 9. DATA-QUALITY NOTES ----------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail
    FROM (
        SELECT 'no_completed_events_current_fy' AS Issue,
               N'No completed closure events with a known Timeliness classification in the current FY - OnTimePctCurrentFY cannot be assessed and is not reported as 0%.' AS Detail
        WHERE @currentCompleted = 0
        UNION ALL
        SELECT 'no_completed_events_previous_fy', N'No completed closure events with a known Timeliness classification in the previous FY - year-over-year comparison suppressed.'
        WHERE @previousCompleted = 0
    ) q;

    DROP TABLE #scoped; DROP TABLE #events; DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Timeliness by FY dimension installed.';
GO
