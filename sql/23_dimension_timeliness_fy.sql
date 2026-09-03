/*===========================================================================
  RegTrack Insights - Phase 1d (paid engine upgrade path)
  TIMELINESS BY FISCAL YEAR - on-time closure rate, current FY vs previous FY

  Spec reference : docs/PAID_TIER_SAMPLE_REFERENCE.md Sec.3.1 (`timeliness`)
  Real UI target : the fixed-holistic Operations tab's timeliness card
                   (di-fybars) - see prompts/05_report_html_fixed_holistic.md's
                   own Tab 4 note, currently gated pending this file.

  [STATUS, 2026-09-02] Deployed to UAT, reconciled against real data (tenant
  29: 1 completed closure current FY, 19 previous FY - see this session's own
  verification). NOT yet run against >=5 tenants of different profile
  (CLAUDE.md Sec.11) - do that before calling this DONE.

  [FIX, 2026-09-02] Originally emitted only 3 result sets (control_totals,
  a single named 'timeliness' row, data_quality) - did not conform to
  IDimensionRepository's fixed 6-result-set positional contract every other
  dimension proc follows (control_totals, rows, detector_policy, assertions,
  findings, data_quality - see SqlDimensionRepository.ExecuteAsync's own
  ordering comment). Restructured: the tenant-level derived comparatives
  (OnTimePctCurrentFY/PreviousFY, YoyChangePP, FyTrend) moved into
  control_totals, matching sql/05's own TenantOnTimePct precedent for
  "a tenant-wide scalar the rows list doesn't carry a home for"; the real
  per-FY breakdown (#rows, already computed, previously never SELECTed) is
  now the position-2 'rows' result; detector_policy/assertions/findings
  added, matching sql/05's reference shape - see CLAUDE.md Sec.4.

  Grain: ONE ROW, tenant-wide (not per-member) - this is a single current-FY-
  vs-previous-FY comparison, not a breakdown dimension. `by_product`
  (per-category lateness rows, design doc Sec.3.1's own field list) is
  DELIBERATELY OUT OF SCOPE for this file - Nature/Act dimensions already
  compute per-member OverduePct (a different, already-real metric) but not
  per-category Timeliness specifically; threading that in is real follow-up
  work, not invented here.

  Timeliness is ANCHORED ON ScheduleOn (the obligation's due date), per the
  design doc's own explicit instruction ("on-time percentage of completed
  work, per FY, anchored on ScheduleOn") - NOT the completion/transaction
  date. A schedule due in FY2025-26 but closed late in FY2026-27 still counts
  against FY2025-26's rate; this is deliberate, matching what "on-time for
  that obligation" actually means.

  Error block: 51170-51179 (same block as sql/22 - a single-row tenant-wide
  aggregate, not a per-member dimension, does not need its own 10-code block
  on top of that file's; see sql/22's own header for the block-numbering note).

  Target: SQL Server (vitComplianceSystem). IDEMPOTENT (DROP + CREATE).
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

    /*-- 1. FISCAL-YEAR BOUNDARIES - same logic as sql/22, kept local until a
       second real caller justifies extracting a shared function (YAGNI). --*/
    DECLARE @currentFyStartYear INT = CASE WHEN MONTH(@AsOf) >= 4 THEN YEAR(@AsOf) ELSE YEAR(@AsOf) - 1 END;
    DECLARE @currentFyStart DATE = DATEFROMPARTS(@currentFyStartYear, 4, 1);
    DECLARE @previousFyStart DATE = DATEADD(YEAR, -1, @currentFyStart);
    DECLARE @previousFyEnd DATE = DATEADD(DAY, -1, @currentFyStart);
    DECLARE @currentFyLabel VARCHAR(10) = CONCAT('FY', @currentFyStartYear, '-', RIGHT(CAST(@currentFyStartYear + 1 AS VARCHAR(4)), 2));
    DECLARE @previousFyLabel VARCHAR(10) = CONCAT('FY', @currentFyStartYear - 1, '-', RIGHT(CAST(@currentFyStartYear AS VARCHAR(4)), 2));

    /*-- 2. SCOPED CLOSURE EVENTS WITH A REAL TIMELINESS CLASSIFICATION -----
       Same base query sql/12's own #quality table already proves correct,
       widened from "this user's own performer work" (RoleID = 3) to every
       scoped closure regardless of role, and bucketed by the SCHEDULE's own
       FY instead of grouped by user. resolved_terminal carries no
       Timeliness and is excluded from the denominator by construction
       (d.Timeliness IS NOT NULL), exactly as spec Sec.6.4 requires and
       sql/12 already established. --------------------------------------- */
    IF OBJECT_ID('tempdb..#events') IS NOT NULL DROP TABLE #events;
    SELECT i.ID AS ComplianceInstanceID, cso.ScheduleOn, d.Timeliness,
           CASE
               WHEN cso.ScheduleOn >= @currentFyStart THEN 'current_fy'
               WHEN cso.ScheduleOn >= @previousFyStart AND cso.ScheduleOn <= @previousFyEnd THEN 'previous_fy'
               ELSE 'outside_window'
           END AS FyBucket
    INTO #events
    FROM ComplianceScheduleOn cso
    JOIN ComplianceInstance   i   ON i.ID  = cso.ComplianceInstanceID
    JOIN CustomerBranch       cb  ON cb.ID = i.CustomerBranchID
    JOIN Compliance           c   ON c.ID  = i.ComplianceID
    JOIN Act                  a   ON a.ID  = c.ActID
    JOIN ComplianceTransaction t  ON t.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = t.StatusId
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp
      ON sp.BranchID = cb.ID AND sp.CategoryId = a.ComplianceCategoryId
    WHERE cb.IsDeleted = 0 AND cb.Status = 1
      AND i.IsDeleted = 0 AND c.IsDeleted = 0
      AND d.ClosureClass = 'completed' AND d.Timeliness IS NOT NULL
      AND cso.ScheduleOn >= @previousFyStart AND cso.ScheduleOn <= @AsOf;

    /*-- 3. THE 2 REAL FY ROWS -----------------------------------------------*/
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

    /*-- Sec.4's "single member -> suppress comparatives" applied to the
       2-FY comparison itself: no yoy_change_pts / fy_trend when either side
       has nothing to compare (CLAUDE.md non-negotiable #5 - never a
       fabricated comparative). ------------------------------------------ */
    DECLARE @yoyChangePP DECIMAL(5,1) = CASE WHEN @currentOnTimePct IS NULL OR @previousOnTimePct IS NULL THEN NULL
                                              ELSE @currentOnTimePct - @previousOnTimePct END;
    DECLARE @fyTrend VARCHAR(12) = CASE WHEN @yoyChangePP IS NULL THEN NULL
                                         WHEN @yoyChangePP > 1.0 THEN 'improving'
                                         WHEN @yoyChangePP < -1.0 THEN 'declining'
                                         ELSE 'flat' END;

    /*-- 4. CONTROL TOTALS - carries the tenant-level derived comparatives
       too (YoyChangePP, FyTrend belong to neither FY row alone), same
       precedent as sql/05's own TenantOnTimePct. ---------------------------*/
    SELECT 'control_totals' AS ResultSet,
           @CustomerID AS CustomerID, @AsOf AS AsOfUtc,
           @currentFyLabel AS CurrentFyLabel, @previousFyLabel AS PreviousFyLabel,
           @currentCompleted AS ClosuresCurrentFY, @previousCompleted AS ClosuresPreviousFY,
           @currentOnTimePct AS OnTimePctCurrentFY, @previousOnTimePct AS OnTimePctPreviousFY,
           @yoyChangePP AS YoyChangePP, @fyTrend AS FyTrend;

    /*-- 5. THE 2 REAL ROWS, each with its own OnTimePct -----------------------*/
    SELECT 'rows' AS ResultSet,
           FyBucket, FYLabel, CompletedEvents, OnTimeEvents,
           CASE WHEN CompletedEvents = 0 THEN NULL
                ELSE CAST(100.0 * OnTimeEvents / CompletedEvents AS DECIMAL(5,1)) END AS OnTimePct
    FROM #rows
    ORDER BY CASE FyBucket WHEN 'current_fy' THEN 1 ELSE 2 END;

    /*-- 6. DETECTOR EMISSION POLICY - CLAUDE.md Sec.4, applied to a
       tenant-wide single-fact dimension: Eligible = 1 (the tenant itself is
       the only "member"), so EmitMode always resolves via the Eligible<=5
       rule sql/05 already establishes for small eligible sets - never
       'aggregate' (there is nothing to aggregate over), always 'individual'
       when flagged. ---------------------------------------------------------*/
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

    /*-- 7. TYPED ASSERTIONS (comparatives COMPUTED here - spec Sec.6.10) ---*/
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

    -- A-CURRENT: current-FY on-time pct vs previous-FY, only when assessable
    IF @currentOnTimePct IS NOT NULL
    INSERT #assert
    SELECT 'A-CURRENT', 'ontime_pct', @currentFyLabel, @currentOnTimePct, NULL, NULL,
           @previousOnTimePct, @yoyChangePP,
           CASE WHEN @yoyChangePP > 0 THEN 'better' WHEN @yoyChangePP < 0 THEN 'worse' ELSE NULL END,
           CASE WHEN @previousOnTimePct IS NULL THEN N'no previous-FY comparator - previous FY has no completed events with a known Timeliness classification' ELSE NULL END;

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 8. FINDINGS - every one backed by assertion ids ---------------------*/
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

    /*-- 9. DATA-QUALITY NOTES ------------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail
    FROM (
        SELECT 'no_completed_events_current_fy' AS Issue,
               N'No completed closure events with a known Timeliness classification in the current FY - OnTimePctCurrentFY cannot be assessed and is not reported as 0%.' AS Detail
        WHERE @currentCompleted = 0
        UNION ALL
        SELECT 'no_completed_events_previous_fy', N'No completed closure events with a known Timeliness classification in the previous FY - year-over-year comparison suppressed.'
        WHERE @previousCompleted = 0
    ) q;

    DROP TABLE #events; DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
