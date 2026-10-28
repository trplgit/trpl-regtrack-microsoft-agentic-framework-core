/*===========================================================================
  ROLLBACK - restores the exact definitions PRODUCTION ran on 2026-10-27,
  dumped read-only (OBJECT_DEFINITION) from 10.224.254.4 that day, before
  01-03 of this package. Run only to undo this package.
===========================================================================*/
SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Insights_Dimension_Risk
    @UserID      INT,
    @CustomerID  INT,
    @WindowStart DATETIME,
    @WindowEnd   DATETIME,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51060, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    IF OBJECT_ID('tempdb..#risk') IS NOT NULL DROP TABLE #risk;
    SELECT TRY_CAST(p.RawValue AS INT) AS RiskType,
           p.Meaning                   AS RiskLabel
    INTO #risk
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'RiskType' AND TRY_CAST(p.RawValue AS INT) IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM #risk)
        THROW 51065, N'DICTIONARY GAP - no RiskType values are mapped in InsightsEnumPolarity. Refusing to compute a risk dimension.', 1;

    DECLARE @criticalRisk INT = (SELECT TOP 1 RiskType FROM #risk WHERE RiskLabel LIKE N'Critical%');

    IF @criticalRisk IS NULL
        THROW 51066, N'DICTIONARY GAP - no RiskType value is mapped to Critical in InsightsEnumPolarity. Refusing to compute.', 1;

    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.RiskType,
        s.Imprisonment
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;

    CREATE CLUSTERED INDEX IX_inst ON #inst (RiskType, ComplianceInstanceID);

    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51062, N'RISK DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51062, N'RISK DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    /*===================================================================
      OCCURRENCE-LEVEL ACTIVE SCHEDULES [CHANGED 2026-10-01] - one row per
      real due date, carrying RiskType straight from #inst for grouping.
    ===================================================================*/
    IF OBJECT_ID('tempdb..#occ') IS NOT NULL DROP TABLE #occ;
    SELECT
        cso.ID AS ScheduleOnID,
        cso.ComplianceInstanceID,
        i.RiskType,
        cso.ScheduleOn
    INTO #occ
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd
      AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1;
    CREATE CLUSTERED INDEX IX_occ ON #occ (RiskType, ScheduleOnID);
    CREATE NONCLUSTERED INDEX IX_occ_inst ON #occ (ComplianceInstanceID);

    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #occ o WHERE o.ComplianceInstanceID = i.ComplianceInstanceID);

    /*===================================================================
      OVERDUE - OCCURRENCE-LEVEL [CHANGED 2026-10-01]
    ===================================================================*/
    IF OBJECT_ID('tempdb..#ovdocc') IS NOT NULL DROP TABLE #ovdocc;
    SELECT o.ComplianceScheduleOnID
    INTO #ovdocc
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #occ x ON x.ScheduleOnID = o.ComplianceScheduleOnID
    WHERE o.ScheduleOn >= @WindowStart AND o.ScheduleOn < @WindowEnd;
    CREATE CLUSTERED INDEX IX_ovdocc ON #ovdocc (ComplianceScheduleOnID);

    IF OBJECT_ID('tempdb..#ownership') IS NOT NULL DROP TABLE #ownership;
    SELECT o.ComplianceInstanceID, o.HasInstanceOwner, o.HasScheduleOwner,
           o.HasNoSchedules, o.NoInstanceOwner, o.NoOwnerAnywhere, o.OwnerClass
    INTO #ownership
    FROM dbo.tvfInsightsOwnership(@UserID, @CustomerID) o;
    CREATE CLUSTERED INDEX IX_ownership ON #ownership (ComplianceInstanceID);

    IF OBJECT_ID('tempdb..#owned') IS NOT NULL DROP TABLE #owned;
    SELECT ComplianceInstanceID INTO #owned
    FROM #ownership WHERE HasInstanceOwner = 1;
    CREATE CLUSTERED INDEX IX_owned ON #owned (ComplianceInstanceID);

    /*===================================================================
      PER-LEVEL ROWS - Instances/Overdue now over #occ; NoInstanceOwner/
      Imprisonment stay instance-level via #inst (flagged first, then
      summed - avoids the EXISTS-inside-CASE-inside-aggregate trap).
    ===================================================================*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        RiskType              INT            NOT NULL PRIMARY KEY,
        RiskLabel             NVARCHAR(200)  NULL,
        Instances             INT            NOT NULL,
        Overdue               INT            NOT NULL,
        OverduePct            DECIMAL(5,1)   NULL,
        NoInstanceOwner             INT            NOT NULL,
        ImprisonmentInstances INT            NOT NULL,
        ImprisonmentOverdue   INT            NOT NULL,
        BranchesCovered       INT            NOT NULL,
        VsTenantPP            DECIMAL(9,2)   NULL,
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (RiskType, RiskLabel, Instances, Overdue, NoInstanceOwner,
                  ImprisonmentInstances, ImprisonmentOverdue, BranchesCovered)
    SELECT
        r.RiskType,
        r.RiskLabel,
        COUNT(x.ScheduleOnID),
        SUM(CASE WHEN v.ComplianceScheduleOnID IS NOT NULL THEN 1 ELSE 0 END),
        ISNULL(MAX(ii.NoInstanceOwner), 0),
        ISNULL(MAX(ii.ImprisonmentInstances), 0),
        ISNULL(MAX(ii.ImprisonmentOverdue), 0),
        (SELECT COUNT(DISTINCT i4.BranchID) FROM #inst i4 WHERE i4.RiskType = r.RiskType)
    FROM #risk r
    LEFT JOIN #occ    x ON x.RiskType = r.RiskType
    LEFT JOIN #ovdocc v ON v.ComplianceScheduleOnID = x.ScheduleOnID
    LEFT JOIN (
        SELECT i3.RiskType,
               SUM(CASE WHEN w3.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END) AS NoInstanceOwner,
               SUM(CASE WHEN i3.Imprisonment = 1 THEN 1 ELSE 0 END) AS ImprisonmentInstances,
               SUM(CASE WHEN i3.Imprisonment = 1 AND ovd3.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END) AS ImprisonmentOverdue
        FROM #inst i3
        LEFT JOIN #owned w3 ON w3.ComplianceInstanceID = i3.ComplianceInstanceID
        LEFT JOIN (SELECT DISTINCT x4.ComplianceInstanceID
                   FROM #occ x4 JOIN #ovdocc v4 ON v4.ComplianceScheduleOnID = x4.ScheduleOnID) ovd3
               ON ovd3.ComplianceInstanceID = i3.ComplianceInstanceID
        GROUP BY i3.RiskType
    ) ii ON ii.RiskType = r.RiskType
    GROUP BY r.RiskType, r.RiskLabel;

    DECLARE @rowSum      INT = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #occ);
    -- [FIX 2026-10-01] ScopedInstances = distinct instances, not occurrences. @scopedTotal
    -- (from #occ) stays the occurrence-level denominator (RegTrack parity, unchanged).
    DECLARE @scopedInstancesDistinct INT = (SELECT COUNT(*) FROM #inst);

    IF @rowSum <> @scopedTotal
        THROW 51061, N'RISK DIMENSION RECONCILIATION FAILED - per-level sums do not tie to the scoped occurrence total. An instance carries a RiskType absent from InsightsEnumPolarity. Refusing to publish.', 1;

    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT COUNT(*) FROM #ovdocc) / @scopedTotal END;

    DECLARE @hasAnyObligations BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;

    UPDATE #rows SET
        OverduePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END;

    UPDATE #rows SET
        VsTenantPP = CASE WHEN Instances = 0 THEN NULL ELSE OverduePct - @tenantOverduePct END;

    DECLARE @impTotal INT = (SELECT COUNT(*) FROM #inst WHERE Imprisonment = 1);
    DECLARE @impOnCritical INT = (SELECT COUNT(*) FROM #inst WHERE Imprisonment = 1 AND RiskType = @criticalRisk);
    DECLARE @impOverlapPct DECIMAL(5,1) =
        CASE WHEN @impTotal = 0 THEN NULL ELSE 100.0 * @impOnCritical / @impTotal END;

    SELECT
        'control_totals'                  AS ResultSet,
        @scopedInstancesDistinct          AS ScopedInstances,
        @rowSum                           AS SumOfRows,
        CAST(1 AS BIT)                    AS Reconciled,
        (SELECT COUNT(*) FROM #ovdocc)    AS OverdueInstances,
        @tenantOverduePct                 AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows)      AS RiskLevelsReported,
        (SELECT COUNT(*) FROM #rows WHERE Instances > 0) AS RiskLevelsWithObligations,
        @criticalRisk                     AS CriticalRiskType,
        @impTotal                         AS ImprisonmentInstances,
        @impOverlapPct                    AS ImprisonmentOnCriticalPct;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    DECLARE @criticalNoInstanceOwner INT =
        (SELECT ISNULL(MAX(NoInstanceOwner),0) FROM #rows WHERE RiskType = @criticalRisk);

    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyObligations = 1
                  AND Instances > 0
                  AND RiskType <> @criticalRisk
                  AND NoInstanceOwner > @criticalNoInstanceOwner
                 THEN ',ownership_gap_below_critical' ELSE '' END
        , 1, 1, '');

    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector      VARCHAR(40) PRIMARY KEY,
        Eligible      INT,
        Flagged       INT,
        FlaggedPct    DECIMAL(5,1),
        EmitMode      VARCHAR(12)
    );

    DECLARE @withObl INT = (SELECT COUNT(*) FROM #rows WHERE Instances > 0);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'ownership_gap_below_critical', @withObl,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%ownership_gap_below_critical%');

    UPDATE #detector
       SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;

    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51040, N'DETECTOR CONTRACT VIOLATED - a detector flagged more rows than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId  VARCHAR(20),
        Metric       VARCHAR(60),
        ScopeLabel   NVARCHAR(500),
        Value        DECIMAL(18,2),
        Rank_        INT NULL,
        OfN          INT NULL,
        ComparatorValue DECIMAL(18,2) NULL,
        VsComparatorPP  DECIMAL(9,2) NULL,
        Direction    VARCHAR(10) NULL,
        Caveat       NVARCHAR(500) NULL
    );

    INSERT #assert VALUES ('A-TENANT','overdue_pct',N'tenant',@tenantOverduePct,NULL,NULL,NULL,NULL,NULL,NULL);

    IF @hasAnyObligations = 1
    INSERT #assert
    SELECT 'A-CRIT','overdue_pct',RiskLabel,OverduePct,NULL,NULL,
           @tenantOverduePct, VsTenantPP,
           CASE WHEN OverduePct > @tenantOverduePct THEN 'worse' ELSE 'better' END,
           NULL
    FROM #rows WHERE RiskType = @criticalRisk AND Instances > 0;

    IF EXISTS (SELECT 1 FROM #rows WHERE ImprisonmentOverdue > 0)
    INSERT #assert
    SELECT TOP 1 'A-WORST-RISK-EXP','imprisonment_overdue_count', RiskLabel, ImprisonmentOverdue, NULL,
           (SELECT SUM(ImprisonmentOverdue) FROM #rows), NULL, NULL, 'worse',
           N'ranked by CONSEQUENCE - overdue obligations carrying personal liability - not by rate.'
    FROM #rows WHERE ImprisonmentOverdue > 0 ORDER BY ImprisonmentOverdue DESC, Overdue DESC;

    IF @impTotal > 0
    INSERT #assert
    VALUES ('A-IMP-OVERLAP','imprisonment_on_critical_pct',N'tenant',@impOverlapPct,
            NULL,@impTotal,NULL,NULL,NULL,
            N'critical_and_imprisonment_overlap: these are largely the SAME obligations, not two independent exposures');

    IF (SELECT EmitMode FROM #detector WHERE Detector='ownership_gap_below_critical') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-OWNGAP-' + CAST(ROW_NUMBER() OVER (ORDER BY NoInstanceOwner DESC) AS VARCHAR(5)),
               'ownerless', RiskLabel, NoInstanceOwner, NULL, NULL,
               @criticalNoInstanceOwner, NoInstanceOwner - @criticalNoInstanceOwner, 'worse',
               N'ownership_gap_below_critical: attention follows severity, ownership does not'
        FROM #rows WHERE Flags LIKE '%ownership_gap_below_critical%' ORDER BY NoInstanceOwner DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='ownership_gap_below_critical') = 'aggregate'
        INSERT #assert
        SELECT 'A-OWNGAP-AGG','risk_levels_with_ownership_gap',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - unassigned ownership sits below the Critical tier across several levels'
        FROM #detector WHERE Detector='ownership_gap_below_critical';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (
        FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400),
        NarrativeGuard NVARCHAR(500) NULL
    );

    INSERT #find
    SELECT 'F-CRIT-BETTER','info',
           CONCAT(N'', ScopeLabel, N' obligations run at ', Value, N'% overdue, ',
                  ABS(VsComparatorPP), N' points BELOW the tenant average'),
           'A-CRIT,A-TENANT',
           N'MUST NOT be presented as a failure. This tier is better managed than the tenant average - the organisation triages correctly. Do not narrate Critical volume as a problem.'
    FROM #assert WHERE AssertionId = 'A-CRIT' AND Direction = 'better';

    INSERT #find
    SELECT 'F-CRIT-WORSE','high',
           CONCAT(N'', ScopeLabel, N' obligations run at ', Value, N'% overdue, ',
                  VsComparatorPP, N' points above the tenant average'),
           'A-CRIT,A-TENANT',
           N'Do not also raise imprisonment exposure as a separate finding - see A-IMP-OVERLAP, they are largely the same obligations.'
    FROM #assert WHERE AssertionId = 'A-CRIT' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-OWNGAP','high',
           CONCAT(N'', ScopeLabel, N' carries ', CAST(Value AS INT),
                  N' obligations with no INSTANCE-LEVEL owner, more than the Critical tier'),
           AssertionId,
           N'Attention follows severity; ownership does not. The coverage gap is in the middle tiers, not the top one.'
    FROM #assert WHERE AssertionId LIKE 'A-OWNGAP-[0-9]%';

    INSERT #find
    SELECT 'F-OWNGAP-AGG','high',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' risk levels (', VsComparatorPP,
                  N'%) carry more unassigned obligations than the Critical tier'),
           AssertionId,
           N'Attention follows severity; ownership does not.'
    FROM #assert WHERE AssertionId = 'A-OWNGAP-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'ownership_has_two_mechanisms'   THEN 'NoInstanceOwner'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'critical_imprisonment_overlap'        THEN 'ImprisonmentOnCriticalPct'
                   WHEN 'risk_levels_unused'                   THEN 'RiskLevelsWithObligations'
                   WHEN 'occurrence_level_counting'            THEN 'Instances'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to obligations with a scheduled occurrence between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. An obligation with no occurrence in this window is excluded entirely, not just its '
                    + N'overdue figures - it will not appear in any row here even if it exists cumulatively.') AS Detail
        UNION ALL
        SELECT 'occurrence_level_counting' AS Issue,
               N'[ADDED 2026-10-01] Instances/Overdue/OverduePct now count every real scheduled '
             + N'occurrence in the window, not distinct compliance obligations. NoInstanceOwner/'
             + N'ImprisonmentInstances/ImprisonmentOverdue remain INSTANCE-level.' AS Detail
        UNION ALL
        SELECT 'ownership_has_two_mechanisms' AS Issue,
               N'RegTrack assigns a performer by TWO mechanisms: ComplianceAssignment (on the '
             + N'obligation) and ComplianceScheduleOn.Performerid (on each occurrence, 99.8% '
             + N'populated). This metric counts only the FIRST. Most obligations it counts DO '
             + N'have someone named per occurrence - what is missing is accountability for the '
             + N'obligation itself. NEVER present it as "nobody is doing this". NoOwnerAnywhere '
             + N'is the stricter measure.' AS Detail
        UNION ALL
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'critical_imprisonment_overlap',
               CONCAT(N'', @impOverlapPct, N'% of imprisonment-bearing obligations sit on the ',
                      N'Critical tier. Critical risk and personal liability are largely the SAME ',
                      N'population, not independent axes. Do not present them as two findings.')
        WHERE @impTotal > 0
        UNION ALL
        SELECT 'risk_levels_unused',
               CONCAT(N'', COUNT(*), N' mapped risk level(s) carry no obligations in this scope ',
                      N'and are reported with zero counts rather than omitted.')
        FROM #rows WHERE Instances = 0 HAVING COUNT(*) > 0
    ) q;

    DROP TABLE #risk; DROP TABLE #inst; DROP TABLE #occ; DROP TABLE #ovdocc; DROP TABLE #owned; DROP TABLE #ownership;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Risk restored to the 2026-10-27 production definition.';
GO

CREATE OR ALTER PROCEDURE dbo.usp_Insights_Dimension_Nature
    @UserID      INT,
    @CustomerID  INT,
    @WindowStart DATETIME,
    @WindowEnd   DATETIME,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51070, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    DECLARE @criticalRisk INT = (
        SELECT TRY_CAST(p.RawValue AS INT)
        FROM dbo.InsightsEnumPolarity p
        JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
        WHERE p.Semantic = 'RiskType' AND p.Meaning LIKE N'Critical%');

    IF @criticalRisk IS NULL
        THROW 51075, N'DICTIONARY GAP - no RiskType value is mapped to Critical in InsightsEnumPolarity. Refusing to compute.', 1;

    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.RiskType,
        s.Imprisonment,
        s.NatureOfCompliance AS NatureId,
        CASE WHEN ISNULL(c.FixedMinimum,0)              > 0
                OR ISNULL(c.FixedMaximum,0)             > 0
                OR ISNULL(c.VariableAmountPerDay,0)     > 0
                OR ISNULL(c.VariableAmountPerMonth,0)   > 0
                OR ISNULL(c.VariableAmountPerInstance,0)> 0
             THEN 1 ELSE 0 END AS HasFinancialPenalty,
        CASE WHEN c.IsForcefulClosure = 1 THEN 1 ELSE 0 END AS HasClosureRisk
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
    JOIN Compliance c ON c.ID = s.ComplianceID;

    CREATE CLUSTERED INDEX IX_inst ON #inst (NatureId, ComplianceInstanceID);

    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51073, N'NATURE DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51073, N'NATURE DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    IF OBJECT_ID('tempdb..#occ') IS NOT NULL DROP TABLE #occ;
    SELECT
        cso.ID AS ScheduleOnID,
        cso.ComplianceInstanceID,
        i.NatureId,
        cso.ScheduleOn
    INTO #occ
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd
      AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1;
    CREATE CLUSTERED INDEX IX_occ ON #occ (NatureId, ScheduleOnID);
    CREATE NONCLUSTERED INDEX IX_occ_inst ON #occ (ComplianceInstanceID);

    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #occ o WHERE o.ComplianceInstanceID = i.ComplianceInstanceID);

    IF OBJECT_ID('tempdb..#ovdocc') IS NOT NULL DROP TABLE #ovdocc;
    SELECT o.ComplianceScheduleOnID
    INTO #ovdocc
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #occ x ON x.ScheduleOnID = o.ComplianceScheduleOnID
    WHERE o.ScheduleOn >= @WindowStart AND o.ScheduleOn < @WindowEnd;
    CREATE CLUSTERED INDEX IX_ovdocc ON #ovdocc (ComplianceScheduleOnID);

    IF OBJECT_ID('tempdb..#ownership') IS NOT NULL DROP TABLE #ownership;
    SELECT o.ComplianceInstanceID, o.HasInstanceOwner, o.HasScheduleOwner,
           o.HasNoSchedules, o.NoInstanceOwner, o.NoOwnerAnywhere, o.OwnerClass
    INTO #ownership
    FROM dbo.tvfInsightsOwnership(@UserID, @CustomerID) o;
    CREATE CLUSTERED INDEX IX_ownership ON #ownership (ComplianceInstanceID);

    IF OBJECT_ID('tempdb..#owned') IS NOT NULL DROP TABLE #owned;
    SELECT ComplianceInstanceID INTO #owned
    FROM #ownership WHERE HasInstanceOwner = 1;
    CREATE CLUSTERED INDEX IX_owned ON #owned (ComplianceInstanceID);

    IF OBJECT_ID('tempdb..#nature') IS NOT NULL DROP TABLE #nature;
    SELECT n.ID AS NatureId, n.Name AS NatureName,
           CAST(CASE WHEN n.IsDeleted = 1 THEN 1 ELSE 0 END AS BIT) AS IsRetired
    INTO #nature
    FROM NatureOfCompliance n
    WHERE n.IsDeleted = 0
       OR EXISTS (SELECT 1 FROM #inst i WHERE i.NatureId = n.ID);

    IF NOT EXISTS (SELECT 1 FROM #nature)
        THROW 51076, N'MASTER DATA GAP - NatureOfCompliance master is empty. Refusing to compute a nature dimension.', 1;

    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        NatureId                 INT            NOT NULL PRIMARY KEY,
        NatureName               NVARCHAR(300)  NULL,
        IsRetired                BIT            NOT NULL,
        Instances                INT            NOT NULL,
        Overdue                  INT            NOT NULL,
        OverduePct               DECIMAL(5,1)   NULL,
        NoInstanceOwner                INT            NOT NULL,
        ImprisonmentInstances    INT            NOT NULL,
        ImprisonmentOverdue      INT            NOT NULL,
        CriticalInstances        INT            NOT NULL,
        BranchesCovered          INT            NOT NULL,
        PenaltyBearingInstances  INT            NOT NULL,
        FinancialPenaltyInstances INT           NOT NULL,
        ClosureRiskInstances     INT            NOT NULL,
        ImprisonmentSharePct     DECIMAL(5,1)   NULL,
        OverdueRank              INT            NULL,
        Flags                    VARCHAR(200)   NULL
    );

    INSERT #rows (NatureId, NatureName, IsRetired, Instances, Overdue, NoInstanceOwner,
                  ImprisonmentInstances, ImprisonmentOverdue, CriticalInstances,
                  BranchesCovered, PenaltyBearingInstances,
                  FinancialPenaltyInstances, ClosureRiskInstances)
    SELECT
        n.NatureId, n.NatureName, n.IsRetired,
        COUNT(x.ScheduleOnID),
        SUM(CASE WHEN v.ComplianceScheduleOnID IS NOT NULL THEN 1 ELSE 0 END),
        ISNULL(MAX(ii.NoInstanceOwner), 0),
        ISNULL(MAX(ii.ImprisonmentInstances), 0),
        ISNULL(MAX(ii.ImprisonmentOverdue), 0),
        ISNULL(MAX(ii.CriticalInstances), 0),
        (SELECT COUNT(DISTINCT i4.BranchID) FROM #inst i4 WHERE i4.NatureId = n.NatureId),
        ISNULL(MAX(ii.PenaltyBearingInstances), 0),
        ISNULL(MAX(ii.FinancialPenaltyInstances), 0),
        ISNULL(MAX(ii.ClosureRiskInstances), 0)
    FROM #nature n
    LEFT JOIN #occ    x ON x.NatureId = n.NatureId
    LEFT JOIN #ovdocc v ON v.ComplianceScheduleOnID = x.ScheduleOnID
    LEFT JOIN (
        SELECT i3.NatureId,
               SUM(CASE WHEN w3.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END) AS NoInstanceOwner,
               SUM(CASE WHEN i3.Imprisonment = 1 THEN 1 ELSE 0 END) AS ImprisonmentInstances,
               SUM(CASE WHEN i3.Imprisonment = 1 AND ovd3.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END) AS ImprisonmentOverdue,
               SUM(CASE WHEN i3.RiskType = @criticalRisk THEN 1 ELSE 0 END) AS CriticalInstances,
               SUM(CASE WHEN i3.HasFinancialPenalty = 1 OR i3.Imprisonment = 1 OR i3.HasClosureRisk = 1 THEN 1 ELSE 0 END) AS PenaltyBearingInstances,
               SUM(CASE WHEN i3.HasFinancialPenalty = 1 THEN 1 ELSE 0 END) AS FinancialPenaltyInstances,
               SUM(CASE WHEN i3.HasClosureRisk = 1 THEN 1 ELSE 0 END) AS ClosureRiskInstances
        FROM #inst i3
        LEFT JOIN #owned w3 ON w3.ComplianceInstanceID = i3.ComplianceInstanceID
        LEFT JOIN (SELECT DISTINCT x4.ComplianceInstanceID
                   FROM #occ x4 JOIN #ovdocc v4 ON v4.ComplianceScheduleOnID = x4.ScheduleOnID) ovd3
               ON ovd3.ComplianceInstanceID = i3.ComplianceInstanceID
        GROUP BY i3.NatureId
    ) ii ON ii.NatureId = n.NatureId
    GROUP BY n.NatureId, n.NatureName, n.IsRetired;

    DECLARE @rowSum      INT = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #occ);
    -- [FIX 2026-10-01] ScopedInstances = distinct instances, not occurrences. @scopedTotal
    -- (from #occ) stays the occurrence-level denominator (RegTrack parity, unchanged).
    DECLARE @scopedInstancesDistinct INT = (SELECT COUNT(*) FROM #inst);
    DECLARE @untagged    INT = (SELECT COUNT(*) FROM #occ o JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID WHERE i.NatureId IS NULL);
    DECLARE @orphan      INT = (SELECT COUNT(*) FROM #inst i
                                WHERE i.NatureId IS NOT NULL
                                  AND NOT EXISTS (SELECT 1 FROM #nature n WHERE n.NatureId = i.NatureId));

    IF @orphan > 0
        THROW 51071, N'NATURE DIMENSION RECONCILIATION FAILED - an instance carries a NatureOfCompliance absent from the master. This is a referential break, not a tagging gap. Refusing to publish.', 1;

    IF @rowSum + @untagged <> @scopedTotal
        THROW 51072, N'NATURE DIMENSION RECONCILIATION FAILED - per-nature sums plus the untagged bucket do not tie to the scoped occurrence total. Refusing to publish.', 1;

    DECLARE @hasAnyObligations BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * (SELECT COUNT(*) FROM #ovdocc) / @scopedTotal END;
    DECLARE @tenantImpSharePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT COUNT(*) FROM #occ x JOIN #inst i ON i.ComplianceInstanceID = x.ComplianceInstanceID WHERE i.Imprisonment = 1) / @scopedTotal END;

    DECLARE @othersInstances INT =
        (SELECT ISNULL(SUM(Instances),0) FROM #rows WHERE NatureName LIKE N'Other%');
    DECLARE @uncat    INT = @othersInstances + @untagged;
    DECLARE @uncatPct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * @uncat / @scopedTotal END;

    UPDATE #rows SET
        OverduePct           = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END,
        ImprisonmentSharePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * ImprisonmentInstances / Instances END;

    DECLARE @materialityFloor INT = 50;
    DECLARE @materialMembers  INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @materialityFloor);
    DECLARE @rankDegraded     BIT = CASE WHEN @materialMembers < 2 THEN 1 ELSE 0 END;
    DECLARE @rankFloor        INT = CASE WHEN @rankDegraded = 1 THEN 1 ELSE @materialityFloor END;

    ;WITH r AS (SELECT NatureId, RANK() OVER (ORDER BY OverduePct DESC) AS rk
                FROM #rows WHERE Instances >= @rankFloor)
    UPDATE #rows SET OverdueRank = r.rk FROM #rows JOIN r ON r.NatureId = #rows.NatureId;

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
        @scopedInstancesDistinct     AS ScopedInstances,
        @rowSum                      AS CategorisedInstances,
        CAST(1 AS BIT)               AS Reconciled,
        (SELECT COUNT(*) FROM #ovdocc) AS OverdueInstances,
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

    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51040, N'DETECTOR CONTRACT VIOLATED - a detector flagged more rows than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    INSERT #assert VALUES ('A-TENANT','overdue_pct',N'tenant',@tenantOverduePct,NULL,NULL,NULL,NULL,NULL,NULL);

    DECLARE @rankable  INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor);
    DECLARE @tiedAtTop INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor AND OverdueRank = 1);

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

    IF EXISTS (SELECT 1 FROM #rows WHERE ImprisonmentOverdue > 0)
    INSERT #assert
    SELECT TOP 1 'A-WORST-NATURE-EXP','imprisonment_overdue_count', NatureName, ImprisonmentOverdue, NULL,
           (SELECT SUM(ImprisonmentOverdue) FROM #rows), NULL, NULL, 'worse',
           N'ranked by CONSEQUENCE - overdue obligations carrying personal liability - not by rate.'
    FROM #rows WHERE ImprisonmentOverdue > 0 AND NatureName NOT LIKE N'Other%'
    ORDER BY ImprisonmentOverdue DESC, Overdue DESC;

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

    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'ownership_has_two_mechanisms'   THEN 'NoInstanceOwner'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'nature_uncategorised_gap'             THEN 'UncategorisedPct'
                   WHEN 'nature_untagged'                      THEN 'UntaggedInstances'
                   WHEN 'nature_retired_still_in_use'          THEN 'RetiredNaturesStillInUse'
                   WHEN 'natures_unused'                       THEN 'NaturesWithObligations'
                   WHEN 'occurrence_level_counting'            THEN 'Instances'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to obligations with a scheduled occurrence between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. An obligation with no occurrence in this window is excluded entirely, not just its '
                    + N'overdue figures - it will not appear in any row here even if it exists cumulatively.') AS Detail
        UNION ALL
        SELECT 'occurrence_level_counting' AS Issue,
               N'[ADDED 2026-10-01] Instances/Overdue/OverduePct now count every real scheduled '
             + N'occurrence in the window, not distinct compliance obligations. NoInstanceOwner/'
             + N'Imprisonment*/CriticalInstances/penalty fields remain INSTANCE-level.' AS Detail
        UNION ALL
        SELECT 'ownership_has_two_mechanisms' AS Issue,
               N'RegTrack assigns a performer by TWO mechanisms: ComplianceAssignment (on the '
             + N'obligation) and ComplianceScheduleOn.Performerid (on each occurrence, 99.8% '
             + N'populated). This metric counts only the FIRST. Most obligations it counts DO '
             + N'have someone named per occurrence - what is missing is accountability for the '
             + N'obligation itself. NEVER present it as "nobody is doing this". NoOwnerAnywhere '
             + N'is the stricter measure.' AS Detail
        UNION ALL
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

    DROP TABLE #inst; DROP TABLE #occ; DROP TABLE #ovdocc; DROP TABLE #owned; DROP TABLE #ownership; DROP TABLE #nature;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Nature restored to the 2026-10-27 production definition.';
GO

CREATE OR ALTER PROCEDURE dbo.usp_Insights_Dimension_Internal
    @UserID      INT,
    @CustomerID  INT,
    @WindowStart DATETIME,
    @WindowEnd   DATETIME,
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

    CREATE CLUSTERED INDEX IX_stat ON #stat (ComplianceInstanceID);

    /*  [ADDED 2026-09-25] HARD WINDOW GATE, STATUTORY SIDE - caller-supplied period,
        no FY default. #stat carries no per-row date - real due dates live on
        ComplianceScheduleOn, one row per recurring occurrence. Same proven pattern
        as sql/23 TimelinessFY and sql/11 Act: #stat is materialised and indexed
        above, so find which of those instances had >=1 scheduled occurrence in the
        window, then narrow #stat itself down to just those. Every downstream step
        (statutory overdue, ownership, per-branch rollup, reconciliation) already
        reads from #stat, so it inherits the window for free.
        Do NOT reintroduce a join hint or skip the materialise-first step - the same
        shape of query against ComplianceScheduleOn's 29.4M rows without it measured
        183,502 ms on a real tenant versus 157 ms scoped-first (sql/23's own numbers).
        @WindowStart/@WindowEnd are validated once here and reused for the internal
        side's own gate below - they govern the whole dimension, not just #stat.      */
    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51112, N'INTERNAL DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51112, N'INTERNAL DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    IF OBJECT_ID('tempdb..#statActive') IS NOT NULL DROP TABLE #statActive;
    SELECT DISTINCT cso.ComplianceInstanceID
    INTO #statActive
    FROM #stat s
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = s.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd
      AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1;   -- [v2] switched-off due dates do not count (RegTrack parity)
    CREATE CLUSTERED INDEX IX_statActive ON #statActive (ComplianceInstanceID);

    DELETE s FROM #stat s
    WHERE NOT EXISTS (SELECT 1 FROM #statActive a WHERE a.ComplianceInstanceID = s.ComplianceInstanceID);

    IF OBJECT_ID('tempdb..#statOvd') IS NOT NULL DROP TABLE #statOvd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #statOvd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #stat s ON s.ComplianceInstanceID = o.ComplianceInstanceID
    -- [FIX 2026-09-30, found live REQ-1085/Detailed Report reconciliation, tenant 1285] o was
    -- matched by ComplianceInstanceID alone - an instance in #stat (window-active) whose window
    -- occurrence is NOT overdue still got flagged overdue here if it had ANY other, out-of-window
    -- occurrence that was overdue. tvfInsightsOverdueSchedules carries its own ScheduleOn per
    -- occurrence - constrain it to THIS window too, same bound #statActive already uses.
    WHERE o.ScheduleOn >= @WindowStart AND o.ScheduleOn < @WindowEnd;

    IF OBJECT_ID('tempdb..#statOwn') IS NOT NULL DROP TABLE #statOwn;
    /*  [CORRECTED 2026-09-05] Statutory ownership has TWO mechanisms - see sql/01.
        #statOwn keeps its original meaning (instance-level) so the rest of this
        proc is unchanged; #statOwnership carries the full picture.

        NOTE: InternalComplianceScheduleOn has NO performer column, so internal
        compliance has only ONE mechanism and #intOwn below is already correct.
        The two populations are therefore NOT measured the same way - that is a
        schema fact, and it is declared in data_quality below.                  */
    IF OBJECT_ID('tempdb..#statOwnership') IS NOT NULL DROP TABLE #statOwnership;
    SELECT o.ComplianceInstanceID, o.NoInstanceOwner, o.NoOwnerAnywhere
    INTO #statOwnership
    FROM dbo.tvfInsightsOwnership(@UserID, @CustomerID) o;
    CREATE CLUSTERED INDEX IX_statOwnership ON #statOwnership (ComplianceInstanceID);

    SELECT ComplianceInstanceID INTO #statOwn
    FROM #statOwnership WHERE NoInstanceOwner = 0;

    /*  Internal - branch-axis scoping only, see the header. */
    IF OBJECT_ID('tempdb..#int') IS NOT NULL DROP TABLE #int;
    SELECT ii.ID AS InternalInstanceID, ii.CustomerBranchID AS BranchID
    INTO #int
    FROM InternalComplianceInstance ii
    JOIN CustomerBranch cb ON cb.ID = ii.CustomerBranchID
    JOIN #branch b ON b.BranchID = ii.CustomerBranchID
    WHERE ii.IsDeleted = 0 AND cb.IsDeleted = 0 AND cb.Status = 1 AND cb.CustomerID = @CustomerID
      -- [v2 2026-09-29] RegTrack parity: an internal obligation needs an active performer
      AND EXISTS (SELECT 1 FROM InternalComplianceAssignment ica JOIN [User] u ON u.ID = ica.UserID
                  WHERE ica.InternalComplianceInstanceID = ii.ID AND ica.RoleID = 3
                    AND u.CustomerID = @CustomerID AND u.IsDeleted = 0 AND u.IsActive = 1);

    CREATE CLUSTERED INDEX IX_int ON #int (InternalInstanceID);

    /*  [ADDED 2026-09-25] HARD WINDOW GATE, INTERNAL SIDE - caller-supplied period,
        no FY default. #int carries no per-row date either - real due dates live on
        InternalComplianceScheduledOn (a SEPARATE schema family from the statutory
        side's ComplianceScheduleOn, see the header note), one row per recurring
        occurrence. #int is materialised and indexed above, so find which of those
        instances had >=1 scheduled occurrence in the window, then narrow #int
        itself down to just those. Every downstream step (internal overdue,
        ownership, per-branch rollup, reconciliation) already reads from #int, so
        it inherits the window for free. @WindowStart/@WindowEnd were already
        validated NOT NULL / not inverted on the statutory side above - not
        re-checked here, same params govern both populations.                      */
    IF OBJECT_ID('tempdb..#intActive') IS NOT NULL DROP TABLE #intActive;
    SELECT DISTINCT iso.InternalComplianceInstanceID AS InternalInstanceID
    INTO #intActive
    FROM #int i
    JOIN InternalComplianceScheduledOn iso ON iso.InternalComplianceInstanceID = i.InternalInstanceID
    WHERE iso.ScheduledOn >= @WindowStart AND iso.ScheduledOn < @WindowEnd;
    CREATE CLUSTERED INDEX IX_intActive ON #intActive (InternalInstanceID);

    DELETE i FROM #int i
    WHERE NOT EXISTS (SELECT 1 FROM #intActive a WHERE a.InternalInstanceID = i.InternalInstanceID);

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
      AND iso.ScheduledOn < CAST(@AsOf AS DATE)   -- [v2] due before today
      AND d.OverdueEligible = 1   -- [v2] Open only (dictionary v2)
      -- [FIX 2026-09-30, same class as #statOvd above] constrain the overdue occurrence itself
      -- to this window, not just instance membership in #int - an out-of-window overdue
      -- occurrence must not flag an instance overdue for a DIFFERENT window's report.
      AND iso.ScheduledOn >= @WindowStart AND iso.ScheduledOn < @WindowEnd;

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
        BranchName          NVARCHAR(600)  NULL,
        ApexName            NVARCHAR(600)  NULL,
        StatutoryInstances  INT            NOT NULL,
        StatutoryOverdue    INT            NOT NULL,
        StatutoryNoInstanceOwner  INT            NOT NULL,
        InternalInstances   INT            NOT NULL,
        InternalOverdue     INT            NOT NULL,
        InternalNoInstanceOwner   INT            NOT NULL,
        -- derived
        StatutoryNoInstanceOwnerPct DECIMAL(5,1) NULL,
        InternalNoInstanceOwnerPct  DECIMAL(5,1) NULL,
        Flags               VARCHAR(200)   NULL
    );

    INSERT #rows (BranchID, BranchName, ApexName, StatutoryInstances, StatutoryOverdue,
                  StatutoryNoInstanceOwner, InternalInstances, InternalOverdue, InternalNoInstanceOwner)
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
        StatutoryNoInstanceOwnerPct = CASE WHEN StatutoryInstances = 0 THEN NULL
                                     ELSE 100.0 * StatutoryNoInstanceOwner / StatutoryInstances END,
        InternalNoInstanceOwnerPct  = CASE WHEN InternalInstances  = 0 THEN NULL
                                     ELSE 100.0 * InternalNoInstanceOwner  / InternalInstances  END;

    /*-- 4. DETECTIONS ---------------------------------------------------*/
    DECLARE @statOwnPct DECIMAL(5,1) =
        CASE WHEN @statTotal = 0 THEN NULL
             ELSE 100.0 * (SELECT ISNULL(SUM(StatutoryNoInstanceOwner),0) FROM #rows) / @statTotal END;
    DECLARE @intOwnPct DECIMAL(5,1) =
        CASE WHEN @intTotal = 0 THEN NULL
             ELSE 100.0 * (SELECT ISNULL(SUM(InternalNoInstanceOwner),0) FROM #rows) / @intTotal END;

    UPDATE #rows SET Flags =
        STUFF(
            /*  The structural finding: a branch running statutory compliance with
                no internal governance configured at all. */
            CASE WHEN @hasAnyObligations = 1 AND StatutoryInstances > 0 AND InternalInstances = 0
                 THEN ',internal_coverage_gap' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND InternalInstances > 0
                  AND @statOwnPct IS NOT NULL AND InternalNoInstanceOwnerPct > @statOwnPct
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
        @statOwnPct                     AS StatutoryNoInstanceOwnerPct,
        @intOwnPct                      AS InternalNoInstanceOwnerPct,
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

    /*  [ADDED 2026-09-10] DETECTOR CONTRACT - fail at source.
        Flagged and Eligible MUST come from the same population. sql/05 once
        emitted 120 flagged of 99 eligible (121.2%) because a flag had no
        Instances > 0 guard; only the .NET layer caught it, three layers
        downstream. A percentage above 100 reaching a narrative writer is
        indefensible - the writer cannot tell it is impossible, and rendering
        it faithfully produces a false statement.
        Shared code 51040 across all dimensions: same failure class, and the
        message names the offending detector.                                 */
    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51040, N'DETECTOR CONTRACT VIOLATED - a detector flagged more rows than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 6. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    IF @statOwnPct IS NOT NULL
    INSERT #assert VALUES ('A-STAT-OWN','no_instance_owner_pct',N'statutory',@statOwnPct,NULL,@statTotal,NULL,NULL,NULL,NULL);

    IF @intOwnPct IS NOT NULL
    INSERT #assert VALUES ('A-INT-OWN','no_instance_owner_pct',N'internal',@intOwnPct,NULL,@intTotal,
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
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'ownership_has_two_mechanisms'   THEN 'StatutoryNoInstanceOwnerPct'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'internal_scope_is_branch_only'        THEN 'InternalInstances'
                   WHEN 'internal_unmapped_status'             THEN 'InternalUnmappedStatusRows'
                   WHEN 'internal_absent'                      THEN 'InternalAbsentEntirely'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to obligations with a scheduled occurrence between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. Applies to BOTH populations - statutory instances are matched against '
                    + N'ComplianceScheduleOn, internal instances against InternalComplianceScheduledOn. An '
                    + N'instance with no occurrence in this window is excluded entirely, not just its overdue '
                    + N'figures - it will not appear in any row here even if it exists cumulatively.') AS Detail
        UNION ALL
        SELECT 'ownership_has_two_mechanisms' AS Issue,
               N'RegTrack assigns a performer by TWO mechanisms: ComplianceAssignment (on the '
             + N'obligation) and ComplianceScheduleOn.Performerid (on each occurrence, 99.8% '
             + N'populated). This metric counts only the FIRST. Most obligations it counts DO '
             + N'have someone named per occurrence - what is missing is accountability for the '
             + N'obligation itself. NEVER present it as "nobody is doing this". NoOwnerAnywhere '
             + N'is the stricter measure.' AS Detail
        UNION ALL
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

    DROP TABLE #branch; DROP TABLE #stat; DROP TABLE #statActive; DROP TABLE #statOvd; DROP TABLE #statOwn;
    DROP TABLE #int; DROP TABLE #intActive; DROP TABLE #intOvd; DROP TABLE #intOwn;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Internal restored to the 2026-10-27 production definition.';
GO

CREATE OR ALTER PROCEDURE dbo.usp_Insights_Dimension_Event
    @UserID          INT,
    @CustomerID      INT,
    @WindowStart     DATETIME,
    @WindowEnd       DATETIME,
    @AsOf            DATETIME = NULL,
    @DormancyMonths  INT      = 12      -- activity window; 12 months per the spec's observation
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT --------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51120, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    /*-- 1. SCOPED EVENT INSTANCES - branch axis only --------------------*/
    IF OBJECT_ID('tempdb..#branch') IS NOT NULL DROP TABLE #branch;
    SELECT DISTINCT sp.BranchID
    INTO #branch
    FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp;

    /*  [ADDED 2026-09-25, CORRECTED 2026-09-25] HARD WINDOW GATE - caller-supplied
        period, no FY default. First built as a hardcoded current-FY-only cut; that
        was wrong - fiscal-year scoping stays specific to TimelinessFY alone. Every
        other dimension, including this one, takes whatever period the caller
        resolves from the picker (last 30/60/90 days, or a quarter) and passes as
        concrete dates, same shape as sql/23 TimelinessFY and sql/11 Act.
        Event is one of the few dimensions where the base population carries a real
        per-row date (StartDate) that already means what it needs to mean here:
        every detector, EarliestStart/LatestStart and DistinctStartDates below
        already treat StartDate as this instance's own temporal anchor, so
        filtering the population on it does not change what any existing field
        represents - it just narrows WHICH instances are in scope, same as every
        other dimension's scope filters. Half-open range: an instance dated exactly
        @WindowEnd belongs to the NEXT window, not this one.
        Applied to #ei itself - the SAME population @eventTotal and every #rows
        figure is built from - so reconciliation ties out automatically, not
        because anything downstream was touched.                                */
    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51122, N'EVENT DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51122, N'EVENT DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    IF OBJECT_ID('tempdb..#ei') IS NOT NULL DROP TABLE #ei;
    SELECT ei.ID AS EventInstanceID, ei.EventID, ei.CustomerBranchID AS BranchID, ei.StartDate
    INTO #ei
    FROM EventInstance ei
    JOIN CustomerBranch cb ON cb.ID = ei.CustomerBranchID
    JOIN #branch b ON b.BranchID = ei.CustomerBranchID
    WHERE ei.IsDeleted = 0 AND cb.IsDeleted = 0 AND cb.Status = 1 AND cb.CustomerID = @CustomerID
      AND ei.StartDate >= @WindowStart AND ei.StartDate < @WindowEnd;

    CREATE CLUSTERED INDEX IX_ei ON #ei (EventID, EventInstanceID);

    /*  Recent activity means a schedule actually MOVED - held, closed, or
        started - inside the window. A configuration date is not activity. */
    IF OBJECT_ID('tempdb..#recent') IS NOT NULL DROP TABLE #recent;
    SELECT DISTINCT e.EventInstanceID
    INTO #recent
    FROM #ei e
    JOIN EventScheduleOn es ON es.EventInstanceID = e.EventInstanceID
    WHERE es.IsDeleted = 0
      AND (   es.HeldOn     >= DATEADD(MONTH, -@DormancyMonths, @AsOf)
           OR es.ClosedDate >= DATEADD(MONTH, -@DormancyMonths, @AsOf)
           OR es.StartDate  >= DATEADD(MONTH, -@DormancyMonths, @AsOf));

    /*-- 2. MEMBER LIST = the event types actually configured ------------*/
    IF OBJECT_ID('tempdb..#etype') IS NOT NULL DROP TABLE #etype;
    SELECT DISTINCT ev.ID AS EventID, ev.Name AS EventName
    INTO #etype
    FROM Event ev
    WHERE EXISTS (SELECT 1 FROM #ei e WHERE e.EventID = ev.ID);

    /*-- 3. ROWS - one per event type -----------------------------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        EventID              BIGINT         NOT NULL PRIMARY KEY,
        EventName            NVARCHAR(1000) NULL,
        InstanceCount        INT            NOT NULL,
        BranchesCovered      INT            NOT NULL,
        EarliestStart        DATETIME       NULL,
        LatestStart          DATETIME       NULL,
        InstancesSinceCutoff INT            NOT NULL,
        -- derived
        DistinctStartDates   INT            NOT NULL,
        Flags                VARCHAR(200)   NULL
    );

    INSERT #rows (EventID, EventName, InstanceCount, BranchesCovered,
                  EarliestStart, LatestStart, InstancesSinceCutoff, DistinctStartDates)
    SELECT
        t.EventID, t.EventName,
        COUNT(e.EventInstanceID),
        COUNT(DISTINCT e.BranchID),
        MIN(e.StartDate),
        MAX(e.StartDate),
        SUM(CASE WHEN r.EventInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        COUNT(DISTINCT CAST(e.StartDate AS DATE))
    FROM #etype t
    LEFT JOIN #ei     e ON e.EventID = t.EventID
    LEFT JOIN #recent r ON r.EventInstanceID = e.EventInstanceID
    GROUP BY t.EventID, t.EventName;

    /*-- 4. RECONCILIATION ----------------------------------------------*/
    DECLARE @eventTotal INT = (SELECT COUNT(*) FROM #ei);
    DECLARE @rowSum     INT = (SELECT ISNULL(SUM(InstanceCount),0) FROM #rows);

    IF @rowSum <> @eventTotal
        THROW 51121, N'EVENT DIMENSION RECONCILIATION FAILED - per-event-type sums do not tie to the scoped event instance total. Refusing to publish.', 1;

    DECLARE @hasAnyEvents  BIT = CASE WHEN @eventTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @recentTotal   INT = (SELECT COUNT(*) FROM #recent);
    DECLARE @branchesInScope INT = (SELECT COUNT(*) FROM #branch);

    /*-- 5. DETECTIONS ---------------------------------------------------*/
    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyEvents = 1 AND InstanceCount > 0 AND InstancesSinceCutoff = 0
                 THEN ',event_types_never_triggered' ELSE '' END +
            /*  Bulk configuration: every instance of this type shares one or two
                start dates AND nothing has moved since. That is the scaffolding
                signature described in the spec, not a judgement about the tenant. */
            CASE WHEN @hasAnyEvents = 1 AND InstanceCount > 1
                  AND DistinctStartDates <= 2 AND InstancesSinceCutoff = 0
                 THEN ',bulk_configured_never_used' ELSE '' END
        , 1, 1, '');

    /*  Module dormancy is a TENANT-level observation, never a per-type flag. */
    DECLARE @moduleDormant BIT =
        CASE WHEN @hasAnyEvents = 1 AND @recentTotal = 0 THEN 1 ELSE 0 END;

    DECLARE @branchesWithEvents INT = (SELECT COUNT(DISTINCT BranchID) FROM #ei);
    DECLARE @branchesWithout    INT = @branchesInScope - @branchesWithEvents;

    SELECT
        'control_totals'                 AS ResultSet,
        @eventTotal                      AS ScopedInstances,
        @rowSum                          AS SumOfRows,
        CAST(1 AS BIT)                   AS Reconciled,
        (SELECT COUNT(*) FROM #rows)     AS EventTypesReported,
        @recentTotal                     AS InstancesActiveInWindow,
        @DormancyMonths                  AS ActivityWindowMonths,
        @branchesInScope                 AS BranchesInScope,
        @branchesWithEvents              AS BranchesWithEventCoverage,
        @branchesWithout                 AS BranchesWithoutEventCoverage,
        @moduleDormant                   AS EventModuleDormant;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY InstanceCount DESC;

    /*-- 6. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @typesWithInstances INT = (SELECT COUNT(*) FROM #rows WHERE InstanceCount > 0);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'event_types_never_triggered', @typesWithInstances,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%event_types_never_triggered%')
    UNION ALL SELECT 'bulk_configured_never_used', @typesWithInstances,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%bulk_configured_never_used%');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    /*  [ADDED 2026-09-10] DETECTOR CONTRACT - fail at source.
        Flagged and Eligible MUST come from the same population. sql/05 once
        emitted 120 flagged of 99 eligible (121.2%) because a flag had no
        Instances > 0 guard; only the .NET layer caught it, three layers
        downstream. A percentage above 100 reaching a narrative writer is
        indefensible - the writer cannot tell it is impossible, and rendering
        it faithfully produces a false statement.
        Shared code 51040 across all dimensions: same failure class, and the
        message names the offending detector.                                 */
    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51040, N'DETECTOR CONTRACT VIOLATED - a detector flagged more rows than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 7. ASSERTIONS - every one carries the off-system caveat ---------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    DECLARE @offSystem NVARCHAR(200) =
        N'may_be_tracked_off_system: absence of activity in this database is not evidence of absence in the business (open item O-8)';

    IF @hasAnyEvents = 1
    INSERT #assert
    VALUES ('A-ACTIVE','event_instances_active_in_window',N'tenant',@recentTotal,
            NULL,@eventTotal,NULL,NULL,NULL,@offSystem);

    IF @moduleDormant = 1
    INSERT #assert
    VALUES ('A-DORMANT','event_instances_active_in_window',N'tenant',0,
            NULL,@eventTotal,NULL,NULL,NULL,@offSystem);

    IF @branchesWithout > 0 AND @branchesInScope > 0
    INSERT #assert
    VALUES ('A-NOCOVER','branches_without_event_coverage',N'tenant',@branchesWithout,
            NULL,@branchesInScope,NULL,NULL,NULL,@offSystem);

    IF (SELECT EmitMode FROM #detector WHERE Detector='event_types_never_triggered') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-NEVER-' + CAST(ROW_NUMBER() OVER (ORDER BY InstanceCount DESC) AS VARCHAR(5)),
               'instances_active_in_window', EventName, 0, NULL, InstanceCount,
               NULL, NULL, NULL, @offSystem
        FROM #rows WHERE Flags LIKE '%event_types_never_triggered%' ORDER BY InstanceCount DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='event_types_never_triggered') = 'aggregate'
        INSERT #assert
        SELECT 'A-NEVER-AGG','event_types_never_triggered',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL, @offSystem
        FROM #detector WHERE Detector='event_types_never_triggered';

    IF (SELECT EmitMode FROM #detector WHERE Detector='bulk_configured_never_used') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-BULK-' + CAST(ROW_NUMBER() OVER (ORDER BY InstanceCount DESC) AS VARCHAR(5)),
               'distinct_start_dates', EventName, DistinctStartDates, NULL, InstanceCount,
               NULL, NULL, NULL, @offSystem
        FROM #rows WHERE Flags LIKE '%bulk_configured_never_used%' ORDER BY InstanceCount DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='bulk_configured_never_used') = 'aggregate'
        INSERT #assert
        SELECT 'A-BULK-AGG','event_types_bulk_configured',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL, @offSystem
        FROM #detector WHERE Detector='bulk_configured_never_used';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 8. FINDINGS - all phrased as questions --------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    DECLARE @askGuard NVARCHAR(300) =
        N'MUST be put as a question, never an accusation. Events may be tracked off-system; this shows only what the platform holds. Ask whether these are managed elsewhere before treating it as a gap.';

    INSERT #find
    SELECT 'F-DORMANT','high',
           CONCAT(N'No event activity has been recorded in the platform in ', @DormancyMonths,
                  N' months, across ', OfN, N' configured event instance(s)'),
           'A-DORMANT', @askGuard
    FROM #assert WHERE AssertionId = 'A-DORMANT';

    INSERT #find
    SELECT 'F-NOCOVER','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN,
                  N' locations have no event-triggered obligations configured'),
           'A-NOCOVER', @askGuard
    FROM #assert WHERE AssertionId = 'A-NOCOVER';

    INSERT #find
    SELECT 'F-NEVER','medium',
           CONCAT(N'', ScopeLabel, N' has ', OfN, N' configured instance(s) and no recorded activity'),
           AssertionId, @askGuard
    FROM #assert WHERE AssertionId LIKE 'A-NEVER-[0-9]%';

    INSERT #find
    SELECT 'F-NEVER-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' event types (', VsComparatorPP,
                  N'%) have no recorded activity in the window'),
           AssertionId, @askGuard
    FROM #assert WHERE AssertionId = 'A-NEVER-AGG';

    INSERT #find
    SELECT 'F-BULK','info',
           CONCAT(N'', ScopeLabel, N' was configured on ', CAST(Value AS INT),
                  N' date(s) across ', OfN, N' instances, with no activity since'),
           AssertionId,
           N'This is the signature of bulk template configuration rather than live tracking. Put it as a question about how these are managed, not as a failure.'
    FROM #assert WHERE AssertionId LIKE 'A-BULK-[0-9]%';

    INSERT #find
    SELECT 'F-BULK-AGG','info',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' event types (', VsComparatorPP,
                  N'%) show the bulk-configuration signature'),
           AssertionId, @askGuard
    FROM #assert WHERE AssertionId = 'A-BULK-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 9. DATA QUALITY -------------------------------------------------*/
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'event_scope_is_branch_only'           THEN 'ScopedInstances'
                   WHEN 'events_may_be_tracked_off_system'     THEN 'EventModuleDormant'
                   WHEN 'no_events_configured'                 THEN 'EventTypesReported'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to event instances whose StartDate falls between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. An event instance outside this window is excluded entirely, not just its activity figures.') AS Detail
        UNION ALL
        SELECT 'event_scope_is_branch_only' AS Issue,
               N'EventInstance carries no compliance category, so this population is constrained on the '
             + N'BRANCH axis only while every statutory cut is constrained on branch AND category. The '
             + N'two are not like-for-like.' AS Detail
        UNION ALL
        SELECT 'events_may_be_tracked_off_system',
               N'Open item O-8. Event-triggered obligations may legitimately be managed outside this '
             + N'platform. Every dormancy figure here describes the DATABASE, not the business. Verify '
             + N'with the tenant''s operations team before asserting dormancy.'
        WHERE @hasAnyEvents = 1
        UNION ALL
        SELECT 'no_events_configured',
               N'No event instances exist in this scope at all, so nothing can be said about '
             + N'event-triggered compliance either way. This is absence of data, not a finding.'
        WHERE @hasAnyEvents = 0
    ) q;

    DROP TABLE #branch; DROP TABLE #ei; DROP TABLE #recent; DROP TABLE #etype;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Event restored to the 2026-10-27 production definition.';
GO

CREATE OR ALTER FUNCTION dbo.tvfInsightsForwardPipelineSchedules (@CustomerID INT, @AsOf DATETIME)
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
      AND cso.ScheduleOn >= CAST(@AsOf AS DATE)   -- [v2] due today is upcoming (RegTrack DueToday), not overdue
      AND cso.ScheduleOn <= DATEADD(DAY, 90, @AsOf)
      AND d.OverdueEligible = 1   -- [v2] Open only; RegTrack does not list due dates with no transaction
);
GO
PRINT 'tvfInsightsForwardPipelineSchedules restored to the 2026-10-27 production definition.';
GO

CREATE OR ALTER PROCEDURE dbo.usp_Insights_Dimension_CoverageGaps
    @UserID          INT,
    @CustomerID      INT,
    @AsOf            DATETIME     = NULL,
    @CoverageThreshold DECIMAL(5,4) = 0.95,   -- peers carrying it, as a share
    @MinPeers        INT          = 10,       -- smallest peer set worth comparing
    @LabourCategory  INT          = 2
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT - fail closed ---------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51200, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*  Establishment class comes from the DICTIONARY, never a literal, so the
        BA can move Warehouse into retail (or out) without touching code.     */
    IF OBJECT_ID('tempdb..#class') IS NOT NULL DROP TABLE #class;
    SELECT TRY_CAST(p.RawValue AS INT) AS NodeTypeId, p.Meaning AS Class
    INTO #class
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'CustomerBranch.Type' AND TRY_CAST(p.RawValue AS INT) IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM #class)
        THROW 51205, N'DICTIONARY GAP - no CustomerBranch.Type values are classified in InsightsEnumPolarity. Seed the NodeType classification (retail / warehouse / plant / office / nonphysical) and re-run.', 1;

    /*-- 1. THE PEER POPULATION ------------------------------------------
        Leaf, operating, in scope, with a known class. Unknown NodeType values
        (17 exist in production, none in NodeType) are classed 'unknown' and
        excluded - never compared, always declared.                          */
    IF OBJECT_ID('tempdb..#branch') IS NOT NULL DROP TABLE #branch;
    CREATE TABLE #branch (
        BranchID    INT           NOT NULL PRIMARY KEY,
        BranchName  NVARCHAR(600) NULL,
        StateID     INT           NULL,
        NodeTypeId  INT           NULL,
        Class       VARCHAR(20)   NOT NULL,
        Comparable  BIT           NOT NULL    -- retail | warehouse | plant only
    );
    INSERT #branch (BranchID, BranchName, StateID, NodeTypeId, Class, Comparable)
    SELECT DISTINCT cb.ID, cb.Name, cb.StateID, cb.Type,
           ISNULL(c.Class, 'unknown'),
           CASE WHEN ISNULL(c.Class,'unknown') IN ('retail','warehouse','plant') THEN 1 ELSE 0 END
    FROM CustomerBranch cb
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp ON sp.BranchID = cb.ID
    LEFT JOIN #class c ON c.NodeTypeId = cb.Type
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1
      AND NOT EXISTS (SELECT 1 FROM CustomerBranch ch
                      WHERE ch.ParentID = cb.ID AND ch.IsDeleted = 0 AND ch.Status = 1);

    /*-- 2. LABOUR OBLIGATIONS PER BRANCH, and the S&E / threshold tags ------
        [TRAP] Threshold-bearing obligations are identified by ACT NAME pattern
        because no applicability-rules table exists. This is a declared
        approximation, not a precise filter. Extend the patterns as BAs rule.  */
    IF OBJECT_ID('tempdb..#obl') IS NOT NULL DROP TABLE #obl;
    CREATE TABLE #obl (
        BranchID     INT NOT NULL,
        ComplianceID INT NOT NULL,
        ActID        INT NULL,
        ActName      NVARCHAR(500) NULL,
        IsShopsAct   BIT NOT NULL,      -- S&E-derived -> reduced confidence
        IsThresholdObligation BIT NOT NULL,   -- excluded from comparison
        PRIMARY KEY (BranchID, ComplianceID)
    );
    INSERT #obl (BranchID, ComplianceID, ActID, ActName, IsShopsAct, IsThresholdObligation)
    SELECT DISTINCT b.BranchID, i.ComplianceID, a.ID, a.Name,
        CASE WHEN a.Name LIKE '%Shops%' OR a.Name LIKE '%Dookan%' OR a.Name LIKE '%Commercial Establishment%'
             THEN 1 ELSE 0 END,
        CASE WHEN c.ShortDescription LIKE '%creche%'  OR c.ShortDescription LIKE '%cr' + NCHAR(232) + 'che%'
               OR c.ShortDescription LIKE '%canteen%'
               OR c.ShortDescription LIKE '%welfare officer%'
               OR a.Name LIKE '%Contract Labour%'
             THEN 1 ELSE 0 END
    FROM #branch b
    JOIN ComplianceInstance i ON i.CustomerBranchID = b.BranchID AND i.IsDeleted = 0
    JOIN Compliance c ON c.ID = i.ComplianceID AND c.IsDeleted = 0
    JOIN Act a ON a.ID = c.ActID
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp
         ON sp.BranchID = b.BranchID AND sp.CategoryId = a.ComplianceCategoryId
    WHERE a.ComplianceCategoryId = @LabourCategory;

    /*  Peer set = comparable branches that carry at least one Labour obligation. */
    IF OBJECT_ID('tempdb..#peer') IS NOT NULL DROP TABLE #peer;
    SELECT b.BranchID, b.BranchName, b.StateID, b.Class
    INTO #peer
    FROM #branch b
    WHERE b.Comparable = 1 AND b.StateID IS NOT NULL
      AND EXISTS (SELECT 1 FROM #obl o WHERE o.BranchID = b.BranchID);

    IF OBJECT_ID('tempdb..#group') IS NOT NULL DROP TABLE #group;
    SELECT StateID, Class, COUNT(*) AS Peers
    INTO #group
    FROM #peer GROUP BY StateID, Class;

    /*-- 3. NEAR-UNIVERSAL OBLIGATIONS per (state, class) ------------------*/
    IF OBJECT_ID('tempdb..#nu') IS NOT NULL DROP TABLE #nu;
    SELECT o.ComplianceID, p.StateID, p.Class,
           MAX(o.ActName) AS ActName,
           MAX(CAST(o.IsShopsAct AS INT)) AS IsShopsAct,
           COUNT(DISTINCT p.BranchID) AS PeersHaving,
           g.Peers,
           CAST(1.0 * COUNT(DISTINCT p.BranchID) / g.Peers AS DECIMAL(5,4)) AS Coverage
    INTO #nu
    FROM #peer p
    JOIN #obl o ON o.BranchID = p.BranchID AND o.IsThresholdObligation = 0
    JOIN #group g ON g.StateID = p.StateID AND g.Class = p.Class
    WHERE g.Peers >= @MinPeers
    GROUP BY o.ComplianceID, p.StateID, p.Class, g.Peers
    HAVING 1.0 * COUNT(DISTINCT p.BranchID) / g.Peers >= @CoverageThreshold
       AND COUNT(DISTINCT p.BranchID) < g.Peers;     -- someone is missing it

    /*-- 4. THE GAPS: (peer, near-universal obligation it lacks) -----------*/
    IF OBJECT_ID('tempdb..#gap') IS NOT NULL DROP TABLE #gap;
    SELECT p.BranchID, p.BranchName, p.StateID, p.Class,
           nu.ComplianceID, nu.ActName, nu.IsShopsAct, nu.PeersHaving, nu.Peers, nu.Coverage,
           CASE WHEN nu.IsShopsAct = 1 THEN 'reduced' ELSE 'full' END AS Confidence
    INTO #gap
    FROM #nu nu
    JOIN #peer p ON p.StateID = nu.StateID AND p.Class = nu.Class
    WHERE NOT EXISTS (SELECT 1 FROM #obl o
                      WHERE o.BranchID = p.BranchID AND o.ComplianceID = nu.ComplianceID);

    /*-- 5. UNDER-CONFIGURED: materially below the peer-set MEDIAN ---------
        Peer-relative (CLAUDE.md Sec.4): below 60% of the (state, class) median
        distinct-Labour-obligation count. An absolute floor would flag every
        small-format store on every tenant.                                   */
    IF OBJECT_ID('tempdb..#load') IS NOT NULL DROP TABLE #load;
    SELECT p.BranchID, p.StateID, p.Class, COUNT(DISTINCT o.ComplianceID) AS LabourCount
    INTO #load
    FROM #peer p JOIN #obl o ON o.BranchID = p.BranchID
    GROUP BY p.BranchID, p.StateID, p.Class;

    IF OBJECT_ID('tempdb..#median') IS NOT NULL DROP TABLE #median;
    SELECT DISTINCT StateID, Class,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY LabourCount)
               OVER (PARTITION BY StateID, Class) AS MedianCount
    INTO #median FROM #load;

    /*-- 6. PER-BRANCH ROWS - from the BRANCH list, not the gap set ---------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        BranchID              INT           NOT NULL PRIMARY KEY,
        BranchName            NVARCHAR(600) NULL,
        StateID               INT           NULL,
        NodeTypeId            INT           NULL,
        Class                 VARCHAR(20)   NOT NULL,
        InPeerSet             BIT           NOT NULL,
        PeerSetSize           INT           NULL,
        LabourObligations     INT           NOT NULL,
        PeerMedianObligations DECIMAL(9,1)  NULL,
        PctOfPeerMedian       DECIMAL(6,1)  NULL,
        Gaps                  INT           NOT NULL,
        GapsFullConfidence    INT           NOT NULL,
        GapsReducedConfidence INT           NOT NULL,
        UnderConfigured       BIT           NOT NULL,
        GapRank               INT           NULL,
        Flags                 VARCHAR(200)  NULL
    );
    INSERT #rows (BranchID, BranchName, StateID, NodeTypeId, Class, InPeerSet, PeerSetSize,
                  LabourObligations, PeerMedianObligations, PctOfPeerMedian,
                  Gaps, GapsFullConfidence, GapsReducedConfidence, UnderConfigured)
    SELECT b.BranchID, b.BranchName, b.StateID, b.NodeTypeId, b.Class,
           CASE WHEN p.BranchID IS NOT NULL THEN 1 ELSE 0 END,
           g.Peers,
           ISNULL(l.LabourCount, 0),
           m.MedianCount,
           CASE WHEN m.MedianCount > 0 THEN 100.0 * ISNULL(l.LabourCount,0) / m.MedianCount END,
           ISNULL(gp.n, 0), ISNULL(gp.nfull, 0), ISNULL(gp.nred, 0),
           CASE WHEN p.BranchID IS NOT NULL AND g.Peers >= @MinPeers
                 AND m.MedianCount > 0 AND ISNULL(l.LabourCount,0) < 0.60 * m.MedianCount
                THEN 1 ELSE 0 END
    FROM #branch b
    LEFT JOIN #peer   p  ON p.BranchID = b.BranchID
    LEFT JOIN #group  g  ON g.StateID = b.StateID AND g.Class = b.Class
    LEFT JOIN #load   l  ON l.BranchID = b.BranchID
    LEFT JOIN #median m  ON m.StateID = b.StateID AND m.Class = b.Class
    LEFT JOIN (SELECT BranchID, COUNT(*) AS n,
                      SUM(CASE WHEN Confidence='full' THEN 1 ELSE 0 END) AS nfull,
                      SUM(CASE WHEN Confidence='reduced' THEN 1 ELSE 0 END) AS nred
               FROM #gap GROUP BY BranchID) gp ON gp.BranchID = b.BranchID;

    ;WITH rk AS (SELECT BranchID, RANK() OVER (ORDER BY Gaps DESC) AS n FROM #rows WHERE Gaps > 0)
    UPDATE #rows SET GapRank = rk.n FROM #rows JOIN rk ON rk.BranchID = #rows.BranchID;

    UPDATE r SET Flags = STUFF(
          CASE WHEN r.Class = 'unknown'                                  THEN ',unknown_node_type'     ELSE '' END
        + CASE WHEN r.Class IN ('office','nonphysical')                  THEN ',not_comparable_class'  ELSE '' END
        + CASE WHEN r.InPeerSet = 1 AND ISNULL(r.PeerSetSize,0) < @MinPeers THEN ',peer_set_too_small' ELSE '' END
        + CASE WHEN r.Gaps > 0                                           THEN ',coverage_gap'          ELSE '' END
        + CASE WHEN r.UnderConfigured = 1                                THEN ',under_configured'      ELSE '' END
        + CASE WHEN r.InPeerSet = 0 AND b.Comparable = 1                 THEN ',no_labour_obligations' ELSE '' END
        , 1, 1, '')
    FROM #rows r JOIN #branch b ON b.BranchID = r.BranchID;

    /*-- 7. CONTROL TOTALS + RECONCILIATION -------------------------------*/
    DECLARE @gapTotal INT = (SELECT COUNT(*) FROM #gap);
    DECLARE @gapRows  INT = (SELECT ISNULL(SUM(Gaps),0) FROM #rows);
    IF @gapTotal <> @gapRows
        THROW 51201, N'COVERAGE GAPS RECONCILIATION FAILED - per-branch gap counts do not tie to the gap set. Refusing to publish.', 1;

    DECLARE @branchTotal INT = (SELECT COUNT(*) FROM #branch);
    DECLARE @rowTotal    INT = (SELECT COUNT(*) FROM #rows);
    IF @branchTotal <> @rowTotal
        THROW 51202, N'COVERAGE GAPS RECONCILIATION FAILED - rows do not cover every in-scope leaf branch.', 1;

    SELECT 'control_totals' AS ResultSet,
        @branchTotal                                              AS LeafBranchesInScope,
        (SELECT COUNT(*) FROM #peer)                              AS PeerSetBranches,
        (SELECT COUNT(*) FROM #group WHERE Peers >= @MinPeers)    AS PeerGroupsQualifying,
        (SELECT COUNT(*) FROM #group WHERE Peers <  @MinPeers)    AS PeerGroupsTooSmall,
        (SELECT COUNT(*) FROM #nu)                                AS NearUniversalObligations,
        @gapTotal                                                 AS Gaps,
        @gapRows                                                  AS SumOfRowGaps,
        CAST(1 AS BIT)                                            AS Reconciled,
        (SELECT COUNT(*) FROM #gap WHERE Confidence='full')       AS GapsFullConfidence,
        (SELECT COUNT(*) FROM #gap WHERE Confidence='reduced')    AS GapsReducedConfidence,
        (SELECT COUNT(DISTINCT BranchID) FROM #gap)               AS BranchesWithGaps,
        (SELECT COUNT(*) FROM #rows WHERE UnderConfigured = 1)    AS UnderConfiguredBranches,
        (SELECT COUNT(*) FROM #branch WHERE Class = 'unknown')    AS UnknownNodeType,
        (SELECT COUNT(DISTINCT ComplianceID) FROM #obl WHERE IsThresholdObligation = 1) AS ThresholdObligationsExcluded,
        @CoverageThreshold                                        AS CoverageThreshold,
        @MinPeers                                                 AS MinPeers,
        N'Peer key = state x establishment class (CustomerBranch.Type via NodeType). Labour = Act.ComplianceCategoryId. REVIEW CANDIDATES, not violations.' AS Method;

    /*-- 8. ROWS ------------------------------------------------------------*/
    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Gaps DESC, LabourObligations DESC;

    /*-- 9. DETECTOR POLICY (CLAUDE.md Sec.4) ------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
                            FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));
    DECLARE @eligible INT = (SELECT COUNT(*) FROM #rows WHERE InPeerSet = 1);
    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'coverage_gap',     @eligible, (SELECT COUNT(*) FROM #rows WHERE Gaps > 0)
    UNION ALL SELECT 'under_configured', @eligible, (SELECT COUNT(*) FROM #rows WHERE UnderConfigured = 1);
    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible=0 THEN 0 ELSE 100.0*Flagged/Eligible END;
    UPDATE #detector SET EmitMode = CASE WHEN Flagged=0 THEN 'none' WHEN FlaggedPct>20.0 THEN 'aggregate' ELSE 'individual' END;
    /*  [ADDED 2026-09-10] DETECTOR CONTRACT - fail at source.
        Flagged and Eligible MUST come from the same population. sql/05 once
        emitted 120 flagged of 99 eligible (121.2%) because a flag had no
        Instances > 0 guard; only the .NET layer caught it, three layers
        downstream. A percentage above 100 reaching a narrative writer is
        indefensible - the writer cannot tell it is impossible, and rendering
        it faithfully produces a false statement.
        Shared code 51040 across all dimensions: same failure class, and the
        message names the offending detector.                                 */
    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51040, N'DETECTOR CONTRACT VIOLATED - a detector flagged more rows than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 10. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(300),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL, ComparatorValue DECIMAL(18,2) NULL,
        VsComparatorPP DECIMAL(9,2) NULL, Direction VARCHAR(10) NULL, Caveat NVARCHAR(400) NULL);

    INSERT #assert VALUES ('A-GAPS','coverage_gaps',N'tenant',@gapTotal,NULL,NULL,NULL,NULL,NULL,
        N'review candidates - no applicability-rules table exists; a genuine exemption can explain any single gap');

    IF (SELECT EmitMode FROM #detector WHERE Detector='coverage_gap') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-GAP-' + CAST(ROW_NUMBER() OVER (ORDER BY Gaps DESC) AS VARCHAR(5)),
               'coverage_gaps', BranchName, Gaps, GapRank,
               (SELECT COUNT(*) FROM #rows WHERE Gaps > 0), NULL, NULL, NULL,
               CASE WHEN GapsReducedConfidence > 0
                    THEN CONCAT(N'', GapsReducedConfidence, N' of these are S&E-Act obligations - reduced confidence: a store in an unscheduled town may lawfully lack them')
                    ELSE N'review candidates' END
        FROM #rows WHERE Gaps > 0 ORDER BY Gaps DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='coverage_gap') = 'aggregate'
        INSERT #assert
        SELECT 'A-GAP-AGG','branches_with_gaps',N'tenant',Flagged,NULL,Eligible,NULL,FlaggedPct,NULL,
               N'aggregate - gaps are a tenant-wide pattern; check whether a whole obligation set was never rolled out'
        FROM #detector WHERE Detector='coverage_gap';

    /*  The single most useful row: which obligation is missing from the most stores. */
    INSERT #assert
    SELECT TOP 1 'A-TOPOBL','stores_missing_obligation', ActName,
           COUNT(*), NULL, NULL, NULL, NULL, NULL,
           CASE WHEN MAX(IsShopsAct)=1 THEN N'S&E-Act obligation - reduced confidence' ELSE NULL END
    FROM #gap GROUP BY ActName ORDER BY COUNT(*) DESC;

    IF (SELECT EmitMode FROM #detector WHERE Detector='under_configured') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-UNDER-' + CAST(ROW_NUMBER() OVER (ORDER BY PctOfPeerMedian) AS VARCHAR(5)),
               'pct_of_peer_median', BranchName, PctOfPeerMedian, NULL, NULL,
               PeerMedianObligations, PctOfPeerMedian - 100.0, 'worse',
               N'headcount is unavailable - a small-format store may lawfully carry fewer obligations'
        FROM #rows WHERE UnderConfigured = 1 ORDER BY PctOfPeerMedian;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='under_configured') = 'aggregate'
        INSERT #assert
        SELECT 'A-UNDER-AGG','under_configured_branches',N'tenant',Flagged,NULL,Eligible,NULL,FlaggedPct,NULL,
               N'aggregate - likely a format or headcount pattern rather than per-store misses'
        FROM #detector WHERE Detector='under_configured';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 11. FINDINGS - every one carries the review-candidate guard ---------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10), Headline NVARCHAR(400),
                        AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(400) NULL);
    DECLARE @guard NVARCHAR(400) =
        N'Present as items to REVIEW, never as violations or missing compliances. No applicability rules table exists; a lawful exemption can explain any gap.';

    INSERT #find
    SELECT 'F-GAP','high',
           CONCAT(N'', CAST(Value AS INT), N' obligations carried by nearly all peers are absent from individual locations'),
           'A-GAPS', @guard
    FROM #assert WHERE AssertionId='A-GAPS' AND Value > 0;

    INSERT #find
    SELECT 'F-TOPOBL','high',
           CONCAT(N'"', ScopeLabel, N'" is configured on nearly every peer but missing from ', CAST(Value AS INT), N' location(s)'),
           'A-TOPOBL', @guard
    FROM #assert WHERE AssertionId='A-TOPOBL';

    INSERT #find
    SELECT 'F-GAP-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' peer-comparable locations (', VsComparatorPP, N'%) show coverage gaps'),
           'A-GAP-AGG', @guard
    FROM #assert WHERE AssertionId='A-GAP-AGG';

    INSERT #find
    SELECT 'F-UNDER-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' locations (', VsComparatorPP, N'%) carry materially fewer obligations than their peers'),
           'A-UNDER-AGG',
           N'Headcount is unavailable; small-format locations may lawfully carry fewer obligations. Present as a configuration review, not a shortfall.'
    FROM #assert WHERE AssertionId='A-UNDER-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 12. DATA QUALITY - the declared limits of the method ---------------*/
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'review_candidates_only'               THEN 'Gaps'
                   WHEN 'headcount_unavailable'                THEN 'Gaps'
                   WHEN 'shops_act_area_mechanism'             THEN 'GapsReducedConfidence'
                   WHEN 'unknown_node_types'                   THEN 'UnknownNodeType'
                   WHEN 'peer_groups_too_small'                THEN 'PeerGroupsTooSmall'
                   WHEN 'recent_state_amendments'              THEN 'Gaps'
                   WHEN 'branch_store_labelling'               THEN 'LeafBranchesInScope'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'review_candidates_only' AS Issue,
               N'Every gap is an inference from peer configuration, not a confirmed obligation. No statutory-applicability rules table exists.' AS Detail
        UNION ALL
        SELECT 'headcount_unavailable',
               N'Employee headcount is not supplied by the customer. Establishment class (NodeType) is the proxy; threshold-bearing obligations (creche, canteen, contract labour, welfare officer) are excluded rather than flagged.'
        UNION ALL
        SELECT 'shops_act_area_mechanism',
               N'Shops & Establishments Acts apply by scheduled area in UP, Bihar, Jharkhand, Karnataka, Delhi and MP/CG, and statewide only in Maharashtra and Gujarat. Gaps in S&E-derived obligations are emitted at reduced confidence.'
        UNION ALL
        SELECT 'unknown_node_types',
               CONCAT(N'', (SELECT COUNT(*) FROM #branch WHERE Class='unknown'),
                      N' leaf branch(es) carry a Type absent from NodeType and were excluded from comparison.')
        WHERE EXISTS (SELECT 1 FROM #branch WHERE Class='unknown')
        UNION ALL
        SELECT 'peer_groups_too_small',
               CONCAT(N'', (SELECT COUNT(*) FROM #group WHERE Peers < @MinPeers),
                      N' (state, class) group(s) have fewer than ', @MinPeers, N' peers and were not compared.')
        WHERE EXISTS (SELECT 1 FROM #group WHERE Peers < @MinPeers)
        UNION ALL
        SELECT 'recent_state_amendments',
               N'Gujarat and Delhi restricted their S&E Acts to 20+ employees in 2026. Configured absences there may be recent and lawful.'
        UNION ALL
        SELECT 'branch_store_labelling',
               N'NodeType Branch and Store are used interchangeably in reference data and are treated as one class.'
    ) q;

    DROP TABLE #class; DROP TABLE #branch; DROP TABLE #obl; DROP TABLE #peer; DROP TABLE #group;
    DROP TABLE #nu; DROP TABLE #gap; DROP TABLE #load; DROP TABLE #median; DROP TABLE #rows;
    DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_CoverageGaps restored to the 2026-10-27 production definition.';
GO

CREATE OR ALTER PROCEDURE dbo.usp_Insights_Dimension_TimelinessFY
    @UserID INT, @CustomerID INT,
    @WindowStart DATETIME, @WindowEnd DATETIME,
    @AsOf DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @AsOf IS NULL SET @AsOf = SYSDATETIME();

    /*-- 0. PRE-FLIGHT ------------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51172, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    /*-- Window is REQUIRED. Caller always resolves a picker choice to dates. */
    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51177, N'TIMELINESS - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51177, N'TIMELINESS - @WindowEnd must be strictly after @WindowStart.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. WINDOW + SAME-WINDOW-LAST-YEAR COMPARATOR ---------------------- */
    DECLARE @prevWindowStart DATETIME = DATEADD(YEAR, -1, @WindowStart);
    DECLARE @prevWindowEnd   DATETIME = DATEADD(YEAR, -1, @WindowEnd);
    DECLARE @currentFyLabel VARCHAR(40) = CONCAT(CONVERT(VARCHAR(10), @WindowStart, 23), ' to ', CONVERT(VARCHAR(10), @WindowEnd, 23));
    DECLARE @previousFyLabel VARCHAR(40) = CONCAT(CONVERT(VARCHAR(10), @prevWindowStart, 23), ' to ', CONVERT(VARCHAR(10), @prevWindowEnd, 23));

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
               WHEN cso.ScheduleOn >= @WindowStart     AND cso.ScheduleOn < @WindowEnd     THEN 'current_fy'
               WHEN cso.ScheduleOn >= @prevWindowStart AND cso.ScheduleOn < @prevWindowEnd THEN 'previous_fy'
               ELSE 'outside_window'
           END AS FyBucket
    INTO #events
    FROM #scoped sc
    JOIN ComplianceScheduleOn  cso ON cso.ComplianceInstanceID = sc.ComplianceInstanceID
    JOIN ComplianceTransaction t   ON t.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = t.StatusId
    WHERE d.ClosureClass = 'completed' AND d.Timeliness IS NOT NULL
      AND cso.ScheduleOn >= @prevWindowStart AND cso.ScheduleOn < @WindowEnd
      AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1;   -- [v2] switched-off due dates do not count

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

    /*  [ADDED 2026-09-10] DETECTOR CONTRACT - fail at source.
        Flagged and Eligible MUST come from the same population. sql/05 once
        emitted 120 flagged of 99 eligible (121.2%) because a flag had no
        Instances > 0 guard; only the .NET layer caught it, three layers
        downstream. A percentage above 100 reaching a narrative writer is
        indefensible - the writer cannot tell it is impossible, and rendering
        it faithfully produces a false statement.
        Shared code 51040 across all dimensions: same failure class, and the
        message names the offending detector.                                 */
    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51040, N'DETECTOR CONTRACT VIOLATED - a detector flagged more rows than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

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
           CASE WHEN @previousOnTimePct IS NULL THEN N'no year-over-year comparator - the same span one year earlier has no completed events with a known Timeliness classification' ELSE NULL END;

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
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'OnTimePctCurrentFY'
                   WHEN 'no_completed_events_in_window'        THEN 'OnTimePctCurrentFY'
                   WHEN 'no_year_over_year_comparator'         THEN 'YoyChangePP'
                   ELSE NULL END AS AppliesToMetric,
           Detail
    FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'On-time rate computed over the caller-supplied window ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' to ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. Year-over-year is the same span one year earlier (',
                      CONVERT(VARCHAR(10), @prevWindowStart, 23), N' to ', CONVERT(VARCHAR(10), @prevWindowEnd, 23), N').') AS Detail
        UNION ALL
        SELECT 'no_completed_events_in_window',
               N'No completed closure events with a known Timeliness classification in the supplied window - OnTimePctCurrentFY cannot be assessed and is not reported as 0%.'
        WHERE @currentCompleted = 0
        UNION ALL
        SELECT 'no_year_over_year_comparator',
               N'The same span one year earlier has no completed closure events with a known Timeliness classification - year-over-year comparison suppressed.'
        WHERE @previousCompleted = 0
    ) q;

    DROP TABLE #scoped; DROP TABLE #events; DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_TimelinessFY restored to the 2026-10-27 production definition.';
GO

CREATE OR ALTER PROCEDURE dbo.usp_Insights_Dimension_ForwardRisk
    @UserID      INT,
    @CustomerID  INT,
    @AsOf        DATETIME = NULL,
    @HorizonDays INT = 90
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();
    IF @HorizonDays IS NULL OR @HorizonDays <= 0 SET @HorizonDays = 90;

    /*-- 0. PRE-FLIGHT - fail closed before computing anything ------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51190, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- THROWs on dictionary gap

    /*-- 1. SCOPED INSTANCE BASE (full 2-D scope) -------------------------*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT s.ComplianceInstanceID, s.BranchID, c.Imprisonment, c.RiskType
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
    JOIN Compliance c ON c.ID = s.ComplianceID;
    CREATE CLUSTERED INDEX IX_inst ON #inst (ComplianceInstanceID);

    /*-- 2. THE FORWARD WINDOW -------------------------------------------
        Only schedules still OPEN count as "due". An occurrence already
        completed ahead of time is not a deadline.                         */
    IF OBJECT_ID('tempdb..#due') IS NOT NULL DROP TABLE #due;
    SELECT cso.ID AS SchedId, i.ComplianceInstanceID, i.BranchID, cso.ScheduleOn,
           i.Imprisonment, i.RiskType,
           DATEDIFF(DAY, @AsOf, cso.ScheduleOn) AS DaysAway
    INTO #due
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    /*  [PERF] explicit latest-status seek, never the 45.7M-row view - see sql/01 */
    OUTER APPLY (SELECT TOP 1 t.StatusId FROM ComplianceTransaction t
                 WHERE t.ComplianceScheduleOnID = cso.ID
                 ORDER BY t.Dated DESC, t.ID DESC) rct
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = rct.StatusId
    WHERE cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn >  @AsOf
      AND cso.ScheduleOn <= DATEADD(DAY, @HorizonDays, @AsOf)
      AND (d.ClosureClass IS NULL OR d.ClosureClass = 'open');
    CREATE CLUSTERED INDEX IX_due ON #due (ComplianceInstanceID);

    /*-- 3. FACTOR INPUTS ------------------------------------------------*/
    IF OBJECT_ID('tempdb..#owned') IS NOT NULL DROP TABLE #owned;
    /*  [CORRECTED 2026-09-05] F1 previously fired on any instance lacking a
        ComplianceAssignment row - which included 34,171 instances that DO have a
        performer named on every schedule. That overstated the factor 181x and
        inflated the clean_at_risk segment this proc exists to produce.

        F1 now means NO OWNER ANYWHERE - neither mechanism. The weaker signal
        (instance-level assignment missing) is still highly predictive but is a
        DIFFERENT factor, reported separately as F1b.                          */
    IF OBJECT_ID('tempdb..#ownership') IS NOT NULL DROP TABLE #ownership;
    SELECT o.ComplianceInstanceID, o.NoInstanceOwner, o.NoOwnerAnywhere, o.OwnerClass
    INTO #ownership
    FROM dbo.tvfInsightsOwnership(@UserID, @CustomerID) o;
    CREATE CLUSTERED INDEX IX_ownership ON #ownership (ComplianceInstanceID);

    SELECT ca.ComplianceInstanceID,
           MAX(CASE WHEN u.IsDeleted = 1 OR u.IsActive = 0 THEN 1 ELSE 0 END) AS AnyAbsentPerformer
    INTO #owned
    FROM ComplianceAssignment ca
    JOIN [User] u ON u.ID = ca.UserID
    WHERE ca.RoleID = 3 AND ca.UserID > 0
      AND EXISTS (SELECT 1 FROM #due d WHERE d.ComplianceInstanceID = ca.ComplianceInstanceID)
    GROUP BY ca.ComplianceInstanceID;

    IF OBJECT_ID('tempdb..#behind') IS NOT NULL DROP TABLE #behind;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #behind
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    WHERE EXISTS (SELECT 1 FROM #due d WHERE d.ComplianceInstanceID = o.ComplianceInstanceID);

    /*  F4 is PEER-RELATIVE. Branch overdue rate is compared to the tenant
        MEDIAN, never a fixed number - an absolute threshold flagged 60% of one
        tenant's members when tried elsewhere (CLAUDE.md Sec.4).            */
    IF OBJECT_ID('tempdb..#branchRate') IS NOT NULL DROP TABLE #branchRate;
    CREATE TABLE #branchRate (
        BranchID    INT NOT NULL PRIMARY KEY,
        Instances   INT NOT NULL,
        Overdue     INT NOT NULL,
        OverduePct  DECIMAL(5,1) NULL
    );
    INSERT #branchRate (BranchID, Instances, Overdue)
    SELECT i.BranchID, COUNT(*),
           SUM(CASE WHEN ov.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END)
    FROM #inst i
    LEFT JOIN (SELECT DISTINCT ComplianceInstanceID
               FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf)) ov
           ON ov.ComplianceInstanceID = i.ComplianceInstanceID
    GROUP BY i.BranchID;

    UPDATE #branchRate
       SET OverduePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END;

    DECLARE @medianBranchOverduePct DECIMAL(9,4);
    SELECT TOP 1 @medianBranchOverduePct =
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY OverduePct) OVER ()
    FROM #branchRate WHERE Instances > 0;
    SET @medianBranchOverduePct = ISNULL(@medianBranchOverduePct, 0);

    /*  1.5x the median. Relative, and it degrades sensibly: on a healthy tenant
        few branches qualify; on a struggling tenant the bar rises with the
        median, so this never flags everyone.                               */
    DECLARE @stressThreshold DECIMAL(9,4) = @medianBranchOverduePct * 1.5;

    /*-- 4. PER-INSTANCE RISK FACTORS ------------------------------------
        Instance grain, not schedule grain: an obligation due three times in
        the window is ONE thing to worry about, not three.

        [TRAP] Declared explicitly with every column, including the derived
        ones. Do NOT use SELECT ... INTO then ALTER TABLE ADD - T-SQL resolves
        names for the whole batch up front and the later UPDATE fails.      */
    IF OBJECT_ID('tempdb..#atrisk') IS NOT NULL DROP TABLE #atrisk;
    CREATE TABLE #atrisk (
        ComplianceInstanceID BIGINT      NOT NULL PRIMARY KEY,
        BranchID             INT         NOT NULL,
        Imprisonment         BIT         NULL,
        RiskType             INT         NULL,
        DaysAway             INT         NULL,
        CarriedForward       BIT         NOT NULL,   -- segment A
        F1_NoOwnerAnywhere         BIT         NOT NULL,
    F1b_NoInstanceOwner        BIT         NOT NULL,
        F2_OwnerAbsent       BIT         NOT NULL,
        F4_BranchStress      BIT         NOT NULL,
        FactorCount          INT         NULL,
        Segment              VARCHAR(16) NULL        -- carried_forward|clean_at_risk|healthy
    );

    INSERT #atrisk (ComplianceInstanceID, BranchID, Imprisonment, RiskType, DaysAway,
                    CarriedForward, F1_NoOwnerAnywhere, F1b_NoInstanceOwner, F2_OwnerAbsent, F4_BranchStress)
    SELECT
        d.ComplianceInstanceID,
        MIN(d.BranchID),
        MAX(CAST(d.Imprisonment AS INT)),
        MAX(d.RiskType),
        MIN(d.DaysAway),
        CASE WHEN MAX(CASE WHEN b.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END) = 1 THEN 1 ELSE 0 END,
        CASE WHEN MAX(CAST(ISNULL(own.NoOwnerAnywhere,0) AS INT)) = 1 THEN 1 ELSE 0 END,   -- MAX over BIT is invalid in T-SQL
        CASE WHEN MAX(CAST(ISNULL(own.NoInstanceOwner,0) AS INT)) = 1 THEN 1 ELSE 0 END,
        CASE WHEN MAX(ISNULL(o.AnyAbsentPerformer,0)) = 1 THEN 1 ELSE 0 END,
        CASE WHEN @medianBranchOverduePct > 0
                  AND MAX(ISNULL(br.OverduePct,0)) > @stressThreshold THEN 1 ELSE 0 END
    FROM #due d
    LEFT JOIN #owned      o   ON o.ComplianceInstanceID   = d.ComplianceInstanceID
    LEFT JOIN #behind     b   ON b.ComplianceInstanceID   = d.ComplianceInstanceID
    LEFT JOIN #branchRate br  ON br.BranchID              = d.BranchID
    LEFT JOIN #ownership  own ON own.ComplianceInstanceID = d.ComplianceInstanceID
    GROUP BY d.ComplianceInstanceID;

    UPDATE #atrisk
       SET FactorCount = CAST(F1_NoOwnerAnywhere AS INT) + CAST(F2_OwnerAbsent AS INT) + CAST(F4_BranchStress AS INT);  -- F1b is REPORTED, not scored - it is a weaker signal  -- BIT + BIT is not addition in T-SQL

    /*  Segment BEFORE scoring. An instance already carrying an overdue
        occurrence is a known problem arriving again, not a new prediction -
        re-scoring it would double-count what the overdue metric already says. */
    UPDATE #atrisk
       SET Segment = CASE WHEN CarriedForward = 1  THEN 'carried_forward'
                          WHEN FactorCount   >= 1  THEN 'clean_at_risk'
                          ELSE 'healthy' END;

    /*-- 5. PER-BRANCH ROWS ----------------------------------------------
        Built from the BRANCH list, not the fact set, so a branch with nothing
        due still appears - "nothing due here" is itself information.        */
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        BranchID            INT           NOT NULL PRIMARY KEY,
        BranchName          NVARCHAR(300) NULL,
        ScopedInstances     INT           NOT NULL,
        DueInWindow         INT           NOT NULL,   -- distinct instances
        SchedulesInWindow   INT           NOT NULL,   -- occurrences
        DueNext30           INT           NOT NULL,
        DueNext60           INT           NOT NULL,
        DueNext90           INT           NOT NULL,
        ImprisonmentDue     INT           NOT NULL,
        CriticalDue         INT           NOT NULL,
        CarriedForward      INT           NOT NULL,   -- segment A
        CleanAtRisk         INT           NOT NULL,   -- segment B (actionable)
        Healthy             INT           NOT NULL,   -- segment C
        F1_NoOwnerAnywhere        INT           NOT NULL,
    F1b_NoInstanceOwner       INT           NOT NULL,
        F2_OwnerAbsent      INT           NOT NULL,
        F4_BranchStress     INT           NOT NULL,
        CleanAtRiskPct      DECIMAL(5,1)  NULL,       -- of CLEAN work, not of all
        CarriedForwardPct   DECIMAL(5,1)  NULL,
        BranchOverduePct    DECIMAL(5,1)  NULL,
        CleanAtRiskRank     INT           NULL,
        Flags               VARCHAR(200)  NULL
    );

    INSERT #rows (BranchID, BranchName, ScopedInstances, DueInWindow, SchedulesInWindow,
                  DueNext30, DueNext60, DueNext90, ImprisonmentDue, CriticalDue,
                  CarriedForward, CleanAtRisk, Healthy,
                  F1_NoOwnerAnywhere, F1b_NoInstanceOwner, F2_OwnerAbsent, F4_BranchStress)
    SELECT
        cb.ID, cb.Name,
        ISNULL(br.Instances, 0),
        COUNT(DISTINCT a.ComplianceInstanceID),
        ISNULL(sw.SchedCount, 0),
        COUNT(DISTINCT CASE WHEN a.DaysAway <= 30 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.DaysAway <= 60 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.DaysAway <= 90 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.Imprisonment = 1 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.RiskType = 3    THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.Segment = 'carried_forward' THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.Segment = 'clean_at_risk'   THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.Segment = 'healthy'         THEN a.ComplianceInstanceID END),
        ISNULL(SUM(CAST(a.F1_NoOwnerAnywhere    AS INT)), 0),
        ISNULL(SUM(CAST(a.F1b_NoInstanceOwner   AS INT)), 0),
        ISNULL(SUM(CAST(a.F2_OwnerAbsent  AS INT)), 0),
        ISNULL(SUM(CAST(a.F4_BranchStress AS INT)), 0)
    FROM CustomerBranch cb
    JOIN dbo.tvfInsightsEntityTree(@CustomerID) t ON t.BranchID = cb.ID
    LEFT JOIN #branchRate br ON br.BranchID = cb.ID
    LEFT JOIN #atrisk     a  ON a.BranchID  = cb.ID
    LEFT JOIN (SELECT BranchID, COUNT(*) AS SchedCount FROM #due GROUP BY BranchID) sw
           ON sw.BranchID = cb.ID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1
    GROUP BY cb.ID, cb.Name, br.Instances, sw.SchedCount;

    /*  CleanAtRiskPct is deliberately a share of CLEAN work, not of all work.
        Dividing by everything would let a large carried-forward backlog mask a
        serious preventable-risk problem, or vice versa.                      */
    UPDATE r
       SET CleanAtRiskPct    = CASE WHEN (r.CleanAtRisk + r.Healthy) = 0 THEN NULL
                                    ELSE 100.0 * r.CleanAtRisk / (r.CleanAtRisk + r.Healthy) END,
           CarriedForwardPct = CASE WHEN r.DueInWindow = 0 THEN NULL
                                    ELSE 100.0 * r.CarriedForward / r.DueInWindow END,
           BranchOverduePct  = ISNULL(br.OverduePct, 0)
    FROM #rows r LEFT JOIN #branchRate br ON br.BranchID = r.BranchID;

    /*  Rank only branches that actually have something due - "rank 1 of 1" on a
        single-member population is a vacuous claim (CLAUDE.md Sec.4).        */
    ;WITH rk AS (SELECT BranchID, RANK() OVER (ORDER BY CleanAtRiskPct DESC) AS n
                 FROM #rows WHERE (CleanAtRisk + Healthy) > 0)
    UPDATE #rows SET CleanAtRiskRank = rk.n FROM #rows JOIN rk ON rk.BranchID = #rows.BranchID;

    UPDATE #rows
       SET Flags = STUFF(
             CASE WHEN DueInWindow = 0                     THEN ',nothing_due_in_window' ELSE '' END +
             CASE WHEN F1_NoOwnerAnywhere > 0                    THEN ',unowned_work_due'      ELSE '' END +
         CASE WHEN F1b_NoInstanceOwner > 0                   THEN ',no_instance_owner'     ELSE '' END +
             CASE WHEN F2_OwnerAbsent > 0                  THEN ',owner_absent'          ELSE '' END +
             CASE WHEN ImprisonmentDue > 0 AND CleanAtRisk > 0
                                                           THEN ',liability_at_risk'     ELSE '' END +
             CASE WHEN CarriedForwardPct IS NOT NULL AND CarriedForwardPct >= 50.0
                                                           THEN ',backlog_dominated'     ELSE '' END +
             CASE WHEN CleanAtRiskPct IS NOT NULL AND CleanAtRiskPct >= 50.0
                                                           THEN ',majority_of_clean_at_risk' ELSE '' END
           , 1, 1, '');

    /*-- 6. CONTROL TOTALS + RECONCILIATION -------------------------------*/
    DECLARE @dueDistinct INT = (SELECT COUNT(DISTINCT ComplianceInstanceID) FROM #due);
    DECLARE @rowsDue     INT = (SELECT ISNULL(SUM(DueInWindow),0) FROM #rows);

    IF @dueDistinct <> @rowsDue
        THROW 51191, N'FORWARD PIPELINE RECONCILIATION FAILED - per-branch due counts do not tie to the distinct instances in the window. Refusing to publish.', 1;

    DECLARE @scoped INT = (SELECT COUNT(*) FROM #inst);

    SELECT 'control_totals' AS ResultSet,
        @scoped                                                      AS ScopedInstances,
        @HorizonDays                                                 AS HorizonDays,
        @dueDistinct                                                 AS DueInWindow,
        @rowsDue                                                     AS SumOfRowsDue,
        CAST(1 AS BIT)                                               AS Reconciled,
        (SELECT COUNT(*) FROM #due)                                  AS SchedulesInWindow,
        (SELECT COUNT(*) FROM #atrisk WHERE Segment='carried_forward') AS CarriedForward,
        (SELECT COUNT(*) FROM #atrisk WHERE Segment='clean_at_risk')   AS CleanAtRisk,
        (SELECT COUNT(*) FROM #atrisk WHERE Segment='healthy')         AS Healthy,
        (SELECT COUNT(*) FROM #atrisk WHERE Segment='clean_at_risk')   AS PredictedAtRisk,  -- sample-schema alias; prefer CleanAtRisk
        (SELECT COUNT(*) FROM #atrisk WHERE Imprisonment = 1
              AND Segment IN ('carried_forward','clean_at_risk'))      AS ImprisonmentNeedingAttention,
        @medianBranchOverduePct                                      AS TenantMedianBranchOverduePct,
        @stressThreshold                                             AS BranchStressThresholdPct,
        (SELECT COUNT(*) FROM #rows)                                 AS BranchesReported,
        (SELECT COUNT(*) FROM #rows WHERE DueInWindow = 0)           AS BranchesWithNothingDue,
        CASE WHEN @dueDistinct = 0 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS ForwardWindowEmpty,
        N'Segmented: carried_forward = already overdue (known problem recurring); clean_at_risk = no existing overdue but >=1 present risk factor (the actionable set); healthy = neither. NOT a forecast - a count of present facts. Computed index; label as such.' AS Method;

    /*-- 7. ROWS ---------------------------------------------------------*/
    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY CleanAtRisk DESC, CarriedForward DESC, DueInWindow DESC;

    /*-- 8. DETECTOR POLICY (CLAUDE.md Sec.4) ---------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    /*  [FIX] TWO eligible populations, because the flags measure two things.
        Flagged and Eligible MUST come from the same population - the .NET
        contract check rejects the report otherwise, and it is right to.

          @withDue   branches with ANY work due in the window. F1 and F2 count
                     factors across ALL due work, including carried-forward, so
                     this is their denominator.
          @withClean branches with CLEAN work (CleanAtRisk + Healthy > 0). Only
                     the clean-share detectors may use this.

        Using @withClean for unowned_work_due would let a branch whose due work
        is entirely carried-forward be FLAGGED but not ELIGIBLE - the same defect
        that made sql/05 report 120 of 99.                                      */
    DECLARE @withDue   INT = (SELECT COUNT(*) FROM #rows WHERE DueInWindow > 0);
    DECLARE @withClean INT = (SELECT COUNT(*) FROM #rows WHERE (CleanAtRisk + Healthy) > 0);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'unowned_work_due', @withDue,   (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%unowned_work_due%')
    UNION ALL SELECT 'owner_absent',      @withDue,   (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%owner_absent%')
    UNION ALL SELECT 'liability_at_risk', @withClean, (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%liability_at_risk%')
    UNION ALL SELECT 'majority_at_risk',  @withClean, (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%majority_at_risk%');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector SET EmitMode = CASE WHEN Flagged = 0       THEN 'none'
                                         WHEN FlaggedPct > 20.0 THEN 'aggregate'
                                         ELSE 'individual' END;

    /*  [ADDED 2026-09-10] DETECTOR CONTRACT - fail at source.
        Flagged and Eligible MUST come from the same population. sql/05 once
        emitted 120 flagged of 99 eligible (121.2%) because a flag had no
        Instances > 0 guard; only the .NET layer caught it, three layers
        downstream. A percentage above 100 reaching a narrative writer is
        indefensible - the writer cannot tell it is impossible, and rendering
        it faithfully produces a false statement.
        Shared code 51040 across all dimensions: same failure class, and the
        message names the offending detector.                                 */
    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51040, N'DETECTOR CONTRACT VIOLATED - a detector flagged more rows than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 9. TYPED ASSERTIONS - comparatives COMPUTED here ----------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(200),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(300) NULL);

    DECLARE @cleanTotal INT = (SELECT COUNT(*) FROM #atrisk WHERE Segment IN ('clean_at_risk','healthy'));
    DECLARE @tenantAtRiskPct DECIMAL(5,1) =
        CASE WHEN @cleanTotal = 0 THEN NULL
             ELSE 100.0 * (SELECT COUNT(*) FROM #atrisk WHERE Segment='clean_at_risk') / @cleanTotal END;

    INSERT #assert VALUES ('A-DUE','due_in_window',N'tenant',@dueDistinct,NULL,NULL,NULL,NULL,NULL,NULL);

    IF @dueDistinct > 0
        INSERT #assert VALUES ('A-RISK','clean_at_risk_pct',N'tenant',@tenantAtRiskPct,NULL,NULL,NULL,NULL,NULL,
            N'computed index: share of CLEAN upcoming work carrying a present risk factor. Not a forecast.');

    /*  Worst branch, only when there is something to compare against.      */
    DECLARE @rankable INT = @withClean;
    IF @rankable >= 2
        INSERT #assert
        SELECT TOP 1 'A-WORST','clean_at_risk_pct',BranchName,CleanAtRiskPct,CleanAtRiskRank,@rankable,
               @tenantAtRiskPct, CleanAtRiskPct - @tenantAtRiskPct, 'worse',
               N'computed index: share of CLEAN upcoming work carrying a present risk factor. Not a forecast.'
        FROM #rows WHERE CleanAtRiskPct IS NOT NULL
        ORDER BY CleanAtRiskPct DESC, DueInWindow DESC;

    IF (SELECT COUNT(*) FROM #atrisk WHERE Imprisonment = 1 AND Segment IN ('carried_forward','clean_at_risk')) > 0
        INSERT #assert
        SELECT 'A-LIAB','imprisonment_at_risk',N'tenant',
               (SELECT COUNT(*) FROM #atrisk WHERE Imprisonment = 1 AND Segment IN ('carried_forward','clean_at_risk')),
               NULL,@dueDistinct,NULL,NULL,NULL,NULL;

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS - each backed by assertion ids ---------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
                        Headline NVARCHAR(300), AssertionIds VARCHAR(400),
                        NarrativeGuard NVARCHAR(400) NULL);

    INSERT #find
    SELECT 'F-LIAB','high',
           CONCAT(N'', CAST(Value AS INT), N' obligations carrying personal liability fall due in the next ',
                  @HorizonDays, N' days and already carry risk factors'),
           AssertionId,
           N'State as items to review. Do NOT present as predicted failures - this is a factor count, not a forecast.'
    FROM #assert WHERE AssertionId = 'A-LIAB';

    IF (SELECT EmitMode FROM #detector WHERE Detector='unowned_work_due') = 'aggregate'
        INSERT #find
        SELECT 'F-UNOWNED-AGG','high',
               CONCAT(N'', Flagged, N' of ', Eligible, N' locations (', FlaggedPct,
                      N'%) have upcoming obligations with no owner on EITHER mechanism'),
               'A-DUE', NULL
        FROM #detector WHERE Detector='unowned_work_due';
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='unowned_work_due') = 'individual'
        INSERT #find
        SELECT TOP 5 'F-UNOWNED','high',
               CONCAT(N'', BranchName, N' has ', F1_NoOwnerAnywhere, N' upcoming obligations with no performer on either mechanism'),
               'A-DUE', NULL
        FROM #rows WHERE Flags LIKE '%unowned_work_due%' ORDER BY F1_NoOwnerAnywhere DESC;

    IF @rankable >= 2
        INSERT #find
        SELECT 'F-WORST','medium',
               CONCAT(N'', ScopeLabel, N' carries the highest share of preventable risk in clean upcoming work at ', Value, N'%'),
               'A-WORST,A-RISK',
               N'Computed index. Say "carries risk factors", never "will be missed".'
        FROM #assert WHERE AssertionId = 'A-WORST';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY - declared, never silent -----------------------*/
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'ownership_has_two_mechanisms'   THEN 'F1b_NoInstanceOwner'
                   WHEN 'computed_index'                       THEN 'PredictedAtRisk'
                   WHEN 'forward_window_empty'                 THEN 'DueInWindow'
                   WHEN 'branch_stress_unavailable'            THEN 'BranchStressThresholdPct'
                   WHEN 'flow_metric_drift'                    THEN 'CarriedForward'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'ownership_has_two_mechanisms' AS Issue,
               N'RegTrack assigns a performer by TWO mechanisms: ComplianceAssignment (on the '
             + N'obligation) and ComplianceScheduleOn.Performerid (on each occurrence, 99.8% '
             + N'populated). This metric counts only the FIRST. Most obligations it counts DO '
             + N'have someone named per occurrence - what is missing is accountability for the '
             + N'obligation itself. NEVER present it as "nobody is doing this". NoOwnerAnywhere '
             + N'is the stricter measure.' AS Detail
        UNION ALL
        SELECT 'computed_index',
               N'Segments are derived from PRESENT FACTS, not a forecast: carried_forward = already overdue; clean_at_risk = ownerless, absent owner, or branch under stress. No accuracy claim. Label as a computed index wherever rendered.'
        UNION ALL
        SELECT 'forward_window_empty',
               CONCAT(N'No obligations fall due in the next ', @HorizonDays,
                      N' days for this scope. This is a real state, not an error - but note that a frozen or stale database will also produce it.')
        WHERE @dueDistinct = 0
        UNION ALL
        SELECT 'branch_stress_unavailable',
               N'Tenant median branch overdue rate is 0, so the peer-relative branch-stress factor (F4) could not be evaluated and was not applied.'
        WHERE @medianBranchOverduePct = 0
        UNION ALL
        SELECT 'flow_metric_drift',
               N'Both the forward window and the already-behind factor read live data and move between runs.'
    ) q;

    DROP TABLE #ownership; DROP TABLE #inst; DROP TABLE #due; DROP TABLE #owned;
    DROP TABLE #behind; DROP TABLE #branchRate; DROP TABLE #atrisk;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_ForwardRisk restored to the 2026-10-27 production definition.';
GO
