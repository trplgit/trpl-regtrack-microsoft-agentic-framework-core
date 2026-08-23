/*===========================================================================
  RegTrack Insights - Phase 1b, Step 4
  NATURE DIMENSION

  Spec reference : docs/DIMENSION_SPECS.md section 2
  Pattern        : sql/05_dimension_location.sql is the reference implementation.
  Emits SIX result sets. Error block 51070-51079.

  -- [TRAP] THE UNCATEGORISED GAP IS MANDATORY TO DECLARE --------------------
  The spec's "~49% Others" is TWO populations, not one: obligations tagged with
  the Others (17) catch-all, AND obligations carrying no nature at all. On the
  reference tenant that is 1,184 + 1,813 = ~50% of scope. Counting only the
  Others bucket reports ~20% and understates the blindness by half.
  The dimension is HALF-BLIND until BA reclassification lands (open item O-3).
  A data_quality entry covering BOTH is MANDATORY - never present a nature chart
  whose largest segment is a meaningless bucket without it.

  -- THE CROSS-TAB ------------------------------------------------------------
  The spec asks for a "nature x penalty-type cross-tab" because each nature's
  risk TYPE differs: financial, personal-liability, inspector-visible. That is
  expressed as columns on the rows (nature = rows, penalty type = columns),
  which IS the cross-tab, rather than a seventh result set - the common contract
  fixes the shape at six and the .NET layer reads them positionally.

  -- MEMBER LIST -------------------------------------------------------------
  Rows come from the NatureOfCompliance master, so a nature carrying nothing
  still appears, plus any RETIRED nature that still carries obligations - a
  nature can be soft-deleted while instances tagged with it remain, and dropping
  it would lose those instances and THROW on correct data.

  -- [TRAP] UNTAGGED IS NOT THE SAME AS ORPHANED -----------------------------
  An instance with NatureId NULL is a CONFIGURATION GAP: counted separately,
  added back for reconciliation, and DECLARED - the same treatment sql/10 gives
  a NULL DepartmentID. Never silently dropped, never invented into a fake
  "Untagged" nature a narrator could name as if it existed.
  An instance whose NatureId is NOT NULL but matches no member is a REFERENTIAL
  BREAK - a different thing entirely - and stays fatal.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_Nature', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_Nature;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Nature
    @UserID      INT,
    @CustomerID  INT,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT --------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51070, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    DECLARE @criticalRisk INT = (
        SELECT TRY_CAST(p.RawValue AS INT)
        FROM dbo.InsightsEnumPolarity p
        JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
        WHERE p.Semantic = 'RiskType' AND p.Meaning LIKE N'Critical%');

    IF @criticalRisk IS NULL
        THROW 51072, N'DICTIONARY GAP - no RiskType value is mapped to Critical in InsightsEnumPolarity. Refusing to compute.', 1;

    /*-- 1. SCOPED INSTANCE BASE ----------------------------------------*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.RiskType,
        s.Imprisonment,
        s.NatureOfCompliance                                    AS NatureId,
        CASE WHEN ISNULL(c.FixedMinimum,0)              > 0
                OR ISNULL(c.FixedMaximum,0)             > 0
                OR ISNULL(c.VariableAmountPerDay,0)     > 0
                OR ISNULL(c.VariableAmountPerMonth,0)   > 0
                OR ISNULL(c.VariableAmountPerInstance,0)> 0
             THEN 1 ELSE 0 END                                  AS HasFinancialPenalty,
        CASE WHEN c.IsForcefulClosure = 1 THEN 1 ELSE 0 END      AS HasClosureRisk
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
    JOIN Compliance c ON c.ID = s.ComplianceID;

    CREATE CLUSTERED INDEX IX_inst ON #inst (NatureId, ComplianceInstanceID);

    /*-- 2. OVERDUE ------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;

    /*-- 3. OWNERSHIP ----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#owned') IS NOT NULL DROP TABLE #owned;
    SELECT DISTINCT ca.ComplianceInstanceID
    INTO #owned
    FROM ComplianceAssignment ca
    JOIN #inst i ON i.ComplianceInstanceID = ca.ComplianceInstanceID
    WHERE ca.RoleID = 3 AND ca.UserID > 0;

    /*-- 4. MEMBER LIST = the nature master, plus retired natures that still
       carry obligations. See the trap note in the header.                */
    IF OBJECT_ID('tempdb..#nature') IS NOT NULL DROP TABLE #nature;
    SELECT n.ID AS NatureId, n.Name AS NatureName,
           CAST(CASE WHEN n.IsDeleted = 1 THEN 1 ELSE 0 END AS BIT) AS IsRetired
    INTO #nature
    FROM NatureOfCompliance n
    WHERE n.IsDeleted = 0
       OR EXISTS (SELECT 1 FROM #inst i WHERE i.NatureId = n.ID);

    IF NOT EXISTS (SELECT 1 FROM #nature)
        THROW 51072, N'MASTER DATA GAP - NatureOfCompliance master is empty. Refusing to compute a nature dimension.', 1;

    /*-- 5. ROWS ---------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        NatureId                 INT            NOT NULL PRIMARY KEY,
        NatureName               NVARCHAR(300)  NULL,
        IsRetired                BIT            NOT NULL,
        Instances                INT            NOT NULL,
        Overdue                  INT            NOT NULL,
        OverduePct               DECIMAL(5,1)   NULL,
        Ownerless                INT            NOT NULL,
        ImprisonmentInstances    INT            NOT NULL,
        ImprisonmentOverdue      INT            NOT NULL,
        CriticalInstances        INT            NOT NULL,
        BranchesCovered          INT            NOT NULL,
        PenaltyBearingInstances  INT            NOT NULL,
        -- the nature x penalty-type cross-tab
        FinancialPenaltyInstances INT           NOT NULL,
        ClosureRiskInstances     INT            NOT NULL,
        -- derived
        ImprisonmentSharePct     DECIMAL(5,1)   NULL,
        OverdueRank              INT            NULL,
        Flags                    VARCHAR(200)   NULL
    );

    INSERT #rows (NatureId, NatureName, IsRetired, Instances, Overdue, Ownerless,
                  ImprisonmentInstances, ImprisonmentOverdue, CriticalInstances,
                  BranchesCovered, PenaltyBearingInstances,
                  FinancialPenaltyInstances, ClosureRiskInstances)
    SELECT
        n.NatureId, n.NatureName, n.IsRetired,
        COUNT(i.ComplianceInstanceID),
        SUM(CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.ComplianceInstanceID IS NOT NULL AND w.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 AND o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.RiskType = @criticalRisk THEN 1 ELSE 0 END),
        COUNT(DISTINCT i.BranchID),
        SUM(CASE WHEN i.HasFinancialPenalty = 1 OR i.Imprisonment = 1 OR i.HasClosureRisk = 1 THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.HasFinancialPenalty = 1 THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.HasClosureRisk = 1 THEN 1 ELSE 0 END)
    FROM #nature n
    LEFT JOIN #inst  i ON i.NatureId = n.NatureId
    LEFT JOIN #ovd   o ON o.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN #owned w ON w.ComplianceInstanceID = i.ComplianceInstanceID
    GROUP BY n.NatureId, n.NatureName, n.IsRetired;

    /*-- 6. RECONCILIATION, with the untagged bucket counted back --------*/
    DECLARE @rowSum      INT = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #inst);
    DECLARE @untagged    INT = (SELECT COUNT(*) FROM #inst WHERE NatureId IS NULL);
    DECLARE @orphan      INT = (SELECT COUNT(*) FROM #inst i
                                WHERE i.NatureId IS NOT NULL
                                  AND NOT EXISTS (SELECT 1 FROM #nature n WHERE n.NatureId = i.NatureId));

    IF @orphan > 0
        THROW 51071, N'NATURE DIMENSION RECONCILIATION FAILED - an instance carries a NatureOfCompliance absent from the master. This is a referential break, not a tagging gap. Refusing to publish.', 1;

    IF @rowSum + @untagged <> @scopedTotal
        THROW 51071, N'NATURE DIMENSION RECONCILIATION FAILED - per-nature sums plus the untagged bucket do not tie to the scoped instance total. Refusing to publish.', 1;

    DECLARE @hasAnyObligations BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * (SELECT COUNT(*) FROM #ovd) / @scopedTotal END;
    DECLARE @tenantImpSharePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT COUNT(*) FROM #inst WHERE Imprisonment = 1) / @scopedTotal END;

    /*  The uncategorised gap is the Others bucket PLUS the untagged instances.
        Reporting either alone halves the apparent blindness - see the header. */
    DECLARE @othersInstances INT =
        (SELECT ISNULL(SUM(Instances),0) FROM #rows WHERE NatureName LIKE N'Other%');
    DECLARE @uncat    INT = @othersInstances + @untagged;
    DECLARE @uncatPct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * @uncat / @scopedTotal END;

    UPDATE #rows SET
        OverduePct           = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END,
        ImprisonmentSharePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * ImprisonmentInstances / Instances END;

    /*  Materiality floor on ranking - see the trap note in sql/05. A nature with
        one overdue obligation must not outrank one with 900 of 1,000.          */
    DECLARE @materialityFloor INT = 50;
    DECLARE @materialMembers  INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @materialityFloor);
    DECLARE @rankDegraded     BIT = CASE WHEN @materialMembers < 2 THEN 1 ELSE 0 END;
    DECLARE @rankFloor        INT = CASE WHEN @rankDegraded = 1 THEN 1 ELSE @materialityFloor END;

    ;WITH r AS (SELECT NatureId, RANK() OVER (ORDER BY OverduePct DESC) AS rk
                FROM #rows WHERE Instances >= @rankFloor)
    UPDATE #rows SET OverdueRank = r.rk FROM #rows JOIN r ON r.NatureId = #rows.NatureId;

    /*-- 7. DETECTIONS - both peer-relative to this tenant ---------------*/
    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyObligations = 1 AND Instances >= @rankFloor
                  AND OverduePct > @tenantOverduePct
                 THEN ',worst_nature_by_overdue' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND Instances >= @rankFloor
                  AND ImprisonmentInstances > 0
                  AND ImprisonmentSharePct > @tenantImpSharePct
                 THEN ',imprisonment_lineage' ELSE '' END
        , 1, 1, '');

    SELECT
        'control_totals'             AS ResultSet,
        @scopedTotal                 AS ScopedInstances,
        @rowSum                      AS CategorisedInstances   /* + UntaggedInstances = ScopedInstances.
                             Renamed from SumOfRows: rows cover only instances WITH a
                             nature, so a field called SumOfRows compared against
                             ScopedInstances reads as a gap when it is a declared residual. */,
        CAST(1 AS BIT)               AS Reconciled,
        (SELECT COUNT(*) FROM #ovd)  AS OverdueInstances,
        @tenantOverduePct            AS TenantOverduePct,
        @tenantImpSharePct           AS TenantImprisonmentSharePct,
        (SELECT COUNT(*) FROM #rows) AS NaturesReported,
        (SELECT COUNT(*) FROM #rows WHERE Instances > 0) AS NaturesWithObligations,
        (SELECT COUNT(*) FROM #rows WHERE IsRetired = 1)  AS RetiredNaturesStillInUse,
        @othersInstances             AS OthersBucketInstances,
        @untagged                    AS UntaggedInstances,
        @uncat                       AS UncategorisedInstances,
        @uncatPct                    AS UncategorisedPct;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*-- 8. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @material INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'worst_nature_by_overdue', @material,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%worst_nature_by_overdue%')
    UNION ALL SELECT 'imprisonment_lineage', @material,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%imprisonment_lineage%');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;

    /*  See sql/05 - below the top-5 cap, aggregating cannot reduce output and
        only loses the member names. */
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 9. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    INSERT #assert VALUES ('A-TENANT','overdue_pct',N'tenant',@tenantOverduePct,NULL,NULL,NULL,NULL,NULL,NULL);

    DECLARE @rankable  INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor);
    DECLARE @tiedAtTop INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor AND OverdueRank = 1);

    /*  The uncategorised bucket must never be named "the worst nature" - it is
        not a nature, it is the absence of one. Excluded from the ranking claim
        and declared separately in data_quality.                                */
    IF @rankable >= 2
    INSERT #assert
    SELECT TOP 1 'A-WORST-NATURE','overdue_pct',NatureName,OverduePct,OverdueRank,@rankable,
           @tenantOverduePct, OverduePct - @tenantOverduePct,
           CASE WHEN OverduePct > @tenantOverduePct THEN 'worse' ELSE 'better' END,
           NULLIF(CONCAT(
               CASE WHEN @rankDegraded = 1
                    THEN CONCAT(N'degraded_ranking_sample: no nature reaches the ', @materialityFloor,
                                N'-instance materiality floor. ') ELSE N'' END,
               CASE WHEN @tiedAtTop > 1
                    THEN CONCAT(N'tied_at_top: ', @tiedAtTop, N' natures share this rate - not uniquely the highest. ')
                    ELSE N'' END), N'')
    FROM #rows
    WHERE Instances >= @rankFloor AND NatureName NOT LIKE N'Other%'
    ORDER BY OverduePct DESC, Instances DESC;

    IF (SELECT EmitMode FROM #detector WHERE Detector='imprisonment_lineage') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-IMPLIN-' + CAST(ROW_NUMBER() OVER (ORDER BY ImprisonmentInstances DESC) AS VARCHAR(5)),
               'imprisonment_share_pct', NatureName, ImprisonmentSharePct, NULL, NULL,
               @tenantImpSharePct, ImprisonmentSharePct - @tenantImpSharePct, 'worse',
               N'imprisonment_lineage: this kind of obligation carries personal liability more often than the tenant average'
        FROM #rows WHERE Flags LIKE '%imprisonment_lineage%' ORDER BY ImprisonmentInstances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='imprisonment_lineage') = 'aggregate'
        INSERT #assert
        SELECT 'A-IMPLIN-AGG','natures_with_imprisonment_lineage',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - personal liability is spread across many kinds of obligation'
        FROM #detector WHERE Detector='imprisonment_lineage';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS ----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-WORST-NATURE','high',
           CONCAT(N'', ScopeLabel, N' has the highest overdue rate at ', Value, N'%'),
           'A-WORST-NATURE,A-TENANT',
           N'State the uncategorised-nature caveat wherever nature is discussed - see data_quality.'
    FROM #assert WHERE AssertionId = 'A-WORST-NATURE' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-IMPLIN','high',
           CONCAT(N'', ScopeLabel, N' carries personal liability on ', Value, N'% of its obligations'),
           AssertionId,
           N'Do not present this as a separate exposure from Critical risk without checking the overlap - see the Risk dimension.'
    FROM #assert WHERE AssertionId LIKE 'A-IMPLIN-[0-9]%';

    INSERT #find
    SELECT 'F-IMPLIN-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' kinds of obligation (', VsComparatorPP,
                  N'%) carry personal liability above the tenant average'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-IMPLIN-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY - the uncategorised gap is MANDATORY ------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'nature_uncategorised_gap',
               CONCAT(N'', @uncat, N' obligation(s), ', @uncatPct, N'% of this scope, carry no usable nature - '
                    , @othersInstances, N' tagged with the Others catch-all and ', @untagged, N' carrying no '
                    + N'nature at all. This dimension is partially blind: any statement about which KIND of '
                    + N'obligation is failing excludes them. Reclassification is open item O-3. '
                    + N'Never present a nature breakdown without this caveat.')
        WHERE @uncat > 0
        UNION ALL
        SELECT 'nature_untagged',
               CONCAT(N'', @untagged, N' obligation(s) in this scope carry no nature at all. They are counted '
                    + N'in the tenant total but appear in NO nature row, so every per-nature figure below '
                    + N'excludes them and the rows do not sum to the scoped total. A configuration gap, '
                    + N'not a defect.')
        WHERE @untagged > 0
        UNION ALL
        SELECT 'nature_retired_still_in_use',
               CONCAT(N'', COUNT(*), N' retired nature(s) still carry obligations in this scope. They are '
                    + N'reported as members flagged IsRetired rather than dropped, which would lose their '
                    + N'instances.')
        FROM #rows WHERE IsRetired = 1 HAVING COUNT(*) > 0
        UNION ALL
        SELECT 'natures_unused',
               CONCAT(N'', COUNT(*), N' nature(s) in the master carry no obligations in this scope and are '
                    + N'reported with zero counts rather than omitted.')
        FROM #rows WHERE Instances = 0 HAVING COUNT(*) > 0
    ) q;

    DROP TABLE #inst; DROP TABLE #ovd; DROP TABLE #owned; DROP TABLE #nature;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Nature dimension installed.';
GO
