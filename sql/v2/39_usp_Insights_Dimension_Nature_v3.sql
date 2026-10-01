/*===========================================================================
  RegTrack Insights - v3 (2026-10-01): occurrence-level Nature
  Object : dbo.usp_Insights_Dimension_Nature
  Change : same as sql/v2/37/38 - Instances/Overdue/OverduePct now count every
  real ComplianceScheduleOn occurrence in the window. NoInstanceOwner/
  ImprisonmentInstances/ImprisonmentOverdue/CriticalInstances/FinancialPenalty/
  ClosureRisk stay INSTANCE-level (properties of the obligation itself).
  Error block: unchanged (51070-51079).
===========================================================================*/
SET NOCOUNT ON;
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
PRINT 'usp_Insights_Dimension_Nature (v3, occurrence-level) installed.';
GO
