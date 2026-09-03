/*===========================================================================
  RegTrack Insights - Phase 1d (paid engine upgrade path)
  EVIDENCE INTEGRITY - closures with a real multi-step review trail

  Spec reference : docs/PAID_TIER_SAMPLE_REFERENCE.md Sec.3.7 (`evidence_integrity`)
  Real UI target : the fixed-holistic Operations tab's Evidence KPI card - see
                   prompts/05_report_html_fixed_holistic.md's own note listing
                   this as a declared-missing tile pending this file.

  [STATUS, 2026-09-02] Deployed to UAT, reconciled against real data (tenant
  29: 1,145 closed schedules, 90.7% with a real trail). NOT yet run against
  >=5 tenants of different profile (CLAUDE.md Sec.11) - do that before
  calling this DONE.

  [FIX, 2026-09-02] Originally emitted only 4 result sets (control_totals, a
  single named 'evidence_integrity' summary row, 'rows', data_quality) - two
  problems: (1) the summary row sat BETWEEN control_totals and rows, so
  position 2 was not 'rows' as IDimensionRepository's fixed 6-result-set
  positional contract requires (see SqlDimensionRepository.ExecuteAsync's own
  ordering comment); (2) no detector_policy/assertions/findings at all.
  Restructured: ClosuresWithReviewTrailPct/EvidenceInSql folded into
  control_totals (same "tenant-level scalar with no row of its own" pattern
  as sql/23's OnTimePctCurrentFY fix), 'rows' (the 2-bucket trail grid) moved
  to position 2, detector_policy/assertions/findings added matching sql/05's
  reference shape - see CLAUDE.md Sec.4.

  [DEFINITION, confirmed against real data, not assumed] Sec.3.7's own field
  is `closures_with_review_trail_pct` - the doc is explicit that
  `evidence_in_sql: false` is "the important field": document evidence lives
  in blob storage, not SQL, so this can ONLY ever measure whether a REVIEW
  TRAIL exists, never whether evidence was actually attached.

  The obvious-looking definition ("count closures whose current StatusId is
  reviewer-authored, per InsightsStatusClassification.RealMeaning") was tried
  first and REJECTED - checked against sql/01's own real seed data: EVERY
  StatusId this dictionary maps to ClosureClass 'completed' or
  'resolved_terminal' (4, 5, 7, 9, 15, 17) already carries "Reviewer-closed" /
  "Reviewer-finalised" / "set by REVIEWER (final)" in its own RealMeaning -
  a closure cannot reach either ClosureClass without reviewer sign-off by
  this dictionary's own construction. Computing the pct off StatusId alone
  would be a trivial, useless 100% every time.

  The real, non-trivial signal instead comes from dbo.ComplianceTransaction -
  a full per-schedule history table (confirmed real: some schedules carry 40+
  rows), not a current-status snapshot. A closure with only ONE transaction
  row on record has no visible in-between step (performer submission, review
  comment, resubmission) - consistent with a bulk-set/migrated closure rather
  than one that passed through the real workflow. "> 1 row" is therefore this
  file's real, verified proxy for "has review trail" - exactly the proxy the
  design doc itself asks to be labelled, never silently upgraded to "evidence
  was attached".

  Grain: per COMPLETED SCHEDULE (ComplianceScheduleOnID) - NOT per instance.
  [TRAP, learned earlier this session building sql/22 and sql/24 - see their
  own header/inline comments] Bucketing DISTINCT ComplianceInstanceID and
  reconciling against a whole-scope DISTINCT ComplianceInstanceID count broke
  the reconciliation the moment one instance had schedules landing in more
  than one bucket. This file has only 2 mutually-exclusive buckets
  (has_trail / single_row_only) that every completed schedule falls into by
  construction, same trap class avoided by counting DISTINCT
  ComplianceScheduleOnID throughout, never ComplianceInstanceID.

  @AsOf is accepted and echoed in control_totals for provenance, matching
  every other dimension proc's signature - but is NOT used as a WHERE bound.
  RecentComplianceTransactionView is already a real-time "current status"
  view (no FY concept applies to a review-trail check), so this proc reports
  the review-trail shape of the tenant's scoped estate as it stands right
  now, not a point-in-time snapshot bounded by @AsOf. [FLAG] if a future need
  arises to reproduce this exact figure from a past run, this decision needs
  revisiting - not needed today.

  Error block: 51170-51179 (same block as sql/22/23/24 - see sql/22's own
  header for the block-numbering note). Next free pair: 51175/51176.

  Target: SQL Server (vitComplianceSystem). IDEMPOTENT (DROP + CREATE).
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_EvidenceIntegrity', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_EvidenceIntegrity;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_EvidenceIntegrity
    @UserID INT, @CustomerID INT, @AsOf DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @AsOf IS NULL SET @AsOf = SYSDATETIME();

    /*-- 0. PRE-FLIGHT - same fail-closed posture as every other dimension --*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51175, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. SCOPED COMPLETED/RESOLVED SCHEDULES + their real transaction-row
       count - the real "has a trail" signal (see header). ------------------*/
    IF OBJECT_ID('tempdb..#closed') IS NOT NULL DROP TABLE #closed;
    SELECT rct.ComplianceScheduleOnID,
           txn.TxnRows,
           CASE WHEN txn.TxnRows > 1 THEN 'has_trail' ELSE 'single_row_only' END AS TrailBucket
    INTO #closed
    FROM dbo.RecentComplianceTransactionView rct
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = rct.ComplianceStatusID
    JOIN dbo.ComplianceScheduleOn cso ON cso.ID = rct.ComplianceScheduleOnID
    JOIN dbo.ComplianceInstance   i   ON i.ID  = cso.ComplianceInstanceID
    JOIN dbo.CustomerBranch       cb  ON cb.ID = i.CustomerBranchID
    JOIN dbo.Compliance           c   ON c.ID  = i.ComplianceID
    JOIN dbo.Act                  a   ON a.ID  = c.ActID
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp
      ON sp.BranchID = cb.ID AND sp.CategoryId = a.ComplianceCategoryId
    CROSS APPLY (
        SELECT COUNT(*) AS TxnRows
        FROM dbo.ComplianceTransaction ct
        WHERE ct.ComplianceScheduleOnID = rct.ComplianceScheduleOnID
    ) txn
    WHERE d.ClosureClass IN ('completed', 'resolved_terminal')
      AND cb.CustomerID = @CustomerID
      AND cb.IsDeleted  = 0
      AND cb.Status     = 1
      AND i.IsDeleted   = 0
      AND c.IsDeleted   = 0;

    /*-- 2. THE 2 REAL BUCKET ROWS - always exactly 2, zero-filled when a
       bucket is genuinely empty (same fixed-list treatment as sql/22's
       buckets and sql/24's windows). ----------------------------------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    SELECT b.TrailBucket, ISNULL(x.ScheduleCount, 0) AS ScheduleCount
    INTO #rows
    FROM (VALUES ('has_trail'), ('single_row_only')) AS b(TrailBucket)
    OUTER APPLY (
        SELECT COUNT(DISTINCT cl.ComplianceScheduleOnID) AS ScheduleCount
        FROM #closed cl WHERE cl.TrailBucket = b.TrailBucket
    ) x;

    /*-- 3. CONTROL TOTALS - captured in the same instant, per CLAUDE.md
       non-negotiable #3. Every completed/resolved schedule falls into
       exactly one of the 2 buckets by construction - an exact identity.
       Carries the tenant-level derived scalars too (ClosuresWithReviewTrailPct,
       EvidenceInSql), same "no row of its own" precedent as sql/23's
       OnTimePctCurrentFY fix. ------------------------------------------------*/
    DECLARE @sumOfRows INT = (SELECT SUM(ScheduleCount) FROM #rows);
    DECLARE @distinctClosedSchedules INT = (SELECT COUNT(DISTINCT ComplianceScheduleOnID) FROM #closed);

    IF @sumOfRows <> @distinctClosedSchedules
        THROW 51176, N'RECONCILIATION FAILED - trail-bucket rows do not sum to the distinct closed-schedule count. Refusing to emit.', 1;

    DECLARE @hasTrailCount INT = (SELECT ScheduleCount FROM #rows WHERE TrailBucket = 'has_trail');
    DECLARE @reviewTrailPct DECIMAL(5,1) = CASE WHEN @sumOfRows = 0 THEN NULL
                                                 ELSE CAST(100.0 * @hasTrailCount / @sumOfRows AS DECIMAL(5,1)) END;

    SELECT 'control_totals' AS ResultSet,
           @CustomerID AS CustomerID, @AsOf AS AsOfUtc,
           @sumOfRows AS SumOfRows, @distinctClosedSchedules AS DistinctClosedSchedules,
           @reviewTrailPct AS ClosuresWithReviewTrailPct,
           CAST(0 AS BIT) AS EvidenceInSql;

    /*-- 4. THE 2 REAL ROWS -----------------------------------------------*/
    SELECT 'rows' AS ResultSet, TrailBucket, ScheduleCount
    FROM #rows
    ORDER BY CASE TrailBucket WHEN 'has_trail' THEN 1 ELSE 2 END;

    /*-- 5. DETECTOR EMISSION POLICY - CLAUDE.md Sec.4. Real signal: the
       share of closures with NO visible workflow trail. Eligible =
       @sumOfRows, same convention as sql/22/24. -----------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector   VARCHAR(40) PRIMARY KEY,
        Eligible   INT,
        Flagged    INT,
        FlaggedPct DECIMAL(5,1),
        EmitMode   VARCHAR(12)
    );
    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'closures_missing_review_trail', @sumOfRows,
           (SELECT ScheduleCount FROM #rows WHERE TrailBucket = 'single_row_only');

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

    -- unconditional: the headline pct itself
    IF @reviewTrailPct IS NOT NULL
    INSERT #assert
    SELECT 'A-REVIEWTRAIL', 'review_trail_pct', N'tenant', @reviewTrailPct, NULL, NULL, NULL, NULL, NULL,
           N'proxy for a recorded multi-step workflow, not for document evidence attached - evidence lives in blob storage, not this database';

    -- policy-gated: the aggregate "meaningful share missing a trail" finding
    IF (SELECT EmitMode FROM #detector WHERE Detector = 'closures_missing_review_trail') = 'aggregate'
        INSERT #assert
        SELECT 'A-MISSING-AGG', 'closures_missing_review_trail', N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'single transaction row only - no visible performer/reviewer workflow step, consistent with a bulk-set or migrated closure'
        FROM #detector WHERE Detector = 'closures_missing_review_trail';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 7. FINDINGS - every one backed by assertion ids ---------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (
        FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(300), AssertionIds VARCHAR(400),
        NarrativeGuard NVARCHAR(300) NULL
    );

    INSERT #find
    SELECT 'F-MISSING-AGG', 'medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' closed schedule(s) (', VsComparatorPP,
                  N'%) have no recorded review trail'),
           AssertionId,
           N'This measures ComplianceTransaction row count only, never whether evidence was actually attached - evidence_in_sql is always false. Do not present as an evidence-attachment finding.'
    FROM #assert WHERE AssertionId = 'A-MISSING-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 8. DATA-QUALITY NOTES - same shape every other dimension emits -----*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail
    FROM (
        SELECT 'zero_closed_schedules' AS Issue,
               N'No completed or resolved schedules in scope - review-trail integrity cannot be assessed.' AS Detail
        WHERE @sumOfRows = 0
        UNION ALL
        SELECT 'proxy_not_evidence',
               N'This measures whether ComplianceTransaction shows more than one recorded step for a closure (a review-trail proxy), never whether supporting evidence was actually attached - document evidence lives in blob storage, not in this database. evidence_in_sql is always false.'
    ) q;

    DROP TABLE #closed; DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
