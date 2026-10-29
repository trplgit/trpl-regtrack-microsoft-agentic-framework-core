/*===========================================================================
  RegTrack Insights - v3 (2026-10-01): occurrence-level Risk
  Object : dbo.usp_Insights_Dimension_Risk
  Base   : the definition DEPLOYED on UAT (live hotfix lineage)

  Change : same as sql/v2/37 (Location) - Instances/Overdue/OverduePct now
  count every real ComplianceScheduleOn OCCURRENCE in the window, not one row
  per distinct ComplianceInstanceID. EventFlag exclusion already lands via
  sql/v2/36's shared tvfInsightsScopedInstances fix - no separate change needed
  here for that part.

  Scope: ONLY Instances/Overdue/OverduePct move to occurrence grain.
  NoInstanceOwner/ImprisonmentInstances/ImprisonmentOverdue stay INSTANCE-level
  (ownership and imprisonment exposure describe the obligation, not something
  that multiplies per occurrence) - same reasoning as Location v3.

  Error block: unchanged (51060-51069).
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
    -- [FOUND LIVE 2026-10-06, same class as Act's LargestRegulatorInstances fix] @impOnCritical was
    -- already computed here but never exposed - the render agent had no way to cite the real
    -- numerator for ImprisonmentOnCriticalPct and could only derive it from the percentage and
    -- @impTotal, an unverifiable number that gets the whole report refused (CLAUDE.md non-negotiable
    -- #5). Now returned as ImprisonmentOnCriticalInstances.
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
        @impOnCritical                    AS ImprisonmentOnCriticalInstances,
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
PRINT 'usp_Insights_Dimension_Risk (v3, occurrence-level) installed.';
GO
