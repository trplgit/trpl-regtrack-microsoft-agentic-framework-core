/*===========================================================================
  RegTrack Insights - Phase 1b, Step 5
  DEPARTMENTS DIMENSION

  Spec reference : docs/DIMENSION_SPECS.md section 4
  Pattern        : sql/05_dimension_location.sql. Emits SIX result sets.
  Error block    : 51080-51089

  -- [TRAP] DepartmentID IS ON THE INSTANCE, NOT THE ASSIGNMENT --------------
  Build rows from the DEPARTMENT list so departments with zero instances still
  appear - an empty department is a configuration finding, not an absence.

  -- [TRAP] NULL DepartmentID ------------------------------------------------
  An instance with no department joins no department row. Left alone that would
  make the per-department sum fall short and THROW on correct data. It is a
  configuration gap, not a defect, so it is counted separately, added back for
  reconciliation, and DECLARED. Never silently dropped, never invented into a
  fake "Unassigned" department that a narrator could name as if it existed.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_Departments', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_Departments;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Departments
    @UserID      INT,
    @CustomerID  INT,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT --------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51080, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    DECLARE @criticalRisk INT = (
        SELECT TRY_CAST(p.RawValue AS INT)
        FROM dbo.InsightsEnumPolarity p
        JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
        WHERE p.Semantic = 'RiskType' AND p.Meaning LIKE N'Critical%');

    IF @criticalRisk IS NULL
        THROW 51085, N'DICTIONARY GAP - no RiskType value is mapped to Critical in InsightsEnumPolarity. Refusing to compute.', 1;

    /*-- 1. SCOPED INSTANCE BASE (DepartmentID lives on the instance) ----*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.RiskType,
        s.Imprisonment,
        ci.DepartmentID
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
    JOIN ComplianceInstance ci ON ci.ID = s.ComplianceInstanceID;

    CREATE CLUSTERED INDEX IX_inst ON #inst (DepartmentID, ComplianceInstanceID);

    /*-- 2. OVERDUE ------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;

    /*-- 3. OWNERSHIP + PEOPLE ------------------------------------------*/
    /*  [CORRECTED 2026-09-05] Ownership has TWO mechanisms - ComplianceAssignment
        (instance-level) AND ComplianceScheduleOn.Performerid (schedule-level,
        99.8% populated). Reading only the first overstated "ownerless" by 181x.
        #owned keeps its original meaning (instance-level assignment) so the rest
        of this proc is unchanged; #ownership carries the full picture.
        [PERF] Materialised and indexed - never joined as an inline TVF.        */
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

    IF OBJECT_ID('tempdb..#people') IS NOT NULL DROP TABLE #people;
    SELECT i.DepartmentID, COUNT(DISTINCT ca.UserID) AS DistinctUsers
    INTO #people
    FROM #inst i
    JOIN ComplianceAssignment ca ON ca.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE ca.UserID > 0 AND i.DepartmentID IS NOT NULL
    GROUP BY i.DepartmentID;

    /*-- 4. MEMBER LIST = the department master --------------------------*/
    IF OBJECT_ID('tempdb..#dept') IS NOT NULL DROP TABLE #dept;
    SELECT d.ID AS DepartmentID, d.Name AS DepartmentName
    INTO #dept
    FROM Department d
    WHERE d.CustomerID = @CustomerID AND d.IsDeleted = 0;

    /*-- 5. ROWS ---------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        DepartmentID          INT            NOT NULL PRIMARY KEY,
        DepartmentName        NVARCHAR(300)  NULL,
        Instances             INT            NOT NULL,
        Overdue               INT            NOT NULL,
        OverduePct            DECIMAL(5,1)   NULL,
        NoInstanceOwner             INT            NOT NULL,
        NoInstanceOwnerPct          DECIMAL(5,1)   NULL,
        ImprisonmentInstances INT            NOT NULL,
        CriticalInstances     INT            NOT NULL,
        DistinctUsers         INT            NOT NULL,
        BranchesCovered       INT            NOT NULL,
        -- derived
        OverdueRank           INT            NULL,
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (DepartmentID, DepartmentName, Instances, Overdue, NoInstanceOwner,
                  ImprisonmentInstances, CriticalInstances, DistinctUsers, BranchesCovered)
    SELECT
        d.DepartmentID, d.DepartmentName,
        COUNT(i.ComplianceInstanceID),
        SUM(CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.ComplianceInstanceID IS NOT NULL AND w.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.RiskType = @criticalRisk THEN 1 ELSE 0 END),
        ISNULL(MAX(p.DistinctUsers), 0),
        COUNT(DISTINCT i.BranchID)
    FROM #dept d
    LEFT JOIN #inst   i ON i.DepartmentID = d.DepartmentID
    LEFT JOIN #ovd    o ON o.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN #owned  w ON w.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN #people p ON p.DepartmentID = d.DepartmentID
    GROUP BY d.DepartmentID, d.DepartmentName;

    /*-- 6. RECONCILIATION, with the unassigned bucket counted back -----*/
    DECLARE @rowSum      INT = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #inst);
    DECLARE @unassigned  INT = (SELECT COUNT(*) FROM #inst i
                                WHERE i.DepartmentID IS NULL
                                   OR NOT EXISTS (SELECT 1 FROM #dept d WHERE d.DepartmentID = i.DepartmentID));

    IF @rowSum + @unassigned <> @scopedTotal
        THROW 51081, N'DEPARTMENTS DIMENSION RECONCILIATION FAILED - per-department sums plus the unassigned bucket do not tie to the scoped instance total. Refusing to publish.', 1;

    DECLARE @hasAnyObligations BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * (SELECT COUNT(*) FROM #ovd) / @scopedTotal END;

    UPDATE #rows SET
        OverduePct   = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue   / Instances END,
        NoInstanceOwnerPct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * NoInstanceOwner / Instances END;

    DECLARE @tenantNoInstanceOwnerPct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT ISNULL(SUM(NoInstanceOwner),0) FROM #rows) / @scopedTotal END;

    /*  Materiality floor on ranking - see the trap note in sql/05. */
    DECLARE @materialityFloor INT = 50;
    DECLARE @materialMembers  INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @materialityFloor);
    DECLARE @rankDegraded     BIT = CASE WHEN @materialMembers < 2 THEN 1 ELSE 0 END;
    DECLARE @rankFloor        INT = CASE WHEN @rankDegraded = 1 THEN 1 ELSE @materialityFloor END;

    ;WITH r AS (SELECT DepartmentID, RANK() OVER (ORDER BY OverduePct DESC) AS rk
                FROM #rows WHERE Instances >= @rankFloor)
    UPDATE #rows SET OverdueRank = r.rk FROM #rows JOIN r ON r.DepartmentID = #rows.DepartmentID;

    /*-- 7. DETECTIONS ---------------------------------------------------*/
    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyObligations = 1 AND Instances >= @rankFloor
                  AND OverduePct > @tenantOverduePct
                 THEN ',worst_department' ELSE '' END +
            CASE WHEN Instances > 0 AND NoInstanceOwnerPct >= 10.0 THEN ',high_no_instance_owner' ELSE '' END +
            CASE WHEN Instances > 0 AND DistinctUsers <= 1  THEN ',single_user_department' ELSE '' END
        , 1, 1, '');

    SELECT
        'control_totals'             AS ResultSet,
        @scopedTotal                 AS ScopedInstances,
        @rowSum                      AS AssignedInstances   /* + UnassignedInstances = ScopedInstances.
                          Renamed from SumOfRows: rows cover only instances WITH a
                          DepartmentID, so a field called SumOfRows compared against
                          ScopedInstances reads as a gap when it is a declared residual. */,
        CAST(1 AS BIT)               AS Reconciled,
        (SELECT COUNT(*) FROM #ovd)  AS OverdueInstances,
        @tenantOverduePct            AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows) AS DepartmentsReported,
        (SELECT COUNT(*) FROM #rows WHERE Instances > 0) AS DepartmentsWithObligations,
        @unassigned                  AS UnassignedInstances,
        CAST(CASE WHEN @scopedTotal = 0 THEN 0
                  ELSE 100.0 * @unassigned / @scopedTotal END AS DECIMAL(5,1)) AS UnassignedPct,
        @tenantNoInstanceOwnerPct          AS TenantNoInstanceOwnerPct;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*-- 8. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @withObl  INT = (SELECT COUNT(*) FROM #rows WHERE Instances > 0);
    DECLARE @material INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'worst_department', @material,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%worst_department%')
    UNION ALL SELECT 'high_no_instance_owner', @withObl,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%high_no_instance_owner%')
    UNION ALL SELECT 'single_user_department', @withObl,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%single_user_department%');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
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

    IF @rankable >= 2
    INSERT #assert
    SELECT TOP 1 'A-WORST-DEPT','overdue_pct',DepartmentName,OverduePct,OverdueRank,@rankable,
           @tenantOverduePct, OverduePct - @tenantOverduePct,
           CASE WHEN OverduePct > @tenantOverduePct THEN 'worse' ELSE 'better' END,
           NULLIF(CONCAT(
               CASE WHEN @rankDegraded = 1
                    THEN CONCAT(N'degraded_ranking_sample: no department reaches the ', @materialityFloor,
                                N'-instance materiality floor. ') ELSE N'' END,
               CASE WHEN @tiedAtTop > 1
                    THEN CONCAT(N'tied_at_top: ', @tiedAtTop, N' departments share this rate - not uniquely the highest. ')
                    ELSE N'' END), N'')
    FROM #rows WHERE Instances >= @rankFloor ORDER BY OverduePct DESC, Instances DESC;

    IF (SELECT EmitMode FROM #detector WHERE Detector='high_no_instance_owner') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-OWN-' + CAST(ROW_NUMBER() OVER (ORDER BY NoInstanceOwner DESC) AS VARCHAR(5)),
               'no_instance_owner_pct', DepartmentName, NoInstanceOwnerPct, NULL, NULL,
               @tenantNoInstanceOwnerPct, NoInstanceOwnerPct - @tenantNoInstanceOwnerPct, 'worse', NULL
        FROM #rows WHERE Flags LIKE '%high_no_instance_owner%' ORDER BY NoInstanceOwner DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='high_no_instance_owner') = 'aggregate'
        INSERT #assert
        SELECT 'A-OWN-AGG','departments_high_no_instance_owner',N'tenant',
               Flagged, NULL, Eligible, @tenantNoInstanceOwnerPct, FlaggedPct, 'worse',
               N'aggregate - unassigned ownership is a tenant-wide pattern'
        FROM #detector WHERE Detector='high_no_instance_owner';

    IF (SELECT EmitMode FROM #detector WHERE Detector='single_user_department') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-SOLO-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
               'distinct_users', DepartmentName, DistinctUsers, NULL, NULL, NULL, NULL, NULL,
               N'single_user_department: the whole function depends on one person'
        FROM #rows WHERE Flags LIKE '%single_user_department%' ORDER BY Instances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='single_user_department') = 'aggregate'
        INSERT #assert
        SELECT 'A-SOLO-AGG','departments_single_user',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL,
               N'aggregate - thin staffing is a tenant-wide pattern, not per-department exceptions'
        FROM #detector WHERE Detector='single_user_department';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS ----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-WORST-DEPT','high',
           CONCAT(N'', ScopeLabel, N' has the highest overdue rate at ', Value, N'%'),
           'A-WORST-DEPT,A-TENANT', NULL
    FROM #assert WHERE AssertionId = 'A-WORST-DEPT' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-OWN','high',
           CONCAT(N'', ScopeLabel, N' has ', Value, N'% of obligations with no assigned owner'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-OWN-[0-9]%';

    INSERT #find
    SELECT 'F-OWN-AGG','high',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' departments (', VsComparatorPP,
                  N'%) have 10%+ of obligations with no assigned owner'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-OWN-AGG';

    INSERT #find
    SELECT 'F-SOLO','medium',
           CONCAT(N'', ScopeLabel, N' depends on a single person'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-SOLO-[0-9]%';

    INSERT #find
    SELECT 'F-SOLO-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' departments (', VsComparatorPP,
                  N'%) depend on a single person'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-SOLO-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY ------------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'unassigned_department',
               CONCAT(N'', @unassigned, N' obligation(s) in this scope carry no department, or a department '
                    + N'absent from the master. They are counted in the tenant total but appear in no '
                    + N'department row. A configuration gap - any per-department statement excludes them.')
        WHERE @unassigned > 0
        UNION ALL
        SELECT 'departments_unused',
               CONCAT(N'', COUNT(*), N' configured department(s) carry no obligations in this scope and are '
                    + N'reported with zero counts rather than omitted.')
        FROM #rows WHERE Instances = 0 HAVING COUNT(*) > 0
    ) q;

    DROP TABLE #inst; DROP TABLE #ovd; DROP TABLE #owned; DROP TABLE #people;
    DROP TABLE #dept; DROP TABLE #rows; DROP TABLE #detector;
    DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Departments dimension installed.';
GO
