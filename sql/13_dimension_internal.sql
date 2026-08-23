/*===========================================================================
  RegTrack Insights - Phase 1b, Step 8
  STATUTORY vs INTERNAL DIMENSION

  Spec reference : docs/DIMENSION_SPECS.md section 7  [LOCKED - full dimension]
  Pattern        : sql/05_dimension_location.sql. Emits SIX result sets.
  Error block    : 51110-51119

  -- [TRAP RESOLVED] INTERNAL STATUSES DO SHARE THE DICTIONARY ---------------
  The spec warned that internal statuses may not use the same dictionary and
  said VERIFY before reusing vInsightsStatusCurrent. Verified against production
  on 19 Aug: every InternalComplianceTransaction.StatusId maps to a
  ComplianceStatus row (1, 2, 3, 4, 5, 6, 7, 10, 11, 14, 15, 16, 17, 18, 19, 21)
  - EXCEPT 27 rows carrying -1 or 0, which map to nothing.

  So the dictionary IS reused, and those unmapped rows are DECLARED rather than
  silently excluded, mirroring the proportionate rule in
  usp_Insights_StatusDataQuality. Never silently incomplete.

  -- THE FINDING IS STRUCTURAL, NOT QUANTITATIVE ----------------------------
  On the reference tenant all 240 internal instances sat on 5 branches of ONE
  division. The other division - higher overdue, most of the monetary exposure -
  had ZERO internal compliance configured. That is a governance-maturity signal
  invisible in any statutory-only report, and it is why rows are per BRANCH with
  the two populations side by side rather than two separate summaries.

  -- SCOPE: INTERNAL HAS NO CATEGORY AXIS ----------------------------------
  InternalComplianceInstance carries a branch and a department but no compliance
  category, so the 2-D (branch x category) scope pair set cannot constrain it.
  Internal is therefore scoped on the BRANCH axis only, taken from the same
  authorised pair set. That is a genuine widening relative to the statutory cut
  and is declared in data_quality - a user scoped to one category sees internal
  work for all categories at their authorised branches.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_Internal', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_Internal;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Internal
    @UserID      INT,
    @CustomerID  INT,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT --------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51110, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. THE TWO POPULATIONS -----------------------------------------*/
    IF OBJECT_ID('tempdb..#branch') IS NOT NULL DROP TABLE #branch;
    SELECT DISTINCT sp.BranchID
    INTO #branch
    FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp;

    IF OBJECT_ID('tempdb..#stat') IS NOT NULL DROP TABLE #stat;
    SELECT s.ComplianceInstanceID, s.BranchID
    INTO #stat
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;

    IF OBJECT_ID('tempdb..#statOvd') IS NOT NULL DROP TABLE #statOvd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #statOvd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #stat s ON s.ComplianceInstanceID = o.ComplianceInstanceID;

    IF OBJECT_ID('tempdb..#statOwn') IS NOT NULL DROP TABLE #statOwn;
    SELECT DISTINCT ca.ComplianceInstanceID
    INTO #statOwn
    FROM ComplianceAssignment ca
    JOIN #stat s ON s.ComplianceInstanceID = ca.ComplianceInstanceID
    WHERE ca.RoleID = 3 AND ca.UserID > 0;

    /*  Internal - branch-axis scoping only, see the header. */
    IF OBJECT_ID('tempdb..#int') IS NOT NULL DROP TABLE #int;
    SELECT ii.ID AS InternalInstanceID, ii.CustomerBranchID AS BranchID
    INTO #int
    FROM InternalComplianceInstance ii
    JOIN CustomerBranch cb ON cb.ID = ii.CustomerBranchID
    JOIN #branch b ON b.BranchID = ii.CustomerBranchID
    WHERE ii.IsDeleted = 0 AND cb.IsDeleted = 0 AND cb.CustomerID = @CustomerID;

    /*  Internal overdue, using the SAME dictionary - verified, see header.
        The INNER JOIN is deliberate: a status the dictionary does not know
        yields no row and is counted separately as an unmapped gap.        */
    IF OBJECT_ID('tempdb..#intOvd') IS NOT NULL DROP TABLE #intOvd;
    SELECT DISTINCT i.InternalInstanceID
    INTO #intOvd
    FROM #int i
    JOIN InternalComplianceScheduledOn iso ON iso.InternalComplianceInstanceID = i.InternalInstanceID
    JOIN InternalComplianceTransaction it  ON it.InternalComplianceScheduledOnID = iso.ID
    JOIN dbo.vInsightsStatusCurrent d      ON d.StatusId = it.StatusId
    WHERE iso.IsActive = 1 AND iso.IsUpcomingNotDeleted = 1
      AND iso.ScheduledOn <= @AsOf
      AND d.OverdueEligible = 1;

    DECLARE @intUnmapped INT = (
        SELECT COUNT(*)
        FROM #int i
        JOIN InternalComplianceScheduledOn iso ON iso.InternalComplianceInstanceID = i.InternalInstanceID
        JOIN InternalComplianceTransaction it  ON it.InternalComplianceScheduledOnID = iso.ID
        WHERE NOT EXISTS (SELECT 1 FROM dbo.vInsightsStatusCurrent d WHERE d.StatusId = it.StatusId));

    IF OBJECT_ID('tempdb..#intOwn') IS NOT NULL DROP TABLE #intOwn;
    SELECT DISTINCT ia.InternalComplianceInstanceID AS InternalInstanceID
    INTO #intOwn
    FROM InternalComplianceAssignment ia
    JOIN #int i ON i.InternalInstanceID = ia.InternalComplianceInstanceID
    WHERE ia.RoleID = 3 AND ia.UserID > 0;

    /*-- 2. ROWS - one per branch, both populations side by side ---------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        BranchID            INT            NOT NULL PRIMARY KEY,
        BranchName          NVARCHAR(300)  NULL,
        ApexName            NVARCHAR(400)  NULL,
        StatutoryInstances  INT            NOT NULL,
        StatutoryOverdue    INT            NOT NULL,
        StatutoryOwnerless  INT            NOT NULL,
        InternalInstances   INT            NOT NULL,
        InternalOverdue     INT            NOT NULL,
        InternalOwnerless   INT            NOT NULL,
        -- derived
        StatutoryOwnerlessPct DECIMAL(5,1) NULL,
        InternalOwnerlessPct  DECIMAL(5,1) NULL,
        Flags               VARCHAR(200)   NULL
    );

    INSERT #rows (BranchID, BranchName, ApexName, StatutoryInstances, StatutoryOverdue,
                  StatutoryOwnerless, InternalInstances, InternalOverdue, InternalOwnerless)
    SELECT
        t.BranchID, t.BranchName, t.ApexName,
        ISNULL(s.Inst,0), ISNULL(s.Ovd,0), ISNULL(s.Own,0),
        ISNULL(n.Inst,0), ISNULL(n.Ovd,0), ISNULL(n.Own,0)
    FROM dbo.tvfInsightsEntityTree(@CustomerID) t
    LEFT JOIN (
        SELECT st.BranchID,
               COUNT(*) AS Inst,
               SUM(CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END) AS Ovd,
               SUM(CASE WHEN w.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END)     AS Own
        FROM #stat st
        LEFT JOIN #statOvd o ON o.ComplianceInstanceID = st.ComplianceInstanceID
        LEFT JOIN #statOwn w ON w.ComplianceInstanceID = st.ComplianceInstanceID
        GROUP BY st.BranchID) s ON s.BranchID = t.BranchID
    LEFT JOIN (
        SELECT it.BranchID,
               COUNT(*) AS Inst,
               SUM(CASE WHEN o.InternalInstanceID IS NOT NULL THEN 1 ELSE 0 END) AS Ovd,
               SUM(CASE WHEN w.InternalInstanceID IS NULL THEN 1 ELSE 0 END)     AS Own
        FROM #int it
        LEFT JOIN #intOvd o ON o.InternalInstanceID = it.InternalInstanceID
        LEFT JOIN #intOwn w ON w.InternalInstanceID = it.InternalInstanceID
        GROUP BY it.BranchID) n ON n.BranchID = t.BranchID;

    /*-- 3. RECONCILIATION - both populations, independently -------------*/
    DECLARE @statTotal   INT = (SELECT COUNT(*) FROM #stat);
    DECLARE @statRowSum  INT = (SELECT ISNULL(SUM(StatutoryInstances),0) FROM #rows);
    DECLARE @intTotal    INT = (SELECT COUNT(*) FROM #int);
    DECLARE @intRowSum   INT = (SELECT ISNULL(SUM(InternalInstances),0) FROM #rows);

    IF @statRowSum <> @statTotal
        THROW 51111, N'INTERNAL DIMENSION RECONCILIATION FAILED - per-branch statutory sums do not tie to the scoped statutory total. Refusing to publish.', 1;

    IF @intRowSum <> @intTotal
        THROW 51111, N'INTERNAL DIMENSION RECONCILIATION FAILED - per-branch internal sums do not tie to the scoped internal total. Refusing to publish.', 1;

    DECLARE @hasAnyObligations BIT = CASE WHEN @statTotal > 0 OR @intTotal > 0 THEN 1 ELSE 0 END;

    UPDATE #rows SET
        StatutoryOwnerlessPct = CASE WHEN StatutoryInstances = 0 THEN NULL
                                     ELSE 100.0 * StatutoryOwnerless / StatutoryInstances END,
        InternalOwnerlessPct  = CASE WHEN InternalInstances  = 0 THEN NULL
                                     ELSE 100.0 * InternalOwnerless  / InternalInstances  END;

    /*-- 4. DETECTIONS ---------------------------------------------------*/
    DECLARE @statOwnPct DECIMAL(5,1) =
        CASE WHEN @statTotal = 0 THEN NULL
             ELSE 100.0 * (SELECT ISNULL(SUM(StatutoryOwnerless),0) FROM #rows) / @statTotal END;
    DECLARE @intOwnPct DECIMAL(5,1) =
        CASE WHEN @intTotal = 0 THEN NULL
             ELSE 100.0 * (SELECT ISNULL(SUM(InternalOwnerless),0) FROM #rows) / @intTotal END;

    UPDATE #rows SET Flags =
        STUFF(
            /*  The structural finding: a branch running statutory compliance with
                no internal governance configured at all. */
            CASE WHEN @hasAnyObligations = 1 AND StatutoryInstances > 0 AND InternalInstances = 0
                 THEN ',internal_coverage_gap' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND InternalInstances > 0
                  AND @statOwnPct IS NOT NULL AND InternalOwnerlessPct > @statOwnPct
                 THEN ',internal_ownerless_rate' ELSE '' END
        , 1, 1, '');

    DECLARE @internalAbsent BIT = CASE WHEN @statTotal > 0 AND @intTotal = 0 THEN 1 ELSE 0 END;

    SELECT
        'control_totals'                AS ResultSet,
        @statTotal                      AS ScopedInstances,
        @statRowSum                     AS SumOfRows,
        CAST(1 AS BIT)                  AS Reconciled,
        @intTotal                       AS InternalInstances,
        @intRowSum                      AS SumOfInternalRows,
        (SELECT COUNT(*) FROM #statOvd) AS StatutoryOverdueInstances,
        (SELECT COUNT(*) FROM #intOvd)  AS InternalOverdueInstances,
        @statOwnPct                     AS StatutoryOwnerlessPct,
        @intOwnPct                      AS InternalOwnerlessPct,
        (SELECT COUNT(*) FROM #rows WHERE StatutoryInstances > 0) AS BranchesWithStatutory,
        (SELECT COUNT(*) FROM #rows WHERE InternalInstances  > 0) AS BranchesWithInternal,
        @internalAbsent                 AS InternalAbsentEntirely,
        @intUnmapped                    AS InternalUnmappedStatusRows;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY StatutoryInstances DESC;

    /*-- 5. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @withStat INT = (SELECT COUNT(*) FROM #rows WHERE StatutoryInstances > 0);
    DECLARE @withInt  INT = (SELECT COUNT(*) FROM #rows WHERE InternalInstances  > 0);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'internal_coverage_gap', @withStat,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%internal_coverage_gap%')
    UNION ALL SELECT 'internal_ownerless_rate', @withInt,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%internal_ownerless_rate%');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 6. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    IF @statOwnPct IS NOT NULL
    INSERT #assert VALUES ('A-STAT-OWN','ownerless_pct',N'statutory',@statOwnPct,NULL,@statTotal,NULL,NULL,NULL,NULL);

    IF @intOwnPct IS NOT NULL
    INSERT #assert VALUES ('A-INT-OWN','ownerless_pct',N'internal',@intOwnPct,NULL,@intTotal,
                           @statOwnPct, @intOwnPct - @statOwnPct,
                           CASE WHEN @intOwnPct > @statOwnPct THEN 'worse' ELSE 'better' END, NULL);

    /*  Coverage: the structural contrast. Branch counts, not instance counts -
        the finding is WHERE internal governance runs, not how much of it. */
    INSERT #assert
    VALUES ('A-COVER','branches_with_internal',N'tenant',@withInt,NULL,@withStat,NULL,NULL,NULL,
            N'branch counts, not instance counts - the finding is WHERE internal governance runs');

    IF @internalAbsent = 1
    INSERT #assert
    VALUES ('A-ABSENT','internal_instances',N'tenant',0,NULL,@statTotal,NULL,NULL,NULL,
            N'internal_absent_entirely: statutory compliance is running with no internal governance configured anywhere in this scope');

    IF (SELECT EmitMode FROM #detector WHERE Detector='internal_coverage_gap') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-GAP-' + CAST(ROW_NUMBER() OVER (ORDER BY StatutoryInstances DESC) AS VARCHAR(5)),
               'statutory_instances_without_internal', BranchName, StatutoryInstances, NULL, NULL,
               NULL, NULL, NULL,
               N'internal_coverage_gap: statutory obligations tracked here, no internal governance configured'
        FROM #rows WHERE Flags LIKE '%internal_coverage_gap%' ORDER BY StatutoryInstances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='internal_coverage_gap') = 'aggregate'
        INSERT #assert
        SELECT 'A-GAP-AGG','branches_without_internal',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL,
               N'aggregate - internal governance is configured on a minority of the estate'
        FROM #detector WHERE Detector='internal_coverage_gap';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 7. FINDINGS -----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-ABSENT','high',
           N'No internal compliance is configured anywhere in this scope',
           'A-ABSENT',
           N'State it as a governance-maturity observation, not a breach. Internal compliance is self-imposed - its absence is a choice the tenant may have made deliberately. Confirm before treating it as a gap.'
    FROM #assert WHERE AssertionId = 'A-ABSENT';

    INSERT #find
    SELECT 'F-COVER','high',
           CONCAT(N'Internal governance runs at ', CAST(Value AS INT), N' of ', OfN,
                  N' locations that carry statutory obligations'),
           'A-COVER',
           N'The finding is structural, not quantitative: WHICH parts of the estate have internal governance and which do not. Do not reduce it to a volume comparison.'
    FROM #assert WHERE AssertionId = 'A-COVER' AND OfN > 0 AND Value < OfN;

    INSERT #find
    SELECT 'F-INT-OWN','high',
           CONCAT(N'Internal obligations are unassigned at ', Value, N'%, against ',
                  ComparatorValue, N'% for statutory'),
           'A-INT-OWN,A-STAT-OWN',
           N'Do not present internal and statutory as one population - they are separately configured and separately owned.'
    FROM #assert WHERE AssertionId = 'A-INT-OWN' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-GAP','medium',
           CONCAT(N'', ScopeLabel, N' carries ', CAST(Value AS INT),
                  N' statutory obligation(s) with no internal governance configured'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-GAP-[0-9]%';

    INSERT #find
    SELECT 'F-GAP-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' locations (', VsComparatorPP,
                  N'%) run statutory compliance with no internal governance'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-GAP-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 8. DATA QUALITY -------------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'internal_scope_is_branch_only',
               N'InternalComplianceInstance carries no compliance category, so the internal population is '
             + N'constrained on the BRANCH axis only while the statutory population is constrained on both '
             + N'branch AND category. The internal figures are therefore a wider cut than the statutory '
             + N'ones and the two are not strictly like-for-like.'
        UNION ALL
        SELECT 'internal_unmapped_status',
               CONCAT(N'', @intUnmapped, N' internal transaction(s) carry a status absent from the '
                    + N'classification dictionary and are excluded from the internal overdue figure. '
                    + N'Declared, not silently dropped.')
        WHERE @intUnmapped > 0
        UNION ALL
        SELECT 'internal_absent',
               N'No internal compliance instances exist in this scope, so every internal figure is zero '
             + N'by absence rather than by measurement. Nothing about internal governance quality can be '
             + N'inferred from this report.'
        WHERE @intTotal = 0
    ) q;

    DROP TABLE #branch; DROP TABLE #stat; DROP TABLE #statOvd; DROP TABLE #statOwn;
    DROP TABLE #int; DROP TABLE #intOvd; DROP TABLE #intOwn;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Statutory vs Internal dimension installed.';
GO
