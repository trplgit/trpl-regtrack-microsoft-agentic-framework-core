/*===========================================================================
  RegTrack Insights - Phase 1d (paid engine upgrade path)
  FORWARD PIPELINE - schedules due in the next 90 days, real counts only

  Spec reference : docs/PAID_TIER_SAMPLE_REFERENCE.md Sec.3.8 (`forward_pipeline`)
  Real UI target : the fixed-holistic Forward Look tab's 90-day timeline
                   (di-fwd) - see prompts/05_report_html_fixed_holistic.md's
                   own Tab 5 note, currently gated pending this file.

  [STATUS, 2026-09-02] DRAFT, UNTESTED - written entirely from static evidence
  (tvfInsightsOverdueSchedules's own real definition, mirrored in the
  opposite direction; InsightsStatusClassification's own real CHECK
  constraint - CK_ISC_Coherent - confirming OverdueEligible = 1 always means
  ClosureClass = 'open', so the SAME "still pending" filter the overdue TVF
  already uses is the correct one for a not-yet-due schedule too). sqlserver
  MCP was unreachable (network timeout to the DB host) when this was
  written. Same discipline note as sql/22's own header - DO NOT DEPLOY until
  run against >=5 tenants of different profile and the golden regression
  passes. See sql/22 for the full rationale; not repeated here to avoid drift.

  [SCOPE, deliberate] `due_next_90d` (real COUNTS, bucketed by a real time
  window) is this file's whole job. `predicted_at_risk` (design doc Sec.3.8's
  OTHER field) is explicitly a PROJECTION per the doc's own words - "not a
  measurement... no prediction model is specified in the file" - a modelling
  decision that needs the business, not a data-lookup problem this proc can
  solve. NOT built here; never fabricated as a byproduct of the real counts.
  The fixed-holistic prompt's own "risk level per block" idea (di-fwd's
  block colouring) depends on that same undecided model - also gated.

  Grain: ONE ROW PER TIME WINDOW, always exactly 5 (0-7d, 8-14d, 15-30d,
  31-60d, 61-90d) - a simple, real bucketing of ScheduleOn, not the mock
  mockup's "This week...+90d, +45d midpoint" evenly-spaced visual scheme
  (that is a RENDERING choice for the prompt to make from these real
  buckets, not a data shape this proc needs to match exactly).

  Error block: 51170-51179 (same block as sql/22/23 - see sql/22's own
  header for the block-numbering note).

  Target: SQL Server (vitComplianceSystem). IDEMPOTENT (DROP + CREATE).
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
    /*  Mirror of dbo.tvfInsightsOverdueSchedules (sql/01) in the opposite
        direction - same real joins/filters, only the ScheduleOn comparison
        flips from "<= @AsOf" (already due) to a forward 90-day window (not
        yet due). d.OverdueEligible = 1 is still the right "still open, not
        yet resolved" filter - confirmed real via CK_ISC_Coherent, not
        assumed - see this file's own header. */
    SELECT
        i.ID                        AS ComplianceInstanceID,
        cso.ID                      AS ComplianceScheduleOnID,
        i.CustomerBranchID,
        a.ComplianceCategoryId      AS CategoryId,
        rct.ComplianceStatusID      AS StatusId,
        cso.ScheduleOn
    FROM ComplianceScheduleOn cso
    JOIN ComplianceInstance   i   ON i.ID  = cso.ComplianceInstanceID
    JOIN CustomerBranch       cb  ON cb.ID = i.CustomerBranchID
    JOIN Compliance           c   ON c.ID  = i.ComplianceID
    JOIN Act                  a   ON a.ID  = c.ActID
    JOIN RecentComplianceTransactionView rct ON rct.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d        ON d.StatusId = rct.ComplianceStatusID
    WHERE cb.CustomerID = @CustomerID
      AND cb.IsDeleted  = 0
      AND cb.Status     = 1
      AND i.IsDeleted   = 0
      AND c.IsDeleted   = 0
      AND cso.IsActive  = 1
      AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn > @AsOf
      AND cso.ScheduleOn <= DATEADD(DAY, 90, @AsOf)
      AND d.OverdueEligible = 1
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
    SELECT f.ComplianceInstanceID, f.ComplianceScheduleOnID, f.ScheduleOn,
           DATEDIFF(DAY, CAST(@AsOf AS DATE), CAST(f.ScheduleOn AS DATE)) AS DaysOut
    INTO #fwd
    FROM dbo.tvfInsightsForwardPipelineSchedules(@CustomerID, @AsOf) f
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp
      ON sp.BranchID = f.CustomerBranchID AND sp.CategoryId = f.CategoryId;

    /*-- 2. THE 5 REAL TIME-WINDOW ROWS - always exactly 5, zero-filled when
       a window is genuinely empty (same "fixed list, never omit a member"
       treatment as sql/22's 3 buckets). ---------------------------------- */
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
        /*  DISTINCT ComplianceScheduleOnID, not ComplianceInstanceID - see
            sql/22's identical comment (same bug class, confirmed against
            UAT tenant 29, Msg 51171, before this file was fixed the same
            way). One instance can own several forward-due schedules landing
            in different day-windows; only the schedule is single-window by
            construction. */
        SELECT COUNT(DISTINCT f.ComplianceScheduleOnID) AS DueCount
        FROM #fwd f WHERE f.DaysOut BETWEEN b.MinDaysOut AND b.MaxDaysOut
    ) x;

    /*-- 3. CONTROL TOTALS - captured in the same instant, per CLAUDE.md
       non-negotiable #3. SumOfRows per Sec.4a - every forward SCHEDULE
       falls in exactly one window by construction (see OUTER APPLY comment
       above for why the unit must be schedule, not instance). -------------*/
    DECLARE @sumOfRows INT = (SELECT SUM(DueCount) FROM #rows);
    DECLARE @distinctDueSchedules INT = (SELECT COUNT(DISTINCT ComplianceScheduleOnID) FROM #fwd);

    IF @sumOfRows <> @distinctDueSchedules
        THROW 51174, N'RECONCILIATION FAILED - window rows do not sum to the distinct due-next-90d schedule count. Refusing to emit.', 1;

    SELECT 'control_totals' AS ResultSet,
           @CustomerID AS CustomerID, @AsOf AS AsOfUtc,
           @sumOfRows AS DueNext90d, @sumOfRows AS SumOfRows;

    /*-- 4. THE 5 REAL ROWS ---------------------------------------------------*/
    SELECT 'rows' AS ResultSet, WindowLabel, MinDaysOut, MaxDaysOut, DueCount
    FROM #rows
    ORDER BY MinDaysOut;

    /*-- 5. DETECTOR EMISSION POLICY - CLAUDE.md Sec.4. Real business signal:
       is the pipeline front-loaded (a lot due imminently) vs spread evenly
       across the 90 days. "Near term" = 0-14 days (the two most urgent
       windows combined) - the horizon a CCO can no longer defer against.
       Eligible = @sumOfRows, same convention as sql/22. ---------------------*/
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

    /*-- 6. TYPED ASSERTIONS (comparatives COMPUTED here - spec Sec.6.10) ---*/
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

    -- unconditional: each window's own share of the 90-day pipeline
    INSERT #assert
    SELECT 'A-' + REPLACE(UPPER(WindowLabel), '-', ''), 'forward_share_pct', WindowLabel,
           CASE WHEN @sumOfRows = 0 THEN 0 ELSE CAST(100.0 * DueCount / @sumOfRows AS DECIMAL(18,2)) END,
           NULL, NULL, NULL, NULL, NULL, NULL
    FROM #rows;

    -- policy-gated: the aggregate "front-loaded pipeline" finding
    IF (SELECT EmitMode FROM #detector WHERE Detector = 'near_term_concentration') = 'aggregate'
        INSERT #assert
        SELECT 'A-NEARTERM-AGG', 'forward_near_term_concentration', N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'due within 14 days - the horizon that can no longer be deferred'
        FROM #detector WHERE Detector = 'near_term_concentration';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 7. FINDINGS - every one backed by assertion ids ---------------------*/
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
        SELECT 'predicted_at_risk_not_built',
               N'This proc reports real due-next-90d counts only. "Predicted at risk" (design doc Sec.3.8) is a projection with no defined model yet - never fabricated as a byproduct of these real counts.'
    ) q;

    DROP TABLE #fwd; DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
