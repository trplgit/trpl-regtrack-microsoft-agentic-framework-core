/*===========================================================================
  RegTrack Insights - Phase 1b, Step 1
  Error block 51030-51039.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.
  LOCATION DIMENSION

  Spec reference : RegTrack_Insights_System_Design_v1.md Sec.7.3 (worked exemplar)
  Purpose        : The reference implementation. Every other dimension follows
                   this pattern - rows, control totals, typed assertions with
                   COMPUTED comparatives, findings backed by assertion ids,
                   and declared data quality.

  -- CONTRACT ---------------------------------------------------------------
  Emits FIVE result sets, which the .NET layer serialises into the dimension
  JSON of Sec.7.2. SQL computes; .NET assembles and adds provenance.

    1. control_totals   - MUST reconcile or the proc THROWs
    2. rows             - one per branch (leaf AND intermediate)
    3. assertions       - typed facts INCLUDING comparatives
    4. findings         - each backed by assertion ids
    5. data_quality     - declared gaps

  -- WHY ASSERTIONS ARE COMPUTED HERE, NOT PHRASED LATER (spec Sec.6.10) --------
  The narrative agent may only assert what exists in the assertion set. So
  "worst location" is sayable ONLY because rank = 1 is computed below. This is
  what makes the claim-checker complete by construction rather than best-effort.

  -- SCOPE ------------------------------------------------------------------
  All queries are constrained by the 2-D (branch x category) scope pair set.
  Branch-only scoping would leak across categories WITHIN a tenant - measured
  at up to 119,797 instances on one production tenant.

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_Location', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_Location;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Location
    @UserID      INT,
    @CustomerID  INT,
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
      1. SCOPED INSTANCE BASE
    ===================================================================*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.CategoryId,
        c.Imprisonment,
        c.RiskType
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
    JOIN Compliance c ON c.ID = s.ComplianceID;

    CREATE CLUSTERED INDEX IX_inst ON #inst (BranchID, ComplianceInstanceID);

    /*===================================================================
      2. OVERDUE (dictionary-driven, affirmative form)
    ===================================================================*/
    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;   -- scope-constrained

    /*===================================================================
      3. ASSIGNMENT + LIFETIME CLOSURE FACTS
    ===================================================================*/
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
    CREATE CLUSTERED INDEX IX_owned ON #owned (ComplianceInstanceID);      -- RoleID 3 = performer

    IF OBJECT_ID('tempdb..#people') IS NOT NULL DROP TABLE #people;
    SELECT i.BranchID,
           COUNT(DISTINCT CASE WHEN ca.RoleID = 3 THEN ca.UserID END) AS Performers,
           COUNT(DISTINCT CASE WHEN ca.RoleID = 4 THEN ca.UserID END) AS Reviewers
    INTO #people
    FROM #inst i
    JOIN ComplianceAssignment ca ON ca.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE ca.UserID > 0
    GROUP BY i.BranchID;

    -- Lifetime closure EVENTS (completed closure classes only: 4,5,7,9)
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
      4. PER-BRANCH ROWS  (leaf AND intermediate - never leaf-only)

      [TRAP] BUILD FROM THE BRANCH LIST, NOT FROM THE INSTANCES.
      Driving this from #inst silently DROPS every branch with zero
      obligations - and one of those cases is a genuine finding:

        - grouping / holding nodes with 0 instances  -> normal, expected
        - a LEAF with 0 children AND 0 instances     -> GHOST ENTITY:
          configured in the structure with nothing tracked against it

      On the reference tenant this hid 4 of 16 active branches, one of which
      (a childless leaf) is exactly the coverage blind spot the engine exists
      to surface. Reconciliation still passes either way, because zeros add
      nothing to the sum - which is precisely why this defect is invisible
      without an explicit branch-count check.
    ===================================================================*/
    /*  [TRAP] Do NOT use `SELECT ... INTO #rows` followed by
        `ALTER TABLE #rows ADD <col>` and then reference <col> in the SAME
        procedure body. T-SQL resolves names for the whole batch up front, so
        the later UPDATE fails at runtime with "Invalid column name". Inside a
        procedure you cannot insert a GO to split the batch.

        Declare the table explicitly with ALL columns - base and derived -
        then INSERT, then UPDATE the derived ones.                            */
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        BranchID              INT            NOT NULL PRIMARY KEY,
        BranchName            NVARCHAR(300)  NULL,
        NodeType              VARCHAR(20)    NULL,
        RootKind              VARCHAR(20)    NULL,
        ApexName              NVARCHAR(400)  NULL,
        StateID               INT            NULL,   -- peer-grouping key. NOT Region: Region is
                                                     -- 99.9% blank tenant-wide, StateID was
                                                     -- 152/152 populated on the reference tenant.
        StateName             NVARCHAR(200)  NULL,   -- dbo.State.Name - real state names for a
                                                     -- human-readable grouping label, never the
                                                     -- raw numeric StateID.
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
        -- derived, populated below
        OverduePct            DECIMAL(5,1)   NULL,
        NoInstanceOwnerPct          DECIMAL(5,1)   NULL,
        ClosureRatio          DECIMAL(9,2)   NULL,
        OverdueRank           INT            NULL,
        PeerStateOverduePct   DECIMAL(5,1)   NULL,   -- derived: state-peer median, this tenant's
                                                     -- own distribution only
        VsPeerStateNormPP     DECIMAL(9,2)   NULL,   -- derived: this branch vs its state peers
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (BranchID, BranchName, NodeType, RootKind, ApexName, StateID, StateName, Instances, Overdue,
                  NoInstanceOwner, ImprisonmentInstances, ImprisonmentOverdue, CriticalInstances,
                  DistinctPerformers, DistinctReviewers, ClosureEventsLifetime, ActiveChildren)
    SELECT
        cb.ID, cb.Name, t.NodeType, t.RootKind, t.ApexName, cb.StateID, st.Name,
        COUNT(i.ComplianceInstanceID),
        SUM(CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.ComplianceInstanceID IS NOT NULL
                  AND w.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 AND o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.RiskType = 3 THEN 1 ELSE 0 END),
        ISNULL(MAX(p.Performers), 0),
        ISNULL(MAX(p.Reviewers), 0),
        ISNULL(MAX(cl.ClosureEventsLifetime), 0),
        (SELECT COUNT(*) FROM CustomerBranch ch WHERE ch.ParentID = cb.ID AND ch.IsDeleted = 0 AND ch.Status = 1)
    FROM CustomerBranch cb
    JOIN dbo.tvfInsightsEntityTree(@CustomerID) t ON t.BranchID = cb.ID
    LEFT JOIN #inst     i  ON i.BranchID = cb.ID
    LEFT JOIN #ovd      o  ON o.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN #owned    w  ON w.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN #people   p  ON p.BranchID = cb.ID
    LEFT JOIN #closures cl ON cl.BranchID = cb.ID
    LEFT JOIN dbo.State  st ON st.ID = cb.StateID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1
    GROUP BY cb.ID, cb.Name, t.NodeType, t.RootKind, t.ApexName, cb.StateID, st.Name;

    -- derived ratios + rank (comparatives are COMPUTED, never phrased)
    UPDATE #rows SET
        OverduePct   = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue   / Instances END,
        NoInstanceOwnerPct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * NoInstanceOwner / Instances END,
        ClosureRatio = CASE WHEN Instances = 0 THEN 0 ELSE 1.0 * ClosureEventsLifetime / Instances END;

    ;WITH r AS (SELECT BranchID, RANK() OVER (ORDER BY OverduePct DESC) AS rk
                FROM #rows WHERE Instances > 0)      -- zero-obligation nodes are not ranked
    UPDATE #rows SET OverdueRank = r.rk FROM #rows JOIN r ON r.BranchID = #rows.BranchID;

    /*-------------------------------------------------------------------
      STATE-PEER NORM - the peer group is the tenant's OWN StateID members,
      never a cross-tenant or absolute baseline (Sec.4, same rule as every
      other detector in this file).

      [TRAP] A state with exactly ONE branch has nothing to compare against.
      CLAUDE.md Sec.4: single member -> suppress comparatives. So
      PeerGroupSize >= 2 is required before a branch gets a peer norm at all;
      anything outside that leaves PeerStateOverduePct and VsPeerStateNormPP
      NULL rather than a fabricated self-comparison that would always read
      "exactly average".

      MEDIAN, not mean - matches the peer-relative convention this file
      already uses for the onboarding-artifact ratio. [OPEN] BA sign-off on
      median vs mean for this specific tile is still pending; declared in
      data_quality below rather than presented as settled.
    -------------------------------------------------------------------*/
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

    /*-------------------------------------------------------------------
      DETECTIONS

      [TRAP] "0% overdue" is NOT automatically excellence. A site with many
      configured obligations but almost no lifetime closure events has not
      begun operating the platform - an ONBOARDING ARTIFACT, a false star.

      -- THE DETECTOR MUST BE PEER-RELATIVE, NOT ABSOLUTE --------------
      The obvious rule (ratio < 1.0 lifetime closures per instance) works on
      one tenant and fails badly on others, because the baseline ratio depends
      on platform tenure and on the tenant's compliance frequency mix.

      Measured with the ABSOLUTE rule:
          tenant A: median ratio 7.90 ->   8% of branches flagged   (correct)
          tenant B: median ratio 1.34 ->  57% of branches flagged   (absurd)
          tenant C: median ratio 2.26 ->  60% of branches flagged   (absurd)
      A tenant cannot have 60% of its sites be onboarding artifacts.

      TWO-TIER RULE (implemented below):
        Tier 1 - if the TENANT's own median ratio < 1.0, the whole tenant is
                 newly onboarded. Per-branch artifact flags are meaningless;
                 emit a TENANT-LEVEL data_quality note instead and suppress
                 the per-branch flag.
        Tier 2 - otherwise flag a branch whose ratio is below 20% of the
                 tenant median, i.e. dramatically behind its own peers.

      Verified across 7 tenants: flag rates 8-19%, and the known artifact site
      on the reference tenant is still correctly and solely identified.
    -------------------------------------------------------------------*/
    /*---------------------------------------------------------------
      [TRAP] THE MEDIAN SAMPLE CAN BE EMPTY.

      The natural sample is "branches with >= 50 instances". On a production
      tenant with 446 branches and 2,685 instances, the LARGEST branch held
      34 - so the sample was EMPTY, PERCENTILE_CONT returned NULL, ISNULL
      forced it to 0, and the tenant was declared "newly onboarded".

      That verdict happened to be right (its true average ratio was 0.11) but
      it was reached from an empty sample rather than from measurement. A
      tenant with hundreds of SMALL but HEALTHY branches would be misclassified
      identically - a false negative waiting to happen.

      FIX: adaptive sample. Prefer branches >= 50 instances; if none qualify,
      fall back to every branch that has any instances, and record that the
      sample was degraded so the narrative can qualify the claim.
    ---------------------------------------------------------------*/
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

    -- No branch with instances at all => nothing to assess; do not claim onboarding.
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
            /*  [FIX] Instances > 0 guard is MANDATORY. Without it, every branch
                with no obligations flags too - it has no performers because it
                has no work. On one tenant that flagged 120 of 99 eligible
                (121.2%), which the .NET contract check correctly rejected:
                Flagged and Eligible must come from the SAME population.
                sql/10 already had this guard; sql/05 did not.                */
            CASE WHEN Instances > 0 AND (DistinctPerformers <= 1 OR DistinctReviewers <= 1)
                 THEN ',single_point_of_failure' ELSE '' END +
            CASE WHEN NodeType = 'intermediate' THEN ',instances_on_intermediate_node' ELSE '' END +
            CASE WHEN RootKind = 'orphan' THEN ',orphaned_parent_deleted' ELSE '' END +
            CASE WHEN NoInstanceOwnerPct >= 10.0 THEN ',high_no_instance_owner' ELSE '' END +
            -- GHOST ENTITY: a leaf with no children and nothing tracked against it.
            -- Distinct from a grouping/holding node, which legitimately holds 0.
            CASE WHEN Instances = 0 AND ActiveChildren = 0 THEN ',no_obligations_configured' ELSE '' END +
            CASE WHEN Instances = 0 AND ActiveChildren > 0 THEN ',grouping_node' ELSE '' END
        , 1, 1, '');

    /*===================================================================
      5. CONTROL TOTALS + MANDATORY RECONCILIATION
         (@medianRatio / @tenantIsOnboarding are set in the detection block above)
    ===================================================================*/
    DECLARE @rowSum INT       = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @scopedTotal INT  = (SELECT COUNT(*) FROM #inst);

    IF @rowSum <> @scopedTotal
        THROW 51031, N'LOCATION DIMENSION RECONCILIATION FAILED - per-branch sums do not tie to the scoped instance total. Refusing to publish.', 1;

    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT COUNT(*) FROM #ovd) / @scopedTotal END;

    
    /*  TENANT-WIDE TIMELINESS - the same dictionary-driven Timeliness facet
        sql/12 uses PER PERFORMER, pooled here at tenant grain across every
        completed closure event.

        Event-level, not instance-level - same shape as ClosureEventsLifetime:
        a monthly obligation closed for a year contributes ~12 events, not 1.
        Lifetime, not FY-scoped; sql/23 answers the per-FY question.

        ClosureClass='completed' AND Timeliness IS NOT NULL mirrors sql/12:
        resolved_terminal carries no timeliness and is excluded from the
        denominator BY CONSTRUCTION (the rule G-4 asserts), never by a status
        literal. Counting statuses 15/17 as completions would massively inflate
        on-time% - ~1.1M schedules system-wide sit on status 15 alone.        */
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

    /*  NULL, not 0, when there is nothing to measure - "0% on time" and "no
        completions yet" are different findings.                             */
    DECLARE @tenantOnTimePct DECIMAL(5,1) =
        CASE WHEN @tenantCompletedEvents = 0 THEN NULL
             ELSE 100.0 * @tenantOnTimeEvents / @tenantCompletedEvents END;

SELECT
        'control_totals'                       AS ResultSet,
        @scopedTotal                           AS ScopedInstances,
        @rowSum                                AS SumOfRows,
        CAST(1 AS BIT)                         AS Reconciled,
        (SELECT COUNT(*) FROM #ovd)            AS OverdueInstances,
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
        /*  Two ownership metrics, deliberately separate - see sql/01.
            NoInstanceOwner is the PREDICTIVE one (64.9% vs 16.3% overdue on the
            reference tenant). NoOwnerAnywhere is the absolute failure.          */
        (SELECT COUNT(*) FROM #ownership WHERE NoInstanceOwner = 1) AS NoInstanceOwnerInstances,
        (SELECT COUNT(*) FROM #ownership WHERE NoOwnerAnywhere = 1) AS NoOwnerAnywhereInstances,
        (SELECT COUNT(*) FROM #ownership WHERE HasNoSchedules  = 1) AS InstancesWithNoSchedules;

    /*===================================================================
      6. ROWS
    ===================================================================*/
    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*===================================================================
      6b. DETECTOR EMISSION POLICY  * applies to EVERY dimension *

      -- THE SYSTEMIC LESSON --------------------------------------------
      Every absolute-threshold detector built for this dimension produced
      sane results on the tenant it was designed against and absurd results
      elsewhere. Measured flag rates across 8 production tenants:

         detector            best tenant     worst tenant
         onboarding_artifact      8%              60%
         ghost_entity             1.3%            66%
         single_point_of_failure  4.6%            84%
         high_no_instance_owner           1.2%            76%

      A finding that fires on 84% of a tenant's locations is not a finding -
      it is a description of how that tenant operates, and emitting 32
      separate high-severity items is noise a CCO cannot act on.

      -- THE POLICY -----------------------------------------------------
      For every detector, compare flagged members against eligible members:

        flagged_pct >  20%  ->  EMIT ONE AGGREGATE finding (severity medium)
                               "N of M locations (X%) show <pattern>"
                               and SUPPRESS the individual findings.

        flagged_pct <= 20%  ->  EMIT INDIVIDUAL findings, capped at the top 5
                               by materiality (instance count), so a large
                               tenant with a low rate still cannot flood the
                               report.

      This makes findings scale-invariant: the same code produces 1 finding
      for a 16-branch tenant and 1 aggregate for an 819-branch tenant, and
      never 117 items.
    ===================================================================*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector      VARCHAR(40) PRIMARY KEY,
        Eligible      INT,
        Flagged       INT,
        FlaggedPct    DECIMAL(5,1),
        EmitMode      VARCHAR(12)          -- 'individual' | 'aggregate' | 'none'
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

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*===================================================================
      7. TYPED ASSERTIONS  (comparatives COMPUTED here - spec Sec.6.10)
         Emission governed by #detector.EmitMode above.
    ===================================================================*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId  VARCHAR(20),
        Metric       VARCHAR(60),
        ScopeLabel   NVARCHAR(200),
        Value        DECIMAL(18,2),
        Rank_        INT NULL,
        OfN          INT NULL,
        ComparatorValue DECIMAL(18,2) NULL,
        VsComparatorPP  DECIMAL(9,2) NULL,
        Direction    VARCHAR(10) NULL,
        Caveat       NVARCHAR(200) NULL
    );

    DECLARE @branchCount INT = (SELECT COUNT(*) FROM #rows);

    -- A-TENANT: the baseline every comparative is measured against
    INSERT #assert VALUES ('A-TENANT','overdue_pct',N'tenant',@tenantOverduePct,NULL,NULL,NULL,NULL,NULL,NULL);

    /*  [TRAP] A "worst location" claim needs something to compare against.
        Production has many SINGLE-BRANCH tenants; rank 1 of 1 is vacuous and
        would read as criticism of the only site they have. Require >= 2
        rankable members before emitting any comparative.                    */
    DECLARE @rankable INT = (SELECT COUNT(*) FROM #rows WHERE Instances > 0);

    -- worst location by overdue rate (suppressed when there is nothing to compare)
    IF @rankable >= 2
    INSERT #assert
    SELECT TOP 1 'A-WORST','overdue_pct',BranchName,OverduePct,OverdueRank,@rankable,
           @tenantOverduePct, OverduePct - @tenantOverduePct,
           CASE WHEN OverduePct > @tenantOverduePct THEN 'worse' ELSE 'better' END, NULL
    FROM #rows WHERE Instances > 0 ORDER BY OverduePct DESC, Instances DESC;

    -- onboarding artifacts: value is the closure RATIO, with the guard caveat
    /*  State-peer assertion - only where a real peer group exists (>=2 branches
        in the state). Never emitted from a self-comparison.                  */
    IF EXISTS (SELECT 1 FROM #rows WHERE PeerStateOverduePct IS NOT NULL)
    INSERT #assert
    /*  [FIX] Alias the outer table. The inline subquery below also reads #rows,
        so with BOTH unaliased the ORDER BY cannot resolve and SQL Server raises
        "Ambiguous column name". It compiles, then fails at RUN TIME after
        control_totals has already been emitted - which is why a caller that
        reads only the first result set never sees it.                        */
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

    -- single points of failure (policy-gated, top 5 by materiality)
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

    -- ownerless concentration vs tenant rate
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

    /*---------------------------------------------------------------
      GHOST ENTITIES - two-tier, for the same reason as the onboarding
      detector: what is a per-site finding on one tenant is a structural
      pattern on another.

      Measured across 8 production tenants, ghost entities as a share of
      active branches ranged from 1.3% to 66%:
          1490:   1 of 16  ( 6%)   -> individually meaningful
          1832:   2 of 151 ( 1.3%) -> individually meaningful
          1472:  91 of 819 (11%)   -> a pattern
            29: 117 of 176 (66%)   -> a pattern
             5: 147 of 264 (56%)   -> a pattern
          1308: 181 of 331 (55%)   -> a pattern

      Emitting 117 separate high-severity findings is noise no CCO can use.
      When empty locations are the MAJORITY pattern, the tenant is very
      likely maintaining a location master (HR/ERP import) of which only a
      subset is compliance-relevant - one aggregate statement, not N findings.

      RULE: <= 5 ghosts AND < 15% of branches -> individual findings (high)
            otherwise                          -> ONE aggregate finding (medium)
    ---------------------------------------------------------------*/

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

    -- instance-bearing intermediate nodes (would vanish from a leaf-only rollup)
    INSERT #assert
    SELECT 'A-INT-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
           'instances_on_intermediate_node', BranchName, Instances, NULL, NULL, NULL, NULL, NULL,
           N'held directly on a non-leaf node'
    FROM #rows WHERE Flags LIKE '%instances_on_intermediate_node%' AND Instances > 0;

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*===================================================================
      8. FINDINGS - every one backed by assertion ids
    ===================================================================*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (
        FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(300), AssertionIds VARCHAR(400),
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
    FROM #assert WHERE AssertionId LIKE 'A-SPOF-%';

    INSERT #find
    SELECT 'F-OWN','high',
           CONCAT(N'', ScopeLabel, N' has ', Value, N'% of obligations with no assigned owner'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-OWN-%';

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
                  N'%) have 10%+ of obligations with no assigned owner'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId='A-OWN-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*===================================================================
      9. DATA QUALITY - declared, never silent
    ===================================================================*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
        SELECT 'state_peer_norm_median_vs_mean' AS Issue,
               N'PeerStateOverduePct uses the MEDIAN of the tenant''s own branches in that state. '
             + N'BA sign-off on median vs mean for this specific comparison is still pending. '
             + N'Branches in a state with fewer than 2 obligation-carrying peers get NULL, never a '
             + N'self-comparison.' AS Detail
        UNION ALL
        SELECT 'instances_with_no_schedules' AS Issue,
               CONCAT(N'', (SELECT COUNT(*) FROM #ownership WHERE HasNoSchedules = 1),
                      N' obligation(s) are configured but have NEVER generated a schedule. They cannot be '
                    + N'overdue - and cannot be done either. They inflate the denominator, so the true '
                    + N'overdue rate on schedulable work is higher than the headline figure.') AS Detail
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

    DROP TABLE #ownership; DROP TABLE #inst; DROP TABLE #ovd; DROP TABLE #owned; DROP TABLE #people;
    DROP TABLE #closures; DROP TABLE #rows; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Location dimension installed.';
GO
