/*===========================================================================
  RegTrack Insights - Phase 1b (extension)
  EVIDENCE INTEGRITY DIMENSION - review-trail proxy on closed schedules

  Authored by Claude Code; deployed to production 2026-09-03. Pulled from
  sys.sql_modules and committed here so the repo matches what is running.

  Emits SIX result sets. Error codes 51175-51176 (shared block 5117x).

  -- WHAT IT MEASURES, HONESTLY --------------------------------------------
  Whether a closed schedule shows MORE THAN ONE ComplianceTransaction row - a
  proxy for a recorded performer/reviewer workflow step. It does NOT measure
  whether document evidence was attached: evidence lives in blob storage, not
  this database. EvidenceInSql is always false, and every finding says so.

  -- [FIX 2026-09-04] The original drove FROM dbo.RecentComplianceTransactionView
     as its first table - a full scan of a non-indexed view over 45.7M rows
     before any tenant filter could apply. It cannot complete on production.
     Rewritten to start from the tenant's schedules and resolve latest status
     with the explicit TOP 1 seek (see sql/01). No ScheduleOn window: a
     schedule closed early can be future-dated and must still count.
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

    /*-- 0. PRE-FLIGHT --------------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51175, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. SCOPED CLOSED SCHEDULES + their transaction-row count ------------
       [PERF] Tenant's schedules FIRST, then latest status via index seek.
       A closed schedule by definition has >= 1 transaction, so the APPLY is
       CROSS here (no never-touched case to preserve).                      */
    /*  [PERF - CORRECTED 2026-09-04] SCOPE FIRST via tvfInsightsScopedInstances,
        which already applies the full 2-D (branch x category) scope - so the
        ScopePairs join and its hint both disappear.

        Measured on a 7-branch tenant: deployed 28,518 ms, this 455 ms.
        Do NOT reintroduce INNER HASH JOIN - a join hint forces join ORDER for
        the whole statement, including inside the inlined TVF.                */
    IF OBJECT_ID('tempdb..#scoped') IS NOT NULL DROP TABLE #scoped;
    SELECT s.ComplianceInstanceID INTO #scoped
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;
    CREATE CLUSTERED INDEX IX_scoped ON #scoped (ComplianceInstanceID);

    IF OBJECT_ID('tempdb..#closed') IS NOT NULL DROP TABLE #closed;
    SELECT cso.ID AS ComplianceScheduleOnID,
           txn.TxnRows,
           CASE WHEN txn.TxnRows > 1 THEN 'has_trail' ELSE 'single_row_only' END AS TrailBucket
    INTO #closed
    FROM #scoped sc
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = sc.ComplianceInstanceID
    CROSS APPLY (SELECT TOP 1 t.StatusId FROM ComplianceTransaction t
                 WHERE t.ComplianceScheduleOnID = cso.ID
                 ORDER BY t.Dated DESC, t.ID DESC) lt
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = lt.StatusId
    CROSS APPLY (SELECT COUNT(*) AS TxnRows FROM ComplianceTransaction ct
                 WHERE ct.ComplianceScheduleOnID = cso.ID) txn
    WHERE cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1
      AND d.ClosureClass IN ('completed', 'resolved_terminal');

    /*-- 2. THE 2 REAL BUCKET ROWS - always exactly 2, zero-filled ------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    SELECT b.TrailBucket, ISNULL(x.ScheduleCount, 0) AS ScheduleCount
    INTO #rows
    FROM (VALUES ('has_trail'), ('single_row_only')) AS b(TrailBucket)
    OUTER APPLY (
        SELECT COUNT(DISTINCT cl.ComplianceScheduleOnID) AS ScheduleCount
        FROM #closed cl WHERE cl.TrailBucket = b.TrailBucket
    ) x;

    /*-- 3. CONTROL TOTALS --------------------------------------------------*/
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

    /*-- 5. DETECTOR EMISSION POLICY --------------------------------------*/
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

    IF @reviewTrailPct IS NOT NULL
    INSERT #assert
    SELECT 'A-REVIEWTRAIL', 'review_trail_pct', N'tenant', @reviewTrailPct, NULL, NULL, NULL, NULL, NULL,
           N'proxy for a recorded multi-step workflow, not for document evidence attached - evidence lives in blob storage, not this database';

    IF (SELECT EmitMode FROM #detector WHERE Detector = 'closures_missing_review_trail') = 'aggregate'
        INSERT #assert
        SELECT 'A-MISSING-AGG', 'closures_missing_review_trail', N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'single transaction row only - no visible performer/reviewer workflow step, consistent with a bulk-set or migrated closure'
        FROM #detector WHERE Detector = 'closures_missing_review_trail';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 7. FINDINGS --------------------------------------------------------*/
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

    /*-- 8. DATA-QUALITY NOTES ------------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail
    FROM (
        SELECT 'zero_closed_schedules' AS Issue,
               N'No completed or resolved schedules in scope - review-trail integrity cannot be assessed.' AS Detail
        WHERE @sumOfRows = 0
        UNION ALL
        SELECT 'proxy_not_evidence',
               N'This measures whether ComplianceTransaction shows more than one recorded step for a closure (a review-trail proxy), never whether supporting evidence was actually attached - document evidence lives in blob storage, not in this database. evidence_in_sql is always false.'
    ) q;

    DROP TABLE #scoped; DROP TABLE #closed; DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Evidence integrity dimension installed.';
GO
