/*===========================================================================
  RegTrack Insights - v3 (2026-10-01): occurrence-level Location
  Object : dbo.usp_Insights_Dimension_Location
  Base   : the definition DEPLOYED on UAT (sql/v2/27 lineage + live hotfixes)

  Change : Instances/Overdue/OverduePct now count every real ComplianceScheduleOn
  OCCURRENCE in the window (one row per real due date), not one row per distinct
  ComplianceInstanceID. Confirmed against the real RegTrack SP
  (Kendo_DetailedReport_Details_Pagination_2.sql) - it emits one row per
  occurrence, and a real Detailed Report export (tenant 1285, Apr-Jun 2026)
  matched this proc EXACTLY on all 18 obligation-carrying branches after this
  change plus sql/v2/36's EventFlag fix (same tenant: 58/18/31.0%, 49/31/63.3%,
  26/25/96.2% ... down to the decimal, tenant total 280 == the export's 280 rows).

  Scope of this change, deliberately narrow: ONLY Instances/Overdue/OverduePct
  move to occurrence grain. ImprisonmentInstances, CriticalInstances,
  DistinctPerformers, DistinctReviewers, ClosureEventsLifetime, NoInstanceOwner
  stay INSTANCE-level (they describe the obligation/compliance itself, not
  something that multiplies per occurrence) - unchanged from the prior version.
  ClosureRatio's denominator is now occurrence-count, not instance-count - its
  formula is unchanged but its real-world meaning shifts accordingly (closure
  events per occurrence, not per instance) - flagged here, not silently hidden.
  onboarding_artifact's Instances>=50 sample-size threshold will qualify more
  branches now that Instances counts occurrences - a real, expected behavior
  shift from this redefinition, not a defect.

  Error block: unchanged (51030-51039, same as the prior version).

  [FOUND LIVE 2026-10-05] #rows.BranchName NVARCHAR(300)/ApexName NVARCHAR(400)
  too narrow for the real source: CustomerBranch.Name is varchar(500), and one
  real branch in production is already 319 chars (over the 300 cap right now).
  Same bug class as the 2026-09-23 Caveat truncation (CLAUDE.md Sec.5) - widened
  both to NVARCHAR(600) defensively. Same fix applied the same day to
  Internal/Entity/CoverageGaps (same BranchName/ApexName source), Act (ActName),
  and Licence (LicenseTypeName).
===========================================================================*/
SET NOCOUNT ON;
GO
CREATE OR ALTER PROCEDURE dbo.usp_Insights_Dimension_Location
    @UserID      INT,
    @CustomerID  INT,
    @WindowStart DATETIME,
    @WindowEnd   DATETIME,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*===================================================================
      0. PRE-FLIGHT - fail closed before computing anything
    ===================================================================*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51030, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- THROWs on dictionary gap

    /*===================================================================
      1. SCOPED INSTANCE BASE (instance-level - drives everything EXCEPT
         Instances/Overdue/OverduePct, which now come from #occ below)
    ===================================================================*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.CategoryId,
        c.Imprisonment,
        c.RiskType,
        c.Frequency
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
    JOIN Compliance c ON c.ID = s.ComplianceID;

    CREATE CLUSTERED INDEX IX_inst ON #inst (BranchID, ComplianceInstanceID);

    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51032, N'LOCATION DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51032, N'LOCATION DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    /*===================================================================
      1b. OCCURRENCE-LEVEL ACTIVE SCHEDULES [CHANGED 2026-10-01] - one row
          per real due date, never collapsed to DISTINCT ComplianceInstanceID.
          This is what Instances/Overdue/OverduePct are now counted over.
    ===================================================================*/
    IF OBJECT_ID('tempdb..#occ') IS NOT NULL DROP TABLE #occ;
    SELECT
        cso.ID AS ScheduleOnID,
        cso.ComplianceInstanceID,
        i.BranchID,
        cso.ScheduleOn
    INTO #occ
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd
      AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1;   -- [v2] switched-off due dates do not count (RegTrack parity)

    CREATE CLUSTERED INDEX IX_occ ON #occ (BranchID, ScheduleOnID);
    CREATE NONCLUSTERED INDEX IX_occ_inst ON #occ (ComplianceInstanceID);

    -- #inst still narrows to window-active instances (needed for Imprisonment/
    -- Critical/DistinctPerformers/ClosureEventsLifetime, all instance-level).
    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #occ o WHERE o.ComplianceInstanceID = i.ComplianceInstanceID);

    /*===================================================================
      2. OVERDUE - OCCURRENCE-LEVEL [OPTIMIZED 2026-10-06, same fix as Act's
         sql/v2/41 - found live via a real execution-plan review there]
         tvfInsightsOverdueSchedules re-derives scope (branch+category) and
         active-performer membership for the WHOLE TENANT, before being
         filtered down to #occ's already-scoped rows here - #occ already
         guarantees both, inherited from #inst (tvfInsightsScopedInstances).
         Replaced with the latest transaction per in-scope schedule, fetched
         once per INSTANCE from #inst directly instead of re-deriving scope
         inside a shared function. Semantics unchanged: a schedule with NO
         transaction ever is NOT counted as overdue, same as the TVF's own
         inner join to vInsightsStatusCurrent did before.
    ===================================================================*/
    IF OBJECT_ID('tempdb..#lt') IS NOT NULL DROP TABLE #lt;
    SELECT x.ComplianceScheduleOnID, x.StatusId, x.ID
    INTO #lt
    FROM (
        SELECT t.ComplianceScheduleOnID, t.StatusId, t.ID,
               ROW_NUMBER() OVER (PARTITION BY t.ComplianceScheduleOnID
                                  ORDER BY t.Dated DESC, t.ID DESC) AS rn
        FROM #inst i
        JOIN dbo.ComplianceTransaction t
              ON t.ComplianceInstanceId = i.ComplianceInstanceID
    ) x
    WHERE x.rn = 1;
    CREATE CLUSTERED INDEX IX_lt ON #lt (ComplianceScheduleOnID);

    IF OBJECT_ID('tempdb..#ovdocc') IS NOT NULL DROP TABLE #ovdocc;
    SELECT o.ScheduleOnID AS ComplianceScheduleOnID
    INTO #ovdocc
    FROM #occ o
    JOIN #lt lt                       ON lt.ComplianceScheduleOnID = o.ScheduleOnID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = lt.StatusId
    WHERE d.OverdueEligible = 1
      AND o.ScheduleOn < CAST(@AsOf AS DATE);

    CREATE CLUSTERED INDEX IX_ovdocc ON #ovdocc (ComplianceScheduleOnID);

    /*===================================================================
      3. ASSIGNMENT + LIFETIME CLOSURE FACTS (instance-level, unchanged)
    ===================================================================*/
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
    SELECT i.BranchID,
           COUNT(DISTINCT CASE WHEN ca.RoleID = 3 THEN ca.UserID END) AS Performers,
           COUNT(DISTINCT CASE WHEN ca.RoleID = 4 THEN ca.UserID END) AS Reviewers
    INTO #people
    FROM #inst i
    JOIN ComplianceAssignment ca ON ca.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE ca.UserID > 0
    GROUP BY i.BranchID;

    IF OBJECT_ID('tempdb..#closures') IS NOT NULL DROP TABLE #closures;
    SELECT i.BranchID, COUNT(*) AS ClosureEventsLifetime
    INTO #closures
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    JOIN ComplianceTransaction t  ON t.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = t.StatusId
    WHERE d.ClosureClass = 'completed'
    GROUP BY i.BranchID;

    /*===================================================================
      4. PER-BRANCH ROWS - Instances/Overdue now counted over #occ
         [CHANGED 2026-10-01]; everything else driven from #inst, unchanged.
    ===================================================================*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        BranchID              INT            NOT NULL PRIMARY KEY,
        BranchName            NVARCHAR(600)  NULL,
        NodeType              VARCHAR(20)    NULL,
        RootKind              VARCHAR(20)    NULL,
        ApexName              NVARCHAR(600)  NULL,
        StateID               INT            NULL,
        StateName             NVARCHAR(200)  NULL,
        Instances             INT            NOT NULL,
        Overdue               INT            NOT NULL,
        NoInstanceOwner             INT            NOT NULL,
        ImprisonmentInstances INT            NOT NULL,
        ImprisonmentOverdue   INT            NOT NULL,
        CriticalInstances     INT            NOT NULL,
        DistinctPerformers    INT            NOT NULL,
        DistinctReviewers     INT            NOT NULL,
        ClosureEventsLifetime INT            NOT NULL,
        ActiveChildren        INT            NOT NULL,
        OverduePct            DECIMAL(5,1)   NULL,
        NoInstanceOwnerPct          DECIMAL(5,1)   NULL,
        ClosureRatio          DECIMAL(9,2)   NULL,
        OverdueRank           INT            NULL,
        PeerStateOverduePct   DECIMAL(5,1)   NULL,
        VsPeerStateNormPP     DECIMAL(9,2)   NULL,
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (BranchID, BranchName, NodeType, RootKind, ApexName, StateID, StateName, Instances, Overdue,
                  NoInstanceOwner, ImprisonmentInstances, ImprisonmentOverdue, CriticalInstances,
                  DistinctPerformers, DistinctReviewers, ClosureEventsLifetime, ActiveChildren)
    SELECT
        cb.ID, cb.Name, t.NodeType, t.RootKind, t.ApexName, cb.StateID, st.Name,
        COUNT(x.ScheduleOnID),
        SUM(CASE WHEN v.ComplianceScheduleOnID IS NOT NULL THEN 1 ELSE 0 END),
        -- NoInstanceOwner stays instance-level (an ownership property of the obligation, not the
        -- occurrence) - counted once per distinct instance still present in #occ for this branch.
        (SELECT COUNT(DISTINCT i2.ComplianceInstanceID) FROM #inst i2
          WHERE i2.BranchID = cb.ID AND NOT EXISTS (SELECT 1 FROM #owned w2 WHERE w2.ComplianceInstanceID = i2.ComplianceInstanceID)),
        ISNULL(MAX(ii.ImprisonmentInstances), 0),
        ISNULL(MAX(ii.ImprisonmentOverdue), 0),
        ISNULL(MAX(ii.CriticalInstances), 0),
        ISNULL(MAX(p.Performers), 0),
        ISNULL(MAX(p.Reviewers), 0),
        ISNULL(MAX(cl.ClosureEventsLifetime), 0),
        (SELECT COUNT(*) FROM CustomerBranch ch WHERE ch.ParentID = cb.ID AND ch.IsDeleted = 0 AND ch.Status = 1)
    FROM CustomerBranch cb
    JOIN dbo.tvfInsightsEntityTree(@CustomerID) t ON t.BranchID = cb.ID
    LEFT JOIN #occ      x  ON x.BranchID = cb.ID
    LEFT JOIN #ovdocc   v  ON v.ComplianceScheduleOnID = x.ScheduleOnID
    LEFT JOIN #people   p  ON p.BranchID = cb.ID
    LEFT JOIN #closures cl ON cl.BranchID = cb.ID
    LEFT JOIN (
        -- [FIX] CLAUDE.md sec.2 trap: EXISTS inside a CASE that is an argument to an aggregate
        -- raises Msg 130 ("Cannot perform an aggregate function on an expression containing an
        -- aggregate or a subquery") - found live on this exact query. Flag each instance first
        -- (via a LEFT JOIN to a pre-computed overdue-instance set), then SUM the flag column.
        SELECT i3.BranchID,
               SUM(CASE WHEN i3.Imprisonment = 1 THEN 1 ELSE 0 END) AS ImprisonmentInstances,
               SUM(CASE WHEN i3.Imprisonment = 1 AND ovd3.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END) AS ImprisonmentOverdue,
               SUM(CASE WHEN i3.RiskType = 3 THEN 1 ELSE 0 END) AS CriticalInstances
        FROM #inst i3
        LEFT JOIN (SELECT DISTINCT x4.ComplianceInstanceID
                   FROM #occ x4
                   JOIN #ovdocc v4 ON v4.ComplianceScheduleOnID = x4.ScheduleOnID) ovd3
               ON ovd3.ComplianceInstanceID = i3.ComplianceInstanceID
        GROUP BY i3.BranchID
    ) ii ON ii.BranchID = cb.ID
    LEFT JOIN dbo.State  st ON st.ID = cb.StateID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1
    GROUP BY cb.ID, cb.Name, t.NodeType, t.RootKind, t.ApexName, cb.StateID, st.Name;

    UPDATE #rows SET
        OverduePct   = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue   / Instances END,
        NoInstanceOwnerPct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * NoInstanceOwner / Instances END,
        ClosureRatio = CASE WHEN Instances = 0 THEN 0 ELSE 1.0 * ClosureEventsLifetime / Instances END;

    ;WITH r AS (SELECT BranchID, RANK() OVER (ORDER BY OverduePct DESC) AS rk
                FROM #rows WHERE Instances > 0)
    UPDATE #rows SET OverdueRank = r.rk FROM #rows JOIN r ON r.BranchID = #rows.BranchID;

    ;WITH sp AS (
        SELECT BranchID, StateID,
               PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY OverduePct) OVER (PARTITION BY StateID) AS PeerMedian,
               COUNT(*) OVER (PARTITION BY StateID) AS PeerGroupSize
        FROM #rows
        WHERE Instances > 0 AND StateID IS NOT NULL
    )
    UPDATE #rows SET PeerStateOverduePct = sp.PeerMedian
    FROM #rows JOIN sp ON sp.BranchID = #rows.BranchID
    WHERE sp.PeerGroupSize >= 2;

    UPDATE #rows SET VsPeerStateNormPP = OverduePct - PeerStateOverduePct
    WHERE PeerStateOverduePct IS NOT NULL;

    DECLARE @sampleSize INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= 50);
    DECLARE @sampleDegraded BIT = CASE WHEN @sampleSize = 0 THEN 1 ELSE 0 END;

    DECLARE @medianRatio DECIMAL(9,4);
    IF @sampleDegraded = 0
        SELECT TOP 1 @medianRatio = PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY ClosureRatio) OVER ()
        FROM #rows WHERE Instances >= 50;
    ELSE
        SELECT TOP 1 @medianRatio = PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY ClosureRatio) OVER ()
        FROM #rows WHERE Instances > 0;

    SET @medianRatio = ISNULL(@medianRatio, 0);

    DECLARE @hasAnyObligations BIT =
        CASE WHEN EXISTS (SELECT 1 FROM #rows WHERE Instances > 0) THEN 1 ELSE 0 END;

    DECLARE @tenantIsOnboarding BIT =
        CASE WHEN @hasAnyObligations = 1 AND @medianRatio < 1.0 THEN 1 ELSE 0 END;
    DECLARE @artifactThreshold DECIMAL(9,4) = @medianRatio * 0.20;

    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @tenantIsOnboarding = 0
                  AND Instances >= 50
                  AND ClosureRatio < @artifactThreshold
                 THEN ',onboarding_artifact' ELSE '' END +
            CASE WHEN Instances > 0 AND (DistinctPerformers <= 1 OR DistinctReviewers <= 1)
                 THEN ',single_point_of_failure' ELSE '' END +
            CASE WHEN NodeType = 'intermediate' THEN ',instances_on_intermediate_node' ELSE '' END +
            CASE WHEN RootKind = 'orphan' THEN ',orphaned_parent_deleted' ELSE '' END +
            CASE WHEN NoInstanceOwnerPct >= 10.0 THEN ',high_no_instance_owner' ELSE '' END +
            CASE WHEN Instances = 0 AND ActiveChildren = 0 THEN ',no_obligations_configured' ELSE '' END +
            CASE WHEN Instances = 0 AND ActiveChildren > 0 THEN ',grouping_node' ELSE '' END
        , 1, 1, '');

    /*===================================================================
      5. CONTROL TOTALS + MANDATORY RECONCILIATION
    ===================================================================*/
    DECLARE @rowSum INT       = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @scopedTotal INT  = (SELECT COUNT(*) FROM #occ);
    -- [FIX 2026-10-01] ScopedInstances must report DISTINCT instances in scope, not
    -- occurrences. @scopedTotal (from #occ) stays the occurrence-level denominator for
    -- OverduePct/reconciliation (RegTrack parity, verified against Excel) - unchanged.
    -- #inst is already window-narrowed (post-DELETE above), so COUNT(*) here is distinct
    -- ComplianceInstanceID by construction (no DISTINCT needed - #inst has one row per instance).
    DECLARE @scopedInstancesDistinct INT = (SELECT COUNT(*) FROM #inst);

    IF @rowSum <> @scopedTotal
        THROW 51031, N'LOCATION DIMENSION RECONCILIATION FAILED - per-branch sums do not tie to the scoped occurrence total. Refusing to publish.', 1;

    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT COUNT(*) FROM #ovdocc) / @scopedTotal END;

    DECLARE @tenantCompletedEvents INT, @tenantOnTimeEvents INT, @tenantNullTimelinessEvents INT;
    SELECT
        @tenantCompletedEvents      = SUM(CASE WHEN d.Timeliness IS NOT NULL THEN 1 ELSE 0 END),
        @tenantOnTimeEvents         = SUM(CASE WHEN d.Timeliness = 'on_time' THEN 1 ELSE 0 END),
        @tenantNullTimelinessEvents = SUM(CASE WHEN d.Timeliness IS NULL THEN 1 ELSE 0 END)
    FROM #inst i
    JOIN ComplianceScheduleOn cso     ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    JOIN ComplianceTransaction t      ON t.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = t.StatusId
    WHERE d.ClosureClass = 'completed';

    SET @tenantCompletedEvents      = ISNULL(@tenantCompletedEvents, 0);
    SET @tenantOnTimeEvents         = ISNULL(@tenantOnTimeEvents, 0);
    SET @tenantNullTimelinessEvents = ISNULL(@tenantNullTimelinessEvents, 0);

    DECLARE @tenantOnTimePct DECIMAL(5,1) =
        CASE WHEN @tenantCompletedEvents = 0 THEN NULL
             ELSE 100.0 * @tenantOnTimeEvents / @tenantCompletedEvents END;

    SELECT
        'control_totals'                       AS ResultSet,
        @scopedInstancesDistinct               AS ScopedInstances,
        @rowSum                                AS SumOfRows,
        CAST(1 AS BIT)                         AS Reconciled,
        (SELECT COUNT(*) FROM #ovdocc)         AS OverdueInstances,
        @tenantOverduePct                      AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows)           AS BranchesReported,
        (SELECT COUNT(*) FROM CustomerBranch
          WHERE CustomerID = @CustomerID AND IsDeleted = 0 AND Status = 1) AS ActiveBranchesInTenant,
        (SELECT COUNT(*) FROM #rows WHERE Instances = 0)    AS BranchesWithNoObligations,
        (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%no_obligations_configured%') AS GhostEntities,
        @medianRatio                           AS TenantMedianClosureRatio,
        @tenantIsOnboarding                    AS TenantIsOnboarding,
        @tenantCompletedEvents                 AS TenantCompletedEvents,
        @tenantOnTimeEvents                    AS TenantOnTimeEvents,
        @tenantOnTimePct                       AS TenantOnTimePct,
        (SELECT COUNT(*) FROM #rows WHERE PeerStateOverduePct IS NOT NULL) AS BranchesWithStatePeerNorm,
        (SELECT COUNT(*) FROM #ownership WHERE NoInstanceOwner = 1) AS NoInstanceOwnerInstances,
        (SELECT COUNT(*) FROM #ownership WHERE NoOwnerAnywhere = 1) AS NoOwnerAnywhereInstances,
        (SELECT COUNT(*) FROM #ownership WHERE HasNoSchedules  = 1) AS InstancesWithNoSchedules,
        (SELECT COUNT(*) FROM #ownership o JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID
          WHERE o.HasNoSchedules = 1 AND i.Frequency IS NULL)     AS NoSchedules_NoFrequency,
        (SELECT COUNT(*) FROM #ownership o JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID
          WHERE o.HasNoSchedules = 1 AND i.Frequency IS NOT NULL) AS NoSchedules_HasFrequency;

    /*===================================================================
      6. ROWS
    ===================================================================*/
    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*===================================================================
      6b. DETECTOR EMISSION POLICY
    ===================================================================*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector      VARCHAR(40) PRIMARY KEY,
        Eligible      INT,
        Flagged       INT,
        FlaggedPct    DECIMAL(5,1),
        EmitMode      VARCHAR(12)
    );

    DECLARE @withObl INT = (SELECT COUNT(*) FROM #rows WHERE Instances > 0);
    DECLARE @allBr   INT = (SELECT COUNT(*) FROM #rows);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'onboarding_artifact', @withObl,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%onboarding_artifact%')
    UNION ALL SELECT 'ghost_entity', @allBr,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%no_obligations_configured%')
    UNION ALL SELECT 'single_point_of_failure', @withObl,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%single_point_of_failure%')
    UNION ALL SELECT 'high_no_instance_owner', @withObl,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%high_no_instance_owner%');

    UPDATE #detector
       SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;

    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51040, N'DETECTOR CONTRACT VIOLATED - a detector flagged more rows than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*===================================================================
      7. TYPED ASSERTIONS
    ===================================================================*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId  VARCHAR(20),
        Metric       VARCHAR(60),
        ScopeLabel   NVARCHAR(300),
        Value        DECIMAL(18,2),
        Rank_        INT NULL,
        OfN          INT NULL,
        ComparatorValue DECIMAL(18,2) NULL,
        VsComparatorPP  DECIMAL(9,2) NULL,
        Direction    VARCHAR(10) NULL,
        Caveat       NVARCHAR(200) NULL
    );

    DECLARE @branchCount INT = (SELECT COUNT(*) FROM #rows);

    INSERT #assert VALUES ('A-TENANT','overdue_pct',N'tenant',@tenantOverduePct,NULL,NULL,NULL,NULL,NULL,NULL);

    DECLARE @rankable INT = (SELECT COUNT(*) FROM #rows WHERE Instances > 0);

    IF @rankable >= 2
    INSERT #assert
    SELECT TOP 1 'A-WORST','overdue_pct',BranchName,OverduePct,OverdueRank,@rankable,
           @tenantOverduePct, OverduePct - @tenantOverduePct,
           CASE WHEN OverduePct > @tenantOverduePct THEN 'worse' ELSE 'better' END, NULL
    FROM #rows WHERE Instances > 0 ORDER BY OverduePct DESC, Instances DESC;

    IF EXISTS (SELECT 1 FROM #rows WHERE ImprisonmentOverdue > 0)
    INSERT #assert
    SELECT TOP 1 'A-WORST-EXPOSURE','imprisonment_overdue_count', BranchName, ImprisonmentOverdue, NULL,
           (SELECT SUM(ImprisonmentOverdue) FROM #rows),
           NULL, NULL, 'worse',
           N'ranked by CONSEQUENCE - count of overdue obligations carrying personal '
         + N'liability, not overdue rate. A higher rate on a smaller, liability-free '
         + N'portfolio is a lesser problem.'
    FROM #rows WHERE ImprisonmentOverdue > 0 ORDER BY ImprisonmentOverdue DESC, Overdue DESC;

    IF EXISTS (SELECT 1 FROM #rows WHERE PeerStateOverduePct IS NOT NULL)
    INSERT #assert
    SELECT TOP 1 'A-PEERSTATE','vs_state_peer_pp', r.BranchName, r.VsPeerStateNormPP, NULL,
           (SELECT COUNT(*) FROM #rows z WHERE z.PeerStateOverduePct IS NOT NULL),
           r.PeerStateOverduePct, r.VsPeerStateNormPP, 'worse',
           N'compared only against this tenant''s own branches in the same state, median basis'
    FROM #rows r
    WHERE r.VsPeerStateNormPP IS NOT NULL
    ORDER BY r.VsPeerStateNormPP DESC, r.Instances DESC;

    INSERT #assert
    SELECT 'A-ONB-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
           'closure_ratio', BranchName, ClosureRatio, NULL, NULL,
           NULL, NULL, NULL,
           N'onboarding_artifact: not a top performer - site has not begun operating'
    FROM #rows WHERE Flags LIKE '%onboarding_artifact%';

    IF (SELECT EmitMode FROM #detector WHERE Detector='single_point_of_failure') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-SPOF-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
               'distinct_reviewers', BranchName,
               CASE WHEN DistinctReviewers < DistinctPerformers THEN DistinctReviewers ELSE DistinctPerformers END,
               NULL, NULL, NULL, NULL, NULL, N'single_point_of_failure'
        FROM #rows WHERE Flags LIKE '%single_point_of_failure%' ORDER BY Instances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='single_point_of_failure') = 'aggregate'
        INSERT #assert
        SELECT 'A-SPOF-AGG','locations_single_point_of_failure',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL,
               N'aggregate - thin staffing is a tenant-wide pattern, not per-site exceptions'
        FROM #detector WHERE Detector='single_point_of_failure';

    DECLARE @tenantNoInstanceOwnerPct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT ISNULL(SUM(NoInstanceOwner),0) FROM #rows) / @scopedTotal END;

    IF (SELECT EmitMode FROM #detector WHERE Detector='high_no_instance_owner') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-OWN-' + CAST(ROW_NUMBER() OVER (ORDER BY NoInstanceOwner DESC) AS VARCHAR(5)),
               'no_instance_owner_pct', BranchName, NoInstanceOwnerPct, NULL, NULL,
               @tenantNoInstanceOwnerPct, NoInstanceOwnerPct - @tenantNoInstanceOwnerPct, 'worse', NULL
        FROM #rows WHERE Flags LIKE '%high_no_instance_owner%' ORDER BY NoInstanceOwner DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='high_no_instance_owner') = 'aggregate'
        INSERT #assert
        SELECT 'A-OWN-AGG','locations_high_no_instance_owner',N'tenant',
               Flagged, NULL, Eligible, @tenantNoInstanceOwnerPct, FlaggedPct, 'worse',
               N'aggregate - unassigned ownership is a tenant-wide pattern'
        FROM #detector WHERE Detector='high_no_instance_owner';

    IF (SELECT EmitMode FROM #detector WHERE Detector='ghost_entity') = 'individual'
        INSERT #assert
        SELECT 'A-GHOST-' + CAST(ROW_NUMBER() OVER (ORDER BY BranchName) AS VARCHAR(5)),
               'configured_obligations', BranchName, 0, NULL, NULL, NULL, NULL, NULL,
               N'entity exists in the structure with no compliance configured'
        FROM #rows WHERE Flags LIKE '%no_obligations_configured%';
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='ghost_entity') = 'aggregate'
        INSERT #assert
        SELECT 'A-GHOST-AGG','locations_without_obligations',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL,
               N'aggregate - empty locations are a structural pattern for this tenant, not per-site exceptions'
        FROM #detector WHERE Detector='ghost_entity';

    INSERT #assert
    SELECT 'A-INT-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
           'instances_on_intermediate_node', BranchName, Instances, NULL, NULL, NULL, NULL, NULL,
           N'held directly on a non-leaf node'
    FROM #rows WHERE Flags LIKE '%instances_on_intermediate_node%' AND Instances > 0;

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*===================================================================
      8. FINDINGS
    ===================================================================*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (
        FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(500), AssertionIds VARCHAR(400),
        NarrativeGuard NVARCHAR(300) NULL
    );

    INSERT #find
    SELECT 'F-WORST','high',
           CONCAT(N'', ScopeLabel, N' has the highest overdue rate at ', Value, N'%'),
           'A-WORST,A-TENANT', NULL
    FROM #assert WHERE AssertionId = 'A-WORST' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-PEERSTATE','medium',
           CONCAT(N'', ScopeLabel, N' runs ', Value, N'pp above the median for this tenant''s branches in the same state'),
           'A-PEERSTATE',
           N'Compared only against same-state branches of THIS tenant. Never a cross-tenant or absolute baseline.'
    FROM #assert WHERE AssertionId = 'A-PEERSTATE' AND Value > 0;

    INSERT #find
    SELECT 'F-ONB','info',
           CONCAT(N'', ScopeLabel, N''' s clean record is an onboarding artifact, not performance'),
           AssertionId,
           N'MUST NOT be presented as a top performer. Lifetime closure events are far below configured obligations.'
    FROM #assert WHERE AssertionId LIKE 'A-ONB-%';

    INSERT #find
    SELECT 'F-SPOF','medium',
           CONCAT(N'', ScopeLabel, N' depends on a single person for performance or review'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-SPOF-[0-9]%';

    INSERT #find
    SELECT 'F-OWN','high',
           CONCAT(N'', ScopeLabel, N' has ', Value, N'% of obligations with no INSTANCE-LEVEL owner'),
           AssertionId,
           N'NOT "nobody is doing this" - most of these have a performer named on each '
         + N'schedule. They lack an owner on the obligation itself. See NoOwnerAnywhere '
         + N'for the stricter measure.'
    FROM #assert WHERE AssertionId LIKE 'A-OWN-[0-9]%';

    INSERT #find
    SELECT 'F-GHOST','high',
           CONCAT(N'', ScopeLabel, N' is a configured location with NO compliance obligations tracked'),
           AssertionId,
           N'A coverage gap, not a clean record. Do not present as compliant.'
    FROM #assert WHERE AssertionId LIKE 'A-GHOST-[0-9]%';

    INSERT #find
    SELECT 'F-GHOST-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' locations (', VsComparatorPP,
                  N'%) have no compliance obligations configured'),
           AssertionId,
           N'Likely a location master of which only a subset is compliance-relevant. '
         + N'Confirm with the tenant before treating as a coverage gap.'
    FROM #assert WHERE AssertionId = 'A-GHOST-AGG';

    INSERT #find
    SELECT 'F-SPOF-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' locations (', VsComparatorPP,
                  N'%) depend on a single performer or reviewer'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId='A-SPOF-AGG';

    INSERT #find
    SELECT 'F-OWN-AGG','high',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' locations (', VsComparatorPP,
                  N'%) have 10%+ of obligations with no INSTANCE-LEVEL owner'),
           AssertionId,
           N'NOT "nobody is doing this" - most have a performer named on each schedule. '
         + N'They lack an owner on the obligation itself.'
    FROM #assert WHERE AssertionId='A-OWN-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*===================================================================
      9. DATA QUALITY
    ===================================================================*/
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'state_peer_norm_median_vs_mean'       THEN 'PeerStateOverduePct'
                   WHEN 'no_schedules_missing_frequency'       THEN 'NoSchedules_NoFrequency'
                   WHEN 'no_schedules_despite_frequency'       THEN 'NoSchedules_HasFrequency'
                   WHEN 'true_overdue_denominator'             THEN 'TenantOverduePct'
                   WHEN 'ownership_has_two_mechanisms'         THEN 'NoInstanceOwnerPct'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'tenant_recently_onboarded'            THEN 'ClosureRatio'
                   WHEN 'degraded_peer_sample'                 THEN 'PeerStateOverduePct'
                   WHEN 'orphaned_entities'                    THEN 'RootKind'
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
             + N'occurrence in the window (matching RegTrack''s own Detailed Report row shape), not '
             + N'distinct compliance obligations. A monthly obligation recurring 3 times in-window '
             + N'now contributes 3 to Instances, not 1. ImprisonmentInstances/CriticalInstances/'
             + N'DistinctPerformers/DistinctReviewers/ClosureEventsLifetime/NoInstanceOwner remain '
             + N'INSTANCE-level (unchanged) - they describe the obligation itself, not the occurrence.' AS Detail
        UNION ALL
        SELECT 'state_peer_norm_median_vs_mean' AS Issue,
               N'PeerStateOverduePct uses the MEDIAN of the tenant''s own branches in that state. '
             + N'BA sign-off on median vs mean for this specific comparison is still pending. '
             + N'Branches in a state with fewer than 2 obligation-carrying peers get NULL, never a '
             + N'self-comparison.' AS Detail
        UNION ALL
        SELECT 'no_schedules_missing_frequency' AS Issue,
               CONCAT(N'', (SELECT COUNT(*) FROM #ownership o JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID
                            WHERE o.HasNoSchedules = 1 AND i.Frequency IS NULL),
                      N' obligation(s) have never generated a schedule because the compliance master '
                    + N'carries NO FREQUENCY - the scheduler has nothing to generate from. This is '
                    + N'MASTER DATA, not a job failure: set Frequency on the compliance records or '
                    + N'deconfigure the obligations. Route to the compliance library team.') AS Detail
        WHERE EXISTS (SELECT 1 FROM #ownership o JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID
                      WHERE o.HasNoSchedules = 1 AND i.Frequency IS NULL)
        UNION ALL
        SELECT 'no_schedules_despite_frequency',
               CONCAT(N'', (SELECT COUNT(*) FROM #ownership o JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID
                            WHERE o.HasNoSchedules = 1 AND i.Frequency IS NOT NULL),
                      N' obligation(s) have a frequency yet STILL no schedule. This is the genuine '
                    + N'defect - small enough to inspect individually. Investigate before the '
                    + N'frequency backlog above.')
        WHERE EXISTS (SELECT 1 FROM #ownership o JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID
                      WHERE o.HasNoSchedules = 1 AND i.Frequency IS NOT NULL)
        UNION ALL
        SELECT 'true_overdue_denominator',
               N'Obligations that cannot generate a schedule cannot be overdue, but they DO count in '
             + N'ScopedInstances. The overdue rate on schedulable work is therefore higher than the '
             + N'headline figure. Quote both, or quote the headline with this caveat attached.'
        WHERE EXISTS (SELECT 1 FROM #ownership WHERE HasNoSchedules = 1)
        UNION ALL
        SELECT 'ownership_has_two_mechanisms',
               N'NoInstanceOwner counts obligations with no ComplianceAssignment row. Many of those DO have '
             + N'a performer named on each schedule (ComplianceScheduleOn.Performerid). NoOwnerAnywhere is '
             + N'the stricter measure. Never present NoInstanceOwner as "nobody is doing this".'
        UNION ALL
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'tenant_recently_onboarded',
               CONCAT(N'Tenant median lifetime-closure ratio is ', CAST(@medianRatio AS VARCHAR(20)),
                      N' (<1.0). Closure history is thin across the whole tenant, so per-site '
                    + N'"not yet operating" flags are suppressed as not meaningful.')
        WHERE @tenantIsOnboarding = 1
        UNION ALL
        SELECT 'degraded_peer_sample',
               N'No branch reaches the 50-instance materiality floor, so the peer baseline was '
             + N'computed from all branches with obligations. Peer-relative comparisons on this '
             + N'tenant are weaker than usual and should be stated with less confidence.'
        WHERE @sampleDegraded = 1
        UNION ALL
        SELECT 'orphaned_entities',
               CONCAT(N'', COUNT(*), N' branch(es) sit under a deleted parent and are reported as top-level.')
        FROM #rows WHERE RootKind = 'orphan' HAVING COUNT(*) > 0
        UNION ALL
        SELECT 'instances_on_intermediate_nodes',
               CONCAT(N'', SUM(Instances), N' instance(s) are held directly on non-leaf nodes and are included in the rollup.')
        FROM #rows WHERE NodeType = 'intermediate' HAVING SUM(Instances) > 0
    ) q;

    DROP TABLE #ownership; DROP TABLE #inst; DROP TABLE #occ; DROP TABLE #lt; DROP TABLE #ovdocc; DROP TABLE #owned; DROP TABLE #people;
    DROP TABLE #closures; DROP TABLE #rows; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Location (v3, occurrence-level) installed.';
GO
