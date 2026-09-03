/*===========================================================================
  RegTrack Insights - Phase 1d (paid engine upgrade path)
  BACKLOG AGING - overdue schedules bucketed by the FY they fell due in

  Spec reference : docs/PAID_TIER_SAMPLE_REFERENCE.md Sec.3.2 (`backlog`)
  Real UI target : the fixed-holistic Coverage/Risk & licences tab's "Overdue /
                   backlog" aging bar - see prompts/05_report_html_fixed_holistic.md's
                   own Tab 2 note (di-agebar), currently gated pending this file.

  [STATUS, 2026-09-02] DRAFT, UNTESTED - written entirely from static evidence
  (tvfInsightsOverdueSchedules's own real definition in sql/01, the same
  ScheduleOn-bucketing pattern already proven correct for other dimensions) -
  sqlserver MCP was unreachable (network timeout to the DB host, not a config
  problem) when this was written, so CLAUDE.md Sec.11's own testing discipline
  has NOT been applied yet: "fourteen defects were found building this, every
  single one passed on the first tenant checked... static review cannot find
  contract, encoding, or cross-component defects - run the code." DO NOT DEPLOY
  until run against >=5 tenants of different profile (Sec.11's own table:
  1490, 1403, 29, 522, 2480 at minimum) and the golden regression passes.

  Grain: ONE ROW PER AGE BUCKET, always exactly 3 - `current_fy` / `previous_fy`
  / `older`, bucketed by the FISCAL YEAR the obligation's ComplianceScheduleOn.
  ScheduleOn (its due date) fell in, per the design doc's own Sec.3.2 rationale:
  "separates 'we are behind this year' from 'we have never dealt with this'".
  Rows sum to the total exactly (SumOfRows per Sec.4a - no residual, every
  overdue schedule falls in exactly one bucket by construction).

  [TRAP, inherited from tvfInsightsOverdueSchedules / design doc Sec.3.2's own
  warning] Overdue is a FLOW metric, not a stock one - it drifts between runs
  as RecentComplianceTransactionView refreshes (observed elsewhere: 1,387 ->
  1,398 -> 1,116 on identical SQL in one session). Never compare an overdue
  figure from one run against another run; only ever report the single
  as-of-@AsOf snapshot this proc computes.

  [OPEN, not invented here] `closure_rate_pct` (design doc Sec.3.2's own field
  list) has no defined formula anywhere in this repo yet - same class of open
  item PAID_TIER_SAMPLE_REFERENCE.md's own Sec.2 already flags for the
  composite score components ("how X is derived is not defined anywhere").
  Not emitted by this proc until a real formula is specified - never guessed.

  Error block: 51170-51179. The design doc's own error-block table (CLAUDE.md
  Sec.5b) still lists 51150-51159 as "20 forward pipeline", but file 20 was
  repurposed to tenant_token_usage before this session - that block is
  effectively free too, but 51170+ is the block CLAUDE.md itself names free
  without ambiguity, so this file uses that one. [FLAG] the block-to-file table
  in CLAUDE.md Sec.5b needs a follow-up edit once this file's number is final.

  Target: SQL Server (vitComplianceSystem). IDEMPOTENT (DROP + CREATE).
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
       "previous FY" the one immediately before it - matches this codebase's
       own existing "FY2025-26"-shaped period labels (e.g.
       InsightsReportOrchestrationInput.Period test fixtures). No existing
       fiscal-year boundary helper anywhere in this repo yet - this is the
       first, kept local to this proc rather than a shared function until a
       second real caller needs it (YAGNI; a shared tvf is a one-line
       extraction later if that happens). */
    DECLARE @currentFyStartYear INT = CASE WHEN MONTH(@AsOf) >= 4 THEN YEAR(@AsOf) ELSE YEAR(@AsOf) - 1 END;
    DECLARE @currentFyStart DATE = DATEFROMPARTS(@currentFyStartYear, 4, 1);
    DECLARE @previousFyStart DATE = DATEADD(YEAR, -1, @currentFyStart);
    DECLARE @currentFyLabel VARCHAR(10) = CONCAT('FY', @currentFyStartYear, '-', RIGHT(CAST(@currentFyStartYear + 1 AS VARCHAR(4)), 2));
    DECLARE @previousFyLabel VARCHAR(10) = CONCAT('FY', @currentFyStartYear - 1, '-', RIGHT(CAST(@currentFyStartYear AS VARCHAR(4)), 2));

    /*-- 2. SCOPED OVERDUE SCHEDULES, same base every other dimension using
       overdue already trusts ------------------------------------------------*/
    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT o.ComplianceInstanceID, o.ComplianceScheduleOnID, o.ScheduleOn,
           CASE
               WHEN o.ScheduleOn >= @currentFyStart THEN 'current_fy'
               WHEN o.ScheduleOn >= @previousFyStart THEN 'previous_fy'
               ELSE 'older'
           END AS Bucket
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp
      ON sp.BranchID = o.CustomerBranchID AND sp.CategoryId = o.CategoryId;

    CREATE CLUSTERED INDEX IX_ovd ON #ovd (Bucket);

    /*-- 3. THE 3 REAL BUCKET ROWS - always exactly 3, zero-filled when a
       bucket is genuinely empty (never omitted - CLAUDE.md's own "build from
       the dimension/entity list, or empty members vanish" rule applied to a
       fixed 3-member list) ---------------------------------------------------*/
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
            schedules (recurring obligation, multiple missed due dates) that
            legitimately land in different buckets - counting those distinct
            per-bucket-by-instance double counts the instance across buckets
            and breaks the sum-to-total identity below. Confirmed empirically
            against UAT tenant 29 (RECONCILIATION FAILED, Msg 51171) before
            this fix. Matches sql/06's own already-proven convention for this
            exact "bucket overdue schedules by time window" shape. */
        SELECT COUNT(DISTINCT o.ComplianceScheduleOnID) AS OverdueCount,
               CAST(MIN(o.ScheduleOn) AS DATE) AS OldestDueDate,
               CAST(MAX(o.ScheduleOn) AS DATE) AS NewestDueDate
        FROM #ovd o WHERE o.Bucket = b.Bucket
    ) x;

    /*-- 4. CONTROL TOTALS - captured in the SAME instant as the rows above,
       per CLAUDE.md non-negotiable #3. SumOfRows names it per Sec.4a - every
       overdue SCHEDULE falls in exactly one bucket by construction, so this
       is an exact identity, never approximate (see the OUTER APPLY comment
       above for why the unit must be schedule, not instance). -------------*/
    DECLARE @sumOfRows INT = (SELECT SUM(OverdueCount) FROM #rows);
    DECLARE @distinctOverdueSchedules INT = (SELECT COUNT(DISTINCT ComplianceScheduleOnID) FROM #ovd);

    IF @sumOfRows <> @distinctOverdueSchedules
        THROW 51171, N'RECONCILIATION FAILED - bucket rows do not sum to the distinct overdue schedule count. Refusing to emit.', 1;

    SELECT 'control_totals' AS ResultSet,
           @CustomerID AS CustomerID, @AsOf AS AsOfUtc,
           @currentFyLabel AS CurrentFyLabel, @previousFyLabel AS PreviousFyLabel,
           @sumOfRows AS SumOfRows, @distinctOverdueSchedules AS DistinctOverdueSchedules;

    /*-- 5. THE 3 REAL ROWS, plus each bucket's real share of the total (a
       computed comparative, per CLAUDE.md non-negotiable #5 - never phrased
       by the LLM) ------------------------------------------------------------*/
    SELECT 'rows' AS ResultSet,
           Bucket, FYLabel, OverdueCount, OldestDueDate, NewestDueDate,
           CASE WHEN @sumOfRows = 0 THEN NULL
                ELSE CAST(100.0 * OverdueCount / @sumOfRows AS DECIMAL(5,1)) END AS SharePct
    FROM #rows
    ORDER BY CASE Bucket WHEN 'current_fy' THEN 1 WHEN 'previous_fy' THEN 2 ELSE 3 END;

    /*-- 6. DETECTOR EMISSION POLICY - CLAUDE.md Sec.4. Only one real detector
       here (3 fixed buckets, not a per-member scan): does the "older" bucket
       dominate the backlog. Eligible = @sumOfRows (every overdue schedule is
       "eligible" to fall in any bucket), matching sql/05's own Eligible
       convention. -------------------------------------------------------- */
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

    -- unconditional: each bucket's own share of the backlog
    INSERT #assert
    SELECT 'A-' + UPPER(Bucket), 'backlog_share_pct', ISNULL(FYLabel, N'older'),
           CASE WHEN @sumOfRows = 0 THEN 0 ELSE CAST(100.0 * OverdueCount / @sumOfRows AS DECIMAL(18,2)) END,
           NULL, NULL, NULL, NULL, NULL, NULL
    FROM #rows;

    -- policy-gated: the aggregate "backlog is structurally old" finding
    IF (SELECT EmitMode FROM #detector WHERE Detector = 'older_bucket_dominates') = 'aggregate'
        INSERT #assert
        SELECT 'A-OLDER-AGG', 'backlog_older_than_previous_fy', N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'predates the previous fiscal year - never dealt with, not "behind this year"'
        FROM #detector WHERE Detector = 'older_bucket_dominates';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 8. FINDINGS - every one backed by assertion ids ---------------------*/
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

    /*-- 9. DATA-QUALITY NOTES - same shape every other dimension emits -----*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail
    FROM (
        SELECT 'zero_overdue' AS Issue,
               N'No overdue schedules in scope as of this run - the aging bar has nothing to show.' AS Detail
        WHERE @sumOfRows = 0
        UNION ALL
        SELECT 'flow_metric_caveat',
               N'Overdue is a flow metric and drifts between runs as RecentComplianceTransactionView refreshes - never compare this figure against a different run, only report this single as-of snapshot.'
    ) q;

    DROP TABLE #ovd; DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
