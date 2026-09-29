/*===========================================================================
  RegTrack Insights - ROLLBACK of sql/v2 (RegTrack parity, 2026-09-29)
  Restores every object sql/v2 changed to its definition as deployed on UAT
  on 2026-09-29 before v2, and makes dictionary version 1 current again.
  The relaxed check constraint (CK_ISC_NoOverdueWhenClosed) is left in place:
  it is strictly weaker than the old one, and the old one cannot be re-added
  while the v2 dictionary rows exist. Version 2 rows are kept (inactive).
===========================================================================*/
SET NOCOUNT ON;
GO
UPDATE dbo.InsightsDictionaryVersion SET IsCurrent = CASE WHEN VersionId = 1 THEN 1 ELSE 0 END;
PRINT 'Dictionary v1 is current again.';
GO
-------------------------------------------------------------------------- tvfInsightsScopedInstances
IF OBJECT_ID('dbo.tvfInsightsScopedInstances', 'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsScopedInstances;
GO
CREATE FUNCTION dbo.tvfInsightsScopedInstances (@UserID INT, @CustomerID INT)
RETURNS TABLE
AS
RETURN
(
    SELECT
        i.ID                    AS ComplianceInstanceID,
        i.CustomerBranchID      AS BranchID,
        a.ComplianceCategoryId  AS CategoryId,
        i.ComplianceID,
        c.RiskType,
        c.Imprisonment,
        c.NatureOfCompliance,
        c.ComplianceType,
        a.ID                    AS ActID
    FROM ComplianceInstance i
    JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID
    JOIN Compliance     c  ON c.ID  = i.ComplianceID
    JOIN Act            a  ON a.ID  = c.ActID
    -- 2-D scope constraint: BOTH branch AND category must match
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp
         ON sp.BranchID   = i.CustomerBranchID
        AND sp.CategoryId = a.ComplianceCategoryId
    WHERE cb.CustomerID = @CustomerID
      AND cb.IsDeleted = 0 AND cb.Status = 1
      AND i.IsDeleted   = 0
      AND c.IsDeleted   = 0
);
GO
PRINT 'tvfInsightsScopedInstances restored (pre-v2).';
GO
-------------------------------------------------------------------------- tvfInsightsOverdueSchedules
IF OBJECT_ID('dbo.tvfInsightsOverdueSchedules', 'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsOverdueSchedules;
GO
CREATE FUNCTION dbo.tvfInsightsOverdueSchedules (@CustomerID INT, @AsOf DATETIME)
RETURNS TABLE
AS
RETURN
(
    /*  The canonical overdue predicate. Built on tvfInsightsLatestStatus - see
        the note above for why it no longer touches the view.

        A past-due schedule is overdue when EITHER:
          (a) its latest status is overdue-eligible per the dictionary, OR
          (b) it has NO transaction at all - BA RULING: "a past-due schedule
              with no transaction is to be considered overdue". Nobody has
              ever touched it; that is the purest form of overdue.

        Still FAIL-CLOSED on unknown: a schedule whose latest status is NOT in
        the dictionary is neither (a) nor (b) and is excluded, so a dictionary
        gap still surfaces through reconciliation rather than being guessed. */
    SELECT
        ls.ComplianceInstanceID,
        ls.ComplianceScheduleOnID,
        ls.CustomerBranchID,
        a.ComplianceCategoryId      AS CategoryId,
        ls.StatusId,                                     -- NULL for case (b)
        ls.ScheduleOn,
        CAST(CASE WHEN ls.LatestTransactionId IS NULL THEN 1 ELSE 0 END AS BIT) AS NeverTouched
    FROM dbo.tvfInsightsLatestStatus(@CustomerID, @AsOf) ls
    JOIN Compliance c ON c.ID = ls.ComplianceID          -- IsDeleted already applied upstream
    JOIN Act        a ON a.ID = c.ActID
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = ls.StatusId
    WHERE d.OverdueEligible = 1
       OR ls.LatestTransactionId IS NULL
);
GO
PRINT 'tvfInsightsOverdueSchedules restored (pre-v2).';
GO
-------------------------------------------------------------------------- tvfInsightsForwardPipelineSchedules
IF OBJECT_ID('dbo.tvfInsightsForwardPipelineSchedules', 'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsForwardPipelineSchedules;
GO
CREATE FUNCTION dbo.tvfInsightsForwardPipelineSchedules (@CustomerID INT, @AsOf DATETIME)
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
      AND cso.ScheduleOn >  @AsOf
      AND cso.ScheduleOn <= DATEADD(DAY, 90, @AsOf)
      AND (d.OverdueEligible = 1 OR lt.ID IS NULL)
);
GO
PRINT 'tvfInsightsForwardPipelineSchedules restored (pre-v2).';
GO
-------------------------------------------------------------------------- usp_Insights_Dimension_Location
IF OBJECT_ID('dbo.usp_Insights_Dimension_Location', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Location;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Location
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
      1. SCOPED INSTANCE BASE
    ===================================================================*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.CategoryId,
        c.Imprisonment,
        c.RiskType,
        c.Frequency          -- D-3: 97.8% of no-schedule instances have Frequency NULL
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
    JOIN Compliance c ON c.ID = s.ComplianceID;

    CREATE CLUSTERED INDEX IX_inst ON #inst (BranchID, ComplianceInstanceID);

    /*  [ADDED 2026-09-25] HARD WINDOW GATE - caller-supplied period, no FY default.
        Location's own population (ComplianceInstance via tvfInsightsScopedInstances) carries
        no per-row date - an instance persists across years while its real due dates
        live on ComplianceScheduleOn, one row per recurring occurrence (a single
        instance can have dozens). "Which instances are in scope for the selected
        period" is answered at the SCHEDULE level, same proven pattern as sql/23
        TimelinessFY and sql/11 Act: #inst is already materialised and indexed above,
        so find which of those instances had >=1 scheduled occurrence in the window,
        then narrow #inst itself down to just those. Every downstream step (overdue,
        ownership, per-branch rollup, reconciliation) already reads from #inst, so
        they inherit the window for free - nothing else in this file changes.
        Do NOT reintroduce a join hint or skip the materialise-first step - the same
        shape of query against ComplianceScheduleOn's 29.4M rows without it measured
        183,502 ms on a real tenant versus 157 ms scoped-first (sql/23's own numbers).
        @WindowStart/@WindowEnd are REQUIRED, always caller-resolved from a period-
        picker choice (last 30/60/90 days, or a quarter) - no fiscal-year default here,
        that convention stays specific to TimelinessFY alone.                          */
    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51032, N'LOCATION DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51032, N'LOCATION DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    IF OBJECT_ID('tempdb..#active') IS NOT NULL DROP TABLE #active;
    SELECT DISTINCT cso.ComplianceInstanceID
    INTO #active
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd;
    CREATE CLUSTERED INDEX IX_active ON #active (ComplianceInstanceID);

    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #active a WHERE a.ComplianceInstanceID = i.ComplianceInstanceID);

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
        (SELECT COUNT(*) FROM #ownership WHERE HasNoSchedules  = 1) AS InstancesWithNoSchedules,
        /*  D-3: two populations needing DIFFERENT action. Root cause established
            2026-09-08 - no frequency means the scheduler has nothing to generate
            from, so these are master data, not a job failure.                  */
        (SELECT COUNT(*) FROM #ownership o JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID
          WHERE o.HasNoSchedules = 1 AND i.Frequency IS NULL)     AS NoSchedules_NoFrequency,
        (SELECT COUNT(*) FROM #ownership o JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID
          WHERE o.HasNoSchedules = 1 AND i.Frequency IS NOT NULL) AS NoSchedules_HasFrequency;

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

    /*===================================================================
      7. TYPED ASSERTIONS  (comparatives COMPUTED here - spec Sec.6.10)
         Emission governed by #detector.EmitMode above.
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

    /*  [ADDED 2026-09-13] CONSEQUENCE, not rate. A-WORST ranks by OverduePct,
        which puts a 69-obligation area with ZERO imprisonment exposure above one
        with hundreds of personally-liable items overdue - observed on a live
        pilot report, where the top recommendation carried no liability at all.

        This assertion ranks the SAME rows by imprisonment-bearing overdue count.
        Both are emitted; neither replaces the other. The composition layer
        chooses, and can now choose on consequence.

        Emitted ONLY when there is real exposure to rank - an all-zero ranking
        would manufacture a "worst" that means nothing.                        */
    -- [FIX - found live 2026-09-25, tenant 1285's real ImprisonmentOverdue>0 data] The literal
    -- below was 205 chars against #assert.Caveat NVARCHAR(200) - a real "String or binary data
    -- would be truncated" THROW, only reachable via this same tenant/branch CLAUDE.md sec.5
    -- already documents as the one validated case that exercises this condition. Shortened to
    -- 173 chars, same meaning preserved. General lesson restated in CLAUDE.md sec.5 still applies:
    -- re-measure any literal in a fixed-width column before editing it again.
    IF EXISTS (SELECT 1 FROM #rows WHERE ImprisonmentOverdue > 0)
    INSERT #assert
    SELECT TOP 1 'A-WORST-EXPOSURE','imprisonment_overdue_count', BranchName, ImprisonmentOverdue, NULL,
           (SELECT SUM(ImprisonmentOverdue) FROM #rows),
           NULL, NULL, 'worse',
           N'ranked by CONSEQUENCE - count of overdue obligations carrying personal '
         + N'liability, not overdue rate. A higher rate on a smaller, liability-free '
         + N'portfolio is a lesser problem.'
    FROM #rows WHERE ImprisonmentOverdue > 0 ORDER BY ImprisonmentOverdue DESC, Overdue DESC;


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
        /*  [FIX - found live 2026-09-21] Was NVARCHAR(300) - the 2026-09-17 fix widened
            #assert.ScopeLabel to NVARCHAR(300) to match #rows.BranchName, but never checked the
            NEXT hop: every headline below is CONCAT(ScopeLabel, <30-70 chars of literal text>,
            Value, ...) - up to ~374 characters into a 300-wide column. Same bug class as the
            ScopeLabel fix, one hop further downstream. Confirmed live against Minda (real
            production data, prod-readonly replica) - "String or binary data would be truncated",
            reconciliation refused to publish. 500 = 300 (ScopeLabel max) + longest literal suffix
            (~70, F-PEERSTATE) + Value's string form + safety margin.                          */
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

    /*  [FIX] '[0-9]%' not '%'. 'A-SPOF-%' also matches 'A-SPOF-AGG', so the
        AGGREGATE assertion leaked into the INDIVIDUAL finding - producing a
        duplicate, and the nonsense headline "tenant depends on a single person
        for performance or review". Seen in live SSMS output 2026-09-08.
        sql/05 already used the correct pattern for A-GHOST; SPOF and OWN were
        missed.                                                                */
    INSERT #find
    SELECT 'F-SPOF','medium',
           CONCAT(N'', ScopeLabel, N' depends on a single person for performance or review'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-SPOF-[0-9]%';

    /*  [FIX] Two defects. (1) '[0-9]%' not '%' - see the SPOF note above.
        (2) The headline said "no assigned owner", which CONTRADICTS this
        procedure's own data_quality declaration: most of these DO have a
        performer named on each schedule, they lack an instance-level owner.
        Wording corrected and the guard attached.                              */
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
      9. DATA QUALITY - declared, never silent
    ===================================================================*/
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
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
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to obligations with a scheduled occurrence between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. An obligation with no occurrence in this window is excluded entirely, not just its '
                    + N'overdue figures - it will not appear in any row here even if it exists cumulatively.') AS Detail
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

    DROP TABLE #ownership; DROP TABLE #inst; DROP TABLE #active; DROP TABLE #ovd; DROP TABLE #owned; DROP TABLE #people;
    DROP TABLE #closures; DROP TABLE #rows; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Location restored (pre-v2).';
GO
-------------------------------------------------------------------------- usp_Insights_Dimension_Entity
IF OBJECT_ID('dbo.usp_Insights_Dimension_Entity', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Entity;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Entity
    @UserID      INT,
    @CustomerID  INT,
    @WindowStart DATETIME,
    @WindowEnd   DATETIME,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*=====================================================================
      0. PRE-FLIGHT - fail closed before computing anything
    =====================================================================*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51050, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- THROWs on dictionary gap

    /*=====================================================================
      1. SCOPED INSTANCE BASE - both scope axes
    =====================================================================*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.Imprisonment
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;

    CREATE CLUSTERED INDEX IX_inst ON #inst (BranchID, ComplianceInstanceID);

    /*  [ADDED 2026-09-25] HARD WINDOW GATE - caller-supplied period, no FY default.
        Entity's own population (ComplianceInstance via tvfInsightsScopedInstances) carries
        no per-row date - an instance persists across years while its real due dates
        live on ComplianceScheduleOn, one row per recurring occurrence (a single
        instance can have dozens). "Which instances are in scope for the selected
        period" is answered at the SCHEDULE level, same proven pattern as sql/23
        TimelinessFY and sql/11 Act: #inst is already materialised and indexed above,
        so find which of those instances had >=1 scheduled occurrence in the window,
        then narrow #inst itself down to just those. Every downstream step (#direct,
        the ancestor-descendant rollup, reconciliation) already reads from #inst, so
        they inherit the window for free - nothing else in this file changes.
        Do NOT reintroduce a join hint or skip the materialise-first step - the same
        shape of query against ComplianceScheduleOn's 29.4M rows without it measured
        183,502 ms on a real tenant versus 157 ms scoped-first (sql/23's own numbers).
        @WindowStart/@WindowEnd are REQUIRED, always caller-resolved from a period-
        picker choice (last 30/60/90 days, or a quarter) - no fiscal-year default here,
        that convention stays specific to TimelinessFY alone.                          */
    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51052, N'ENTITY DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51052, N'ENTITY DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    IF OBJECT_ID('tempdb..#active') IS NOT NULL DROP TABLE #active;
    SELECT DISTINCT cso.ComplianceInstanceID
    INTO #active
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd;
    CREATE CLUSTERED INDEX IX_active ON #active (ComplianceInstanceID);

    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #active a WHERE a.ComplianceInstanceID = i.ComplianceInstanceID);

    /*=====================================================================
      2. OVERDUE (dictionary-driven, affirmative form)
    =====================================================================*/
    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;   -- scope-constrained

    /*=====================================================================
      3. OWNERSHIP - an instance with no performer is ownerless
    =====================================================================*/
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

    /*=====================================================================
      4. DIRECT COUNTS PER NODE

      Built from the TREE (the member list), not from the instances, so a node
      holding nothing still produces a row. Every node appears - leaf AND
      intermediate.
    =====================================================================*/
    IF OBJECT_ID('tempdb..#direct') IS NOT NULL DROP TABLE #direct;
    SELECT
        t.BranchID,
        SUM(CASE WHEN i.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END) AS DirectInstances,
        SUM(CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END) AS DirectOverdue,
        SUM(CASE WHEN i.ComplianceInstanceID IS NOT NULL
                  AND w.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END)     AS DirectNoInstanceOwner,
        SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END)                 AS DirectImprisonment
    INTO #direct
    FROM dbo.tvfInsightsEntityTree(@CustomerID) t
    LEFT JOIN #inst  i ON i.BranchID = t.BranchID
    LEFT JOIN #ovd   o ON o.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN #owned w ON w.ComplianceInstanceID = i.ComplianceInstanceID
    GROUP BY t.BranchID;

    CREATE CLUSTERED INDEX IX_direct ON #direct (BranchID);

    /*=====================================================================
      5. ANCESTOR-DESCENDANT CLOSURE

      Each node paired with itself and every node beneath it, so a subtree
      total is a single GROUP BY rather than a per-node recursion. Anchored on
      the same apex-or-orphan tree, so an orphaned subtree rolls up under its
      own root instead of vanishing.
    =====================================================================*/
    IF OBJECT_ID('tempdb..#closure') IS NOT NULL DROP TABLE #closure;
    ;WITH closure AS (
        SELECT t.BranchID AS AncestorId, t.BranchID AS DescendantId
        FROM dbo.tvfInsightsEntityTree(@CustomerID) t
        UNION ALL
        SELECT c.AncestorId, cb.ID
        FROM closure c
        JOIN CustomerBranch cb ON cb.ParentID = c.DescendantId
        WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1
    )
    SELECT AncestorId, DescendantId
    INTO #closure
    FROM closure;

    CREATE CLUSTERED INDEX IX_closure ON #closure (AncestorId, DescendantId);

    /*=====================================================================
      6. PER-NODE ROWS

      [TRAP] Declare the table explicitly with ALL columns - base and derived.
      `SELECT ... INTO #rows` then `ALTER TABLE #rows ADD <col>` then
      referencing <col> in the same procedure body fails: T-SQL resolves names
      for the whole batch up front, and inside a procedure you cannot insert a
      GO to split the batch.
    =====================================================================*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        BranchID            INT            NOT NULL PRIMARY KEY,
        BranchName          NVARCHAR(300)  NULL,
        ParentID            INT            NULL,
        ApexId              INT            NULL,
        ApexName            NVARCHAR(400)  NULL,
        RootKind            VARCHAR(20)    NULL,   -- apex | orphan
        NodeType            VARCHAR(20)    NULL,   -- leaf | intermediate
        Depth               INT            NULL,
        DirectInstances     INT            NOT NULL,
        SubtreeInstances    INT            NOT NULL,
        SubtreeOverdue      INT            NOT NULL,
        SubtreeNoInstanceOwner    INT            NOT NULL,
        SubtreeImprisonment INT            NOT NULL,
        ActiveChildren      INT            NOT NULL,
        -- derived, populated below
        SubtreeOverduePct   DECIMAL(5,1)   NULL,
        ApexSharePct        DECIMAL(5,1)   NULL,
        SubtreeOverdueRank  INT            NULL,
        Flags               VARCHAR(200)   NULL
    );

    INSERT #rows (BranchID, BranchName, ParentID, ApexId, ApexName, RootKind, NodeType, Depth,
                  DirectInstances, SubtreeInstances, SubtreeOverdue, SubtreeNoInstanceOwner,
                  SubtreeImprisonment, ActiveChildren)
    SELECT
        t.BranchID, t.BranchName, t.ParentID, t.ApexId, t.ApexName, t.RootKind, t.NodeType, t.Depth,
        d.DirectInstances,
        0, 0, 0, 0,
        (SELECT COUNT(*) FROM CustomerBranch ch
          WHERE ch.ParentID = t.BranchID AND ch.IsDeleted = 0 AND ch.Status = 1)
    FROM dbo.tvfInsightsEntityTree(@CustomerID) t
    JOIN #direct d ON d.BranchID = t.BranchID;

    /*  Subtree rollups, one GROUP BY over the closure. */
    UPDATE r SET
        SubtreeInstances    = s.Inst,
        SubtreeOverdue      = s.Ovd,
        SubtreeNoInstanceOwner    = s.Own,
        SubtreeImprisonment = s.Imp
    FROM #rows r
    JOIN (
        SELECT c.AncestorId,
               SUM(d.DirectInstances)    AS Inst,
               SUM(d.DirectOverdue)      AS Ovd,
               SUM(d.DirectNoInstanceOwner)    AS Own,
               SUM(d.DirectImprisonment) AS Imp
        FROM #closure c
        JOIN #direct d ON d.BranchID = c.DescendantId
        GROUP BY c.AncestorId
    ) s ON s.AncestorId = r.BranchID;

    /*=====================================================================
      7. DERIVED COMPARATIVES - computed here, never phrased later
    =====================================================================*/
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #inst);

    UPDATE #rows SET
        SubtreeOverduePct = CASE WHEN SubtreeInstances = 0 THEN 0
                                 ELSE 100.0 * SubtreeOverdue / SubtreeInstances END,
        ApexSharePct      = CASE WHEN Depth <> 0 OR @scopedTotal = 0 THEN NULL
                                 ELSE 100.0 * SubtreeInstances / @scopedTotal END;

    /*  Rank apexes only, and only those material enough to be worth comparing.

        [TRAP] RANKING WITHOUT A MATERIALITY FLOOR YIELDS A TRUE BUT USELESS HERO.
        An apex holding one overdue obligation scores 100% and outranks one holding
        900 of 1,000 at 90%. The narrative contract then LICENSES the claim - rank 1
        exists, so "the highest overdue rate" passes the claim-checker, the publish
        gate and reflection - and the report leads on noise. Verified number, false
        story: pre-mortem D7.

        Rank only apexes at or above Detectors:MaterialityFloorInstances. If fewer
        than two qualify, fall back to every apex holding anything and DECLARE the
        fallback on the assertion. Same adaptive pattern as sql/05.               */
    DECLARE @materialityFloor INT = 50;                       -- Detectors:MaterialityFloorInstances
    DECLARE @materialApexes   INT = (SELECT COUNT(*) FROM #rows
                                     WHERE Depth = 0 AND SubtreeInstances >= @materialityFloor);
    DECLARE @rankDegraded     BIT = CASE WHEN @materialApexes < 2 THEN 1 ELSE 0 END;
    DECLARE @rankFloor        INT = CASE WHEN @rankDegraded = 1 THEN 1 ELSE @materialityFloor END;

    ;WITH r AS (SELECT BranchID, RANK() OVER (ORDER BY SubtreeOverduePct DESC) AS rk
                FROM #rows WHERE Depth = 0 AND SubtreeInstances >= @rankFloor)
    UPDATE #rows SET SubtreeOverdueRank = r.rk FROM #rows JOIN r ON r.BranchID = #rows.BranchID;

    /*=====================================================================
      8. TENANT SHAPE + COMPARISON GRAIN

      The same dominance rule as usp_Insights_TenantShape. If that rule is ever
      changed, change it in BOTH places or the entity dimension and the shape
      proc will disagree about the same tenant.
    =====================================================================*/
    DECLARE @apexCount INT = (SELECT COUNT(*) FROM #rows WHERE Depth = 0);
    DECLARE @maxApexShare DECIMAL(5,1) = (SELECT MAX(ApexSharePct) FROM #rows WHERE Depth = 0);
    DECLARE @childlessShell BIT =
        CASE WHEN EXISTS (SELECT 1 FROM #rows
                          WHERE Depth = 0 AND ActiveChildren = 0 AND SubtreeInstances = 0)
             THEN 1 ELSE 0 END;

    DECLARE @tenantShape VARCHAR(20) =
        CASE WHEN @apexCount = 1 THEN 'single_entity' ELSE 'multi_entity' END;

    DECLARE @comparisonGrain VARCHAR(20) =
        CASE WHEN @apexCount = 1                     THEN 'locations'
             WHEN ISNULL(@maxApexShare,0) >= 70.0    THEN 'descend_one_level'
             WHEN @childlessShell = 1                THEN 'descend_one_level'
             ELSE 'apex' END;

    DECLARE @grainReason NVARCHAR(300) =
        CASE WHEN @apexCount = 1                     THEN N'Single apex - skip entity comparison, lead with locations.'
             WHEN ISNULL(@maxApexShare,0) >= 70.0    THEN CONCAT(N'Largest apex holds ', @maxApexShare, N'% - descend one level for a meaningful comparison.')
             WHEN @childlessShell = 1                THEN N'A childless holding shell is present - descend one level.'
             ELSE N'Apex entities are balanced - compare at apex level.' END;

    /*=====================================================================
      9. DETECTIONS

      [TRAP] ZERO OBLIGATIONS MEANS CANNOT ASSESS, NOT A VERDICT.
      Without @hasAnyObligations, a tenant with no obligations in scope has
      every apex at SubtreeInstances = 0, so childless_holding_shell fires on
      ALL of them - 100% of the eligible set - and the emission policy turns
      that into an aggregate finding stating a structural conclusion drawn from
      no data at all. Suppress the shell flag entirely when there is nothing to
      assess, exactly as sql/05 suppresses its onboarding verdict.

      dominant_apex needs no ZERO-OBLIGATION guard: ApexSharePct is NULL when the
      scoped total is zero, and NULL >= 70.0 is UNKNOWN, so it cannot fire. It does
      carry a SINGLE-APEX guard, for a separate reason - see the trap note on it
      below.
    =====================================================================*/
    DECLARE @hasAnyObligations BIT =
        CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;

    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN RootKind = 'orphan' THEN ',orphaned_parent_deleted' ELSE '' END +
            CASE WHEN NodeType = 'intermediate' AND DirectInstances > 0
                 THEN ',instances_on_intermediate_node' ELSE '' END +
            /*  [TRAP] DOMINANCE IS VACUOUS WITH ONE APEX.
                A sole apex holds 100% of the estate by definition, so without the
                @apexCount >= 2 guard this fires on every single-entity tenant and
                emits "1 of 1 apex entities (100%) hold a dominant share" - true,
                meaningless, and the same defect as a rank of 1 of 1, which this
                project bans everywhere. It is also redundant: ComparisonGrain is
                already 'locations' for a single apex, so the descend-one-level
                advice this detector exists to give has already been given.        */
            CASE WHEN @apexCount >= 2 AND Depth = 0 AND ApexSharePct >= 70.0
                 THEN ',dominant_apex' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1
                  AND Depth = 0 AND ActiveChildren = 0 AND SubtreeInstances = 0
                 THEN ',childless_holding_shell' ELSE '' END
        , 1, 1, '');

    /*=====================================================================
      10. CONTROL TOTALS + MANDATORY RECONCILIATION

      On DirectInstances. See the header - SubtreeInstances double-counts by
      construction and must never be reconciled against the control total.
    =====================================================================*/
    DECLARE @rowSum INT = (SELECT ISNULL(SUM(DirectInstances),0) FROM #rows);

    IF @rowSum <> @scopedTotal
        THROW 51051, N'ENTITY DIMENSION RECONCILIATION FAILED - per-node direct sums do not tie to the scoped instance total. A node was dropped. Refusing to publish.', 1;

    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT COUNT(*) FROM #ovd) / @scopedTotal END;

    SELECT
        'control_totals'                   AS ResultSet,
        @scopedTotal                       AS ScopedInstances,
        @rowSum                            AS SumOfRows,
        CAST(1 AS BIT)                     AS Reconciled,
        (SELECT COUNT(*) FROM #ovd)        AS OverdueInstances,
        @tenantOverduePct                  AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows)       AS NodesReported,
        (SELECT COUNT(*) FROM CustomerBranch
          WHERE CustomerID = @CustomerID AND IsDeleted = 0 AND Status = 1) AS ActiveBranchesInTenant,
        @apexCount                         AS ApexEntityCount,
        @tenantShape                       AS TenantShape,
        ISNULL(@maxApexShare,0)            AS LargestApexSharePct,
        @comparisonGrain                   AS ComparisonGrain,
        @grainReason                       AS GrainReason;

    /*=====================================================================
      11. ROWS
    =====================================================================*/
    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY SubtreeInstances DESC, Depth;

    /*=====================================================================
      12. DETECTOR EMISSION POLICY

      Eligible and Flagged MUST be drawn from the same population, or the rate
      can exceed 100%. Each detector below states its own eligible set:
        orphaned_parent_deleted        - every node
        instances_on_intermediate_node - nodes holding instances directly
        dominant_apex                  - apex nodes
        childless_holding_shell        - apex nodes
    =====================================================================*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector      VARCHAR(40) PRIMARY KEY,
        Eligible      INT,
        Flagged       INT,
        FlaggedPct    DECIMAL(5,1),
        EmitMode      VARCHAR(12)          -- 'individual' | 'aggregate' | 'none'
    );

    DECLARE @allNodes   INT = (SELECT COUNT(*) FROM #rows);
    DECLARE @withDirect INT = (SELECT COUNT(*) FROM #rows WHERE DirectInstances > 0);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'orphaned_parent_deleted', @allNodes,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%orphaned_parent_deleted%')
    UNION ALL SELECT 'instances_on_intermediate_node', @withDirect,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%instances_on_intermediate_node%')
    UNION ALL SELECT 'dominant_apex', @apexCount,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%dominant_apex%')
    UNION ALL SELECT 'childless_holding_shell', @apexCount,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%childless_holding_shell%');

    UPDATE #detector
       SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;

    /*  [TRAP] AGGREGATING A SMALL MEMBER SET DESTROYS THE FINDING.
        The aggregate mode exists to stop a detector emitting 117 findings on an
        819-branch tenant. Individual findings are already capped at the top 5 by
        materiality, so when the eligible set is 5 or fewer the cap ALREADY bounds
        the output and aggregating cannot reduce it - it only replaces named members
        with a percentage. Measured on the Risk dimension, whose grain is fixed at
        four levels: 3 of 4 tipped to 'aggregate' and the finding became "3 of 4 risk
        levels (75%)", losing the tier names that ARE the finding the spec requires
        ("the coverage gap hides in the MIDDLE tiers - High 31 + Medium 43 against
        Critical's 9"). Below the cap, always name the members.                     */
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

    /*=====================================================================
      13. TYPED ASSERTIONS - comparatives COMPUTED here
    =====================================================================*/
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

    -- A-TENANT: the baseline every comparative is measured against
    INSERT #assert VALUES ('A-TENANT','overdue_pct',N'tenant',@tenantOverduePct,NULL,NULL,NULL,NULL,NULL,NULL);

    /*  [TRAP] A "worst entity" claim needs something to compare against.
        Rank 1 of 1 is vacuous and reads as criticism of the only entity they
        have. Require >= 2 rankable apexes before emitting any comparative. */
    DECLARE @rankableApex INT = (SELECT COUNT(*) FROM #rows
                                 WHERE Depth = 0 AND SubtreeInstances >= @rankFloor);

    /*  Ties are the other half of the problem. RANK() gives every apex at the same
        rate rank 1, so "the highest" can be true of several at once. The caveat
        travels WITH the value - the prompt contract forbids citing a value without
        its caveat - so the narrative cannot imply uniqueness.                     */
    DECLARE @tiedAtTop INT = (SELECT COUNT(*) FROM #rows
                              WHERE Depth = 0 AND SubtreeInstances >= @rankFloor AND SubtreeOverdueRank = 1);

    IF @rankableApex >= 2
    INSERT #assert
    SELECT TOP 1 'A-APEX-WORST','subtree_overdue_pct',ApexName,SubtreeOverduePct,
           SubtreeOverdueRank,@rankableApex,
           @tenantOverduePct, SubtreeOverduePct - @tenantOverduePct,
           CASE WHEN SubtreeOverduePct > @tenantOverduePct THEN 'worse' ELSE 'better' END,
           NULLIF(CONCAT(
               CASE WHEN @rankDegraded = 1
                    THEN CONCAT(N'degraded_ranking_sample: no apex reaches the ', @materialityFloor,
                                N'-instance materiality floor, so ranking is across all apexes holding obligations. ')
                    ELSE N'' END,
               CASE WHEN @tiedAtTop > 1
                    THEN CONCAT(N'tied_at_top: ', @tiedAtTop, N' apexes share this rate - it is not uniquely the highest. ')
                    ELSE N'' END), N'')
    FROM #rows
    WHERE Depth = 0 AND SubtreeInstances >= @rankFloor
    ORDER BY SubtreeOverduePct DESC, SubtreeInstances DESC;

    /*  Apex share of estate (largest apex). Suppressed when there is only one
        apex - a share of 100% of one entity is vacuous - and when there is
        nothing in scope, where ApexSharePct is NULL and an assertion carrying
        no number must never be emitted. */
    IF @apexCount >= 2 AND @hasAnyObligations = 1
    INSERT #assert
    SELECT TOP 1 'A-APEX-SHARE','apex_share_of_estate',ApexName,ApexSharePct,
           NULL,@apexCount,NULL,NULL,NULL,NULL
    FROM #rows
    WHERE Depth = 0 AND ApexSharePct IS NOT NULL
    ORDER BY ApexSharePct DESC, SubtreeInstances DESC;

    -- dominant apex (policy-gated)
    IF (SELECT EmitMode FROM #detector WHERE Detector='dominant_apex') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-DOM-' + CAST(ROW_NUMBER() OVER (ORDER BY SubtreeInstances DESC) AS VARCHAR(5)),
               'apex_share_of_estate', ApexName, ApexSharePct, NULL, @apexCount,
               NULL, NULL, NULL,
               N'dominant_apex: apex-level comparison is not meaningful at this share'
        FROM #rows WHERE Flags LIKE '%dominant_apex%' ORDER BY SubtreeInstances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='dominant_apex') = 'aggregate'
        INSERT #assert
        SELECT 'A-DOM-AGG','apexes_dominant',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL,
               N'aggregate - concentration is a tenant-wide characteristic'
        FROM #detector WHERE Detector='dominant_apex';

    -- orphaned subtrees (policy-gated, top 5 by materiality)
    IF (SELECT EmitMode FROM #detector WHERE Detector='orphaned_parent_deleted') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-ORPH-' + CAST(ROW_NUMBER() OVER (ORDER BY SubtreeInstances DESC) AS VARCHAR(5)),
               'subtree_instances', BranchName, SubtreeInstances, NULL, NULL,
               NULL, NULL, NULL,
               N'orphaned_parent_deleted: parent entity was deleted, reported as top-level'
        FROM #rows WHERE Flags LIKE '%orphaned_parent_deleted%' ORDER BY SubtreeInstances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='orphaned_parent_deleted') = 'aggregate'
        INSERT #assert
        SELECT 'A-ORPH-AGG','nodes_orphaned',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL,
               N'aggregate - a deleted-parent hierarchy is a tenant-wide data-quality pattern'
        FROM #detector WHERE Detector='orphaned_parent_deleted';

    -- instance-bearing intermediate nodes (would vanish from a leaf-only rollup)
    IF (SELECT EmitMode FROM #detector WHERE Detector='instances_on_intermediate_node') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-INT-' + CAST(ROW_NUMBER() OVER (ORDER BY DirectInstances DESC) AS VARCHAR(5)),
               'instances_on_intermediate_node', BranchName, DirectInstances, NULL, NULL,
               NULL, NULL, NULL,
               N'held directly on a non-leaf node'
        FROM #rows WHERE Flags LIKE '%instances_on_intermediate_node%' ORDER BY DirectInstances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='instances_on_intermediate_node') = 'aggregate'
        INSERT #assert
        SELECT 'A-INT-AGG','nodes_with_instances_on_intermediate',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL,
               N'aggregate - holding obligations on grouping nodes is how this tenant is configured'
        FROM #detector WHERE Detector='instances_on_intermediate_node';

    -- childless holding shells
    IF (SELECT EmitMode FROM #detector WHERE Detector='childless_holding_shell') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-SHELL-' + CAST(ROW_NUMBER() OVER (ORDER BY BranchName) AS VARCHAR(5)),
               'subtree_instances', BranchName, 0, NULL, NULL, NULL, NULL, NULL,
               N'childless_holding_shell: apex with no children and no obligations'
        FROM #rows WHERE Flags LIKE '%childless_holding_shell%';
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='childless_holding_shell') = 'aggregate'
        INSERT #assert
        SELECT 'A-SHELL-AGG','apexes_childless_shell',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL,
               N'aggregate - empty top-level entities are a structural pattern for this tenant'
        FROM #detector WHERE Detector='childless_holding_shell';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*=====================================================================
      14. FINDINGS - every one backed by assertion ids

      [TRAP] Match the NUMBERED assertions only. 'A-ORPH-%' would also match
      'A-ORPH-AGG', emitting an individual-shaped finding about a scope label of
      'tenant' alongside the aggregate. Anchor on [0-9].
    =====================================================================*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (
        FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400),
        NarrativeGuard NVARCHAR(500) NULL
    );

    INSERT #find
    SELECT 'F-APEX-WORST','high',
           CONCAT(N'', ScopeLabel, N' has the highest overdue rate at ', Value, N'%'),
           'A-APEX-WORST,A-TENANT', NULL
    FROM #assert WHERE AssertionId = 'A-APEX-WORST' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-DOM','medium',
           CONCAT(N'', ScopeLabel, N' holds ', Value, N'% of the estate'),
           AssertionId,
           N'Apex-level comparison is not meaningful at this concentration. Compare one level down.'
    FROM #assert WHERE AssertionId LIKE 'A-DOM-[0-9]%';

    INSERT #find
    SELECT 'F-DOM-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' apex entities (', VsComparatorPP,
                  N'%) hold a dominant share of the estate'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-DOM-AGG';

    INSERT #find
    SELECT 'F-ORPH','medium',
           CONCAT(N'', ScopeLabel, N''' s parent entity was deleted; it is reported as top-level'),
           AssertionId,
           N'Do not silently re-parent. Verify the hierarchy with the tenant.'
    FROM #assert WHERE AssertionId LIKE 'A-ORPH-[0-9]%';

    INSERT #find
    SELECT 'F-ORPH-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' nodes (', VsComparatorPP,
                  N'%) sit under a deleted parent'),
           AssertionId,
           N'Do not silently re-parent. Verify the hierarchy with the tenant.'
    FROM #assert WHERE AssertionId = 'A-ORPH-AGG';

    INSERT #find
    SELECT 'F-INT','info',
           CONCAT(N'', ScopeLabel, N' holds ', CAST(Value AS INT), N' obligation(s) directly on a grouping node'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-INT-[0-9]%';

    INSERT #find
    SELECT 'F-INT-AGG','info',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' nodes (', VsComparatorPP,
                  N'%) hold obligations directly on a non-leaf node'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-INT-AGG';

    INSERT #find
    SELECT 'F-SHELL','info',
           CONCAT(N'', ScopeLabel, N' is a top-level entity with no children and no obligations'),
           AssertionId,
           N'A configuration gap, not a clean record. Do not present as compliant.'
    FROM #assert WHERE AssertionId LIKE 'A-SHELL-[0-9]%';

    INSERT #find
    SELECT 'F-SHELL-AGG','info',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' apex entities (', VsComparatorPP,
                  N'%) have no children and no obligations'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-SHELL-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*=====================================================================
      15. DATA QUALITY - declared, never silent
    =====================================================================*/
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'ownership_has_two_mechanisms'   THEN 'NoInstanceOwner'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'orphaned_entities'                    THEN 'RootKind'
                   WHEN 'instances_on_intermediate_nodes'      THEN 'NodeType'
                   WHEN 'dominant_apex_comparison_grain'       THEN 'ComparisonGrain'
                   WHEN 'share_is_scope_relative'              THEN 'LargestApexSharePct'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to obligations with a scheduled occurrence between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. An obligation with no occurrence in this window is excluded entirely, not just its '
                    + N'overdue figures - it will not appear in any node''s direct count here even if it '
                    + N'exists cumulatively.') AS Detail
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
        SELECT 'orphaned_entities',
               CONCAT(N'', COUNT(*), N' node(s) sit under a deleted parent and are reported as top-level.')
        FROM #rows WHERE RootKind = 'orphan' HAVING COUNT(*) > 0
        UNION ALL
        SELECT 'instances_on_intermediate_nodes',
               CONCAT(N'', SUM(DirectInstances), N' instance(s) are held directly on non-leaf nodes and are included in the rollup.')
        FROM #rows WHERE NodeType = 'intermediate' HAVING SUM(DirectInstances) > 0
        UNION ALL
        SELECT 'dominant_apex_comparison_grain',
               CONCAT(N'Largest apex holds ', ISNULL(@maxApexShare,0),
                      N'% of the estate. Apex-level comparison is not meaningful; comparison grain is ',
                      @comparisonGrain, N'.')
        WHERE @comparisonGrain = 'descend_one_level'
        UNION ALL
        /*  ApexSharePct divides by the SCOPED total, not the tenant total, because a
            dimension may only report what the caller is authorised to see. That makes
            it differ from usp_Insights_TenantShape, which is unscoped: tenant 5 reads
            16.6% here and 21.78% there, both correct. Declare it, or a reader takes a
            scope-relative share for a tenant-wide one.                              */
        SELECT 'share_is_scope_relative',
               CONCAT(N'Apex shares are computed against the ', @scopedTotal,
                      N' obligation(s) in this user''s authorised scope, not the tenant total. '
                    + N'They are not comparable with an unscoped tenant-wide figure.')
        WHERE @hasAnyObligations = 1
    ) q;

    DROP TABLE #inst; DROP TABLE #active; DROP TABLE #ovd; DROP TABLE #owned; DROP TABLE #direct;
    DROP TABLE #closure; DROP TABLE #rows; DROP TABLE #detector;
    DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Entity restored (pre-v2).';
GO
-------------------------------------------------------------------------- usp_Insights_Dimension_Risk
IF OBJECT_ID('dbo.usp_Insights_Dimension_Risk', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Risk;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Risk
    @UserID      INT,
    @CustomerID  INT,
    @WindowStart DATETIME,
    @WindowEnd   DATETIME,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*=====================================================================
      0. PRE-FLIGHT - fail closed before computing anything
    =====================================================================*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51060, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- THROWs on dictionary gap

    /*  Every risk value comes from the dictionary. A literal here would be an
        enum literal in a WHERE clause - non-negotiable #4 - and would make this
        proc agree with a wrong seed by construction. */
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

    /*=====================================================================
      1. SCOPED INSTANCE BASE - both scope axes
    =====================================================================*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.RiskType,
        s.Imprisonment
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;

    CREATE CLUSTERED INDEX IX_inst ON #inst (RiskType, ComplianceInstanceID);

    /*  [ADDED 2026-09-25] HARD WINDOW GATE - caller-supplied period, no FY default.
        Risk's own population (ComplianceInstance via tvfInsightsScopedInstances) carries
        no per-row date - an instance persists across years while its real due dates
        live on ComplianceScheduleOn, one row per recurring occurrence (a single
        instance can have dozens). "Which instances are in scope for the selected
        period" is answered at the SCHEDULE level, same proven pattern as sql/23
        TimelinessFY and sql/11 Act: #inst is already materialised and indexed above,
        so find which of those instances had >=1 scheduled occurrence in the window,
        then narrow #inst itself down to just those. Every downstream step (per-level
        rollup, reconciliation) already reads from #inst, so they inherit the window
        for free - nothing else in this file changes.
        Do NOT reintroduce a join hint or skip the materialise-first step - the same
        shape of query against ComplianceScheduleOn's 29.4M rows without it measured
        183,502 ms on a real tenant versus 157 ms scoped-first (sql/23's own numbers).
        @WindowStart/@WindowEnd are REQUIRED, always caller-resolved from a period-
        picker choice (last 30/60/90 days, or a quarter) - no fiscal-year default here,
        that convention stays specific to TimelinessFY alone.                          */
    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51062, N'RISK DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51062, N'RISK DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    IF OBJECT_ID('tempdb..#active') IS NOT NULL DROP TABLE #active;
    SELECT DISTINCT cso.ComplianceInstanceID
    INTO #active
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd;
    CREATE CLUSTERED INDEX IX_active ON #active (ComplianceInstanceID);

    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #active a WHERE a.ComplianceInstanceID = i.ComplianceInstanceID);

    /*=====================================================================
      2. OVERDUE (dictionary-driven, affirmative form)
    =====================================================================*/
    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;   -- scope-constrained

    /*=====================================================================
      3. OWNERSHIP - an instance with no performer is ownerless
    =====================================================================*/
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

    /*=====================================================================
      4. PER-LEVEL ROWS - from the dictionary member list, not the facts

      [TRAP] Declare the table explicitly with ALL columns, base and derived.
      SELECT ... INTO then ALTER TABLE ADD then referencing the new column in
      the same procedure body fails: T-SQL resolves names for the whole batch
      up front and a procedure cannot contain a GO.
    =====================================================================*/
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
        -- derived
        VsTenantPP            DECIMAL(9,2)   NULL,
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (RiskType, RiskLabel, Instances, Overdue, NoInstanceOwner,
                  ImprisonmentInstances, ImprisonmentOverdue, BranchesCovered)
    SELECT
        r.RiskType,
        r.RiskLabel,
        COUNT(i.ComplianceInstanceID),
        SUM(CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.ComplianceInstanceID IS NOT NULL
                  AND w.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 AND o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        COUNT(DISTINCT i.BranchID)
    FROM #risk r
    LEFT JOIN #inst  i ON i.RiskType = r.RiskType
    LEFT JOIN #ovd   o ON o.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN #owned w ON w.ComplianceInstanceID = i.ComplianceInstanceID
    GROUP BY r.RiskType, r.RiskLabel;

    /*=====================================================================
      5. CONTROL TOTALS + MANDATORY RECONCILIATION

      An instance whose RiskType is absent from the dictionary joins no row, so
      the per-level sum falls short and this THROWs. That is the intended
      behaviour - an unknown enum must never be silently bucketed.
    =====================================================================*/
    DECLARE @rowSum      INT = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #inst);

    IF @rowSum <> @scopedTotal
        THROW 51061, N'RISK DIMENSION RECONCILIATION FAILED - per-level sums do not tie to the scoped instance total. An instance carries a RiskType absent from InsightsEnumPolarity. Refusing to publish.', 1;

    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT COUNT(*) FROM #ovd) / @scopedTotal END;

    DECLARE @hasAnyObligations BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;

    UPDATE #rows SET
        OverduePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END;

    UPDATE #rows SET
        VsTenantPP = CASE WHEN Instances = 0 THEN NULL ELSE OverduePct - @tenantOverduePct END;

    /*  The imprisonment overlap. The spec measured 1,419 of 1,424 = 99.6% of
        imprisonment-bearing instances sitting on the Critical tier, which is why
        Critical and imprisonment must not be narrated as two separate findings. */
    DECLARE @impTotal INT = (SELECT COUNT(*) FROM #inst WHERE Imprisonment = 1);
    DECLARE @impOnCritical INT = (SELECT COUNT(*) FROM #inst WHERE Imprisonment = 1 AND RiskType = @criticalRisk);
    DECLARE @impOverlapPct DECIMAL(5,1) =
        CASE WHEN @impTotal = 0 THEN NULL ELSE 100.0 * @impOnCritical / @impTotal END;

    SELECT
        'control_totals'                  AS ResultSet,
        @scopedTotal                      AS ScopedInstances,
        @rowSum                           AS SumOfRows,
        CAST(1 AS BIT)                    AS Reconciled,
        (SELECT COUNT(*) FROM #ovd)       AS OverdueInstances,
        @tenantOverduePct                 AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows)      AS RiskLevelsReported,
        (SELECT COUNT(*) FROM #rows WHERE Instances > 0) AS RiskLevelsWithObligations,
        @criticalRisk                     AS CriticalRiskType,
        @impTotal                         AS ImprisonmentInstances,
        @impOverlapPct                    AS ImprisonmentOnCriticalPct;

    /*=====================================================================
      6. ROWS
    =====================================================================*/
    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*=====================================================================
      7. DETECTIONS

      The spec names no detector list for this dimension. The one detection it
      DOES require is finding 2 - the coverage gap hiding in the middle tiers -
      so that is what is detected: a non-Critical tier carrying MORE ownerless
      obligations than the Critical tier does. Ownership is not following
      severity.

      Guarded on @hasAnyObligations: with nothing in scope every tier holds
      zero, "more than Critical" is 0 > 0 which is false, but the guard makes
      the intent explicit rather than relying on that.
    =====================================================================*/
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

    /*=====================================================================
      8. DETECTOR EMISSION POLICY

      Eligible and Flagged are drawn from the same population - tiers holding
      obligations - so the rate cannot exceed 100%.

      Note this dimension has at most four members, so flooding is structurally
      impossible and the 20% aggregate threshold will almost always tip to
      'aggregate' (1 of 4 = 25%). That is harmless here: with four members an
      aggregate statement and four individual ones carry the same information.
    =====================================================================*/
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

    /*  [TRAP] AGGREGATING A SMALL MEMBER SET DESTROYS THE FINDING.
        The aggregate mode exists to stop a detector emitting 117 findings on an
        819-branch tenant. Individual findings are already capped at the top 5 by
        materiality, so when the eligible set is 5 or fewer the cap ALREADY bounds
        the output and aggregating cannot reduce it - it only replaces named members
        with a percentage. Measured on the Risk dimension, whose grain is fixed at
        four levels: 3 of 4 tipped to 'aggregate' and the finding became "3 of 4 risk
        levels (75%)", losing the tier names that ARE the finding the spec requires
        ("the coverage gap hides in the MIDDLE tiers - High 31 + Medium 43 against
        Critical's 9"). Below the cap, always name the members.                     */
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

    /*=====================================================================
      9. TYPED ASSERTIONS - comparatives COMPUTED here
    =====================================================================*/
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

    /*  FINDING 1. The direction is the whole point. On the reference tenant
        Critical ran 20.8% against a 29% average - BETTER than average, because
        the organisation triages correctly. A narrator given only the volume
        would report the largest tier as the biggest problem, which inverts the
        finding. The comparative is computed here so it cannot be vibed.        */
    IF @hasAnyObligations = 1
    INSERT #assert
    SELECT 'A-CRIT','overdue_pct',RiskLabel,OverduePct,NULL,NULL,
           @tenantOverduePct, VsTenantPP,
           CASE WHEN OverduePct > @tenantOverduePct THEN 'worse' ELSE 'better' END,
           NULL
    FROM #rows WHERE RiskType = @criticalRisk AND Instances > 0;

    /*  [ADDED 2026-09-13] CONSEQUENCE, not rate. See the note in sql/05.
        Emitted only where there is real exposure to rank.                     */
    IF EXISTS (SELECT 1 FROM #rows WHERE ImprisonmentOverdue > 0)
    INSERT #assert
    SELECT TOP 1 'A-WORST-RISK-EXP','imprisonment_overdue_count', RiskLabel, ImprisonmentOverdue, NULL,
           (SELECT SUM(ImprisonmentOverdue) FROM #rows), NULL, NULL, 'worse',
           N'ranked by CONSEQUENCE - overdue obligations carrying personal liability - not by rate.'
    FROM #rows WHERE ImprisonmentOverdue > 0 ORDER BY ImprisonmentOverdue DESC, Overdue DESC;


    /*  THE TRAP, as an assertion. Critical and imprisonment are ~95% the same
        population; this states the overlap so a narrator can see it rather than
        treating the two as independent axes.                                   */
    IF @impTotal > 0
    INSERT #assert
    VALUES ('A-IMP-OVERLAP','imprisonment_on_critical_pct',N'tenant',@impOverlapPct,
            NULL,@impTotal,NULL,NULL,NULL,
            N'critical_and_imprisonment_overlap: these are largely the SAME obligations, not two independent exposures');

    /*  FINDING 2. Ownership not following severity. Policy-gated. */
    IF (SELECT EmitMode FROM #detector WHERE Detector='ownership_gap_below_critical') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-OWNGAP-' + CAST(ROW_NUMBER() OVER (ORDER BY NoInstanceOwner DESC) AS VARCHAR(5)),
               'ownerless', RiskLabel, NoInstanceOwner, NULL, NULL,
               @criticalNoInstanceOwner, NoInstanceOwner - @criticalNoInstanceOwner, 'worse',
               N'ownership_gap_below_critical: attention follows severity, ownership does not'
        FROM #rows WHERE Flags LIKE '%ownership_gap_below_critical%' ORDER BY NoInstanceOwner DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='ownership_gap_below_critical') = 'aggregate'
        /*  ComparatorValue stays NULL. Value here is a COUNT OF RISK LEVELS; putting
            Critical's ownerless COUNT beside it puts two different units in one
            assertion, and a narrator reading 3 against 3 could write "equal to the
            Critical tier", which is meaningless. Aggregates in sql/05 leave it NULL
            for the same reason.                                                    */
        INSERT #assert
        SELECT 'A-OWNGAP-AGG','risk_levels_with_ownership_gap',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - unassigned ownership sits below the Critical tier across several levels'
        FROM #detector WHERE Detector='ownership_gap_below_critical';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*=====================================================================
      10. FINDINGS - every one backed by assertion ids

      [TRAP] Anchor individual findings on [0-9] so the aggregate assertion
      cannot also produce an individual-shaped finding.
    =====================================================================*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (
        FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400),
        NarrativeGuard NVARCHAR(500) NULL
    );

    /*  Critical BETTER than average - the counter-intuitive case. The guard is
        load-bearing: without it a narrator reports the largest, scariest-sounding
        tier as the problem. */
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

    /*=====================================================================
      11. DATA QUALITY - declared, never silent
    =====================================================================*/
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'ownership_has_two_mechanisms'   THEN 'NoInstanceOwner'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'critical_imprisonment_overlap'        THEN 'ImprisonmentOnCriticalPct'
                   WHEN 'risk_levels_unused'                   THEN 'RiskLevelsWithObligations'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to obligations with a scheduled occurrence between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. An obligation with no occurrence in this window is excluded entirely, not just its '
                    + N'overdue figures - it will not appear in any row here even if it exists cumulatively.') AS Detail
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

    DROP TABLE #risk; DROP TABLE #inst; DROP TABLE #active; DROP TABLE #ovd; DROP TABLE #owned;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Risk restored (pre-v2).';
GO
-------------------------------------------------------------------------- usp_Insights_Dimension_Nature
IF OBJECT_ID('dbo.usp_Insights_Dimension_Nature', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Nature;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Nature
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
        THROW 51070, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    DECLARE @criticalRisk INT = (
        SELECT TRY_CAST(p.RawValue AS INT)
        FROM dbo.InsightsEnumPolarity p
        JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
        WHERE p.Semantic = 'RiskType' AND p.Meaning LIKE N'Critical%');

    IF @criticalRisk IS NULL
        THROW 51075, N'DICTIONARY GAP - no RiskType value is mapped to Critical in InsightsEnumPolarity. Refusing to compute.', 1;

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

    /*  [ADDED 2026-09-25] HARD WINDOW GATE - caller-supplied period, no FY default.
        Nature's own population (ComplianceInstance via tvfInsightsScopedInstances) carries
        no per-row date - an instance persists across years while its real due dates
        live on ComplianceScheduleOn, one row per recurring occurrence (a single
        instance can have dozens). "Which instances are in scope for the selected
        period" is answered at the SCHEDULE level, same proven pattern as sql/23
        TimelinessFY and sql/11 Act: #inst is already materialised and indexed above,
        so find which of those instances had >=1 scheduled occurrence in the window,
        then narrow #inst itself down to just those. Every downstream step (per-nature
        rollup, reconciliation) already reads from #inst, so they inherit the window
        for free - nothing else in this file changes.
        Do NOT reintroduce a join hint or skip the materialise-first step - the same
        shape of query against ComplianceScheduleOn's 29.4M rows without it measured
        183,502 ms on a real tenant versus 157 ms scoped-first (sql/23's own numbers).
        @WindowStart/@WindowEnd are REQUIRED, always caller-resolved from a period-
        picker choice (last 30/60/90 days, or a quarter) - no fiscal-year default here,
        that convention stays specific to TimelinessFY alone.                          */
    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51073, N'NATURE DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51073, N'NATURE DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    IF OBJECT_ID('tempdb..#active') IS NOT NULL DROP TABLE #active;
    SELECT DISTINCT cso.ComplianceInstanceID
    INTO #active
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd;
    CREATE CLUSTERED INDEX IX_active ON #active (ComplianceInstanceID);

    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #active a WHERE a.ComplianceInstanceID = i.ComplianceInstanceID);

    /*-- 2. OVERDUE ------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;

    /*-- 3. OWNERSHIP ----------------------------------------------------*/
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
        THROW 51076, N'MASTER DATA GAP - NatureOfCompliance master is empty. Refusing to compute a nature dimension.', 1;

    /*-- 5. ROWS ---------------------------------------------------------*/
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
        -- the nature x penalty-type cross-tab
        FinancialPenaltyInstances INT           NOT NULL,
        ClosureRiskInstances     INT            NOT NULL,
        -- derived
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
        THROW 51072, N'NATURE DIMENSION RECONCILIATION FAILED - per-nature sums plus the untagged bucket do not tie to the scoped instance total. Refusing to publish.', 1;

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

    /*  [ADDED 2026-09-13] CONSEQUENCE, not rate. See the note in sql/05.
        Emitted only where there is real exposure to rank.                     */
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
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'ownership_has_two_mechanisms'   THEN 'NoInstanceOwner'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'nature_uncategorised_gap'             THEN 'UncategorisedPct'
                   WHEN 'nature_untagged'                      THEN 'UntaggedInstances'
                   WHEN 'nature_retired_still_in_use'          THEN 'RetiredNaturesStillInUse'
                   WHEN 'natures_unused'                       THEN 'NaturesWithObligations'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to obligations with a scheduled occurrence between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. An obligation with no occurrence in this window is excluded entirely, not just its '
                    + N'overdue figures - it will not appear in any row here even if it exists cumulatively.') AS Detail
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

    DROP TABLE #inst; DROP TABLE #active; DROP TABLE #ovd; DROP TABLE #owned; DROP TABLE #nature;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Nature restored (pre-v2).';
GO
-------------------------------------------------------------------------- usp_Insights_Dimension_Departments
IF OBJECT_ID('dbo.usp_Insights_Dimension_Departments', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Departments;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Departments
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

    /*  [ADDED 2026-09-25] HARD WINDOW GATE - caller-supplied period, no FY default.
        Departments' own population (ComplianceInstance via tvfInsightsScopedInstances)
        carries no per-row date - an instance persists across years while its real due
        dates live on ComplianceScheduleOn, one row per recurring occurrence (a single
        instance can have dozens). "Which instances are in scope for the selected
        period" is answered at the SCHEDULE level, same proven pattern as sql/23
        TimelinessFY and sql/11 Act: #inst is already materialised and indexed above,
        so find which of those instances had >=1 scheduled occurrence in the window,
        then narrow #inst itself down to just those. Every downstream step (per-
        department rollup, reconciliation) already reads from #inst, so they inherit
        the window for free - nothing else in this file changes.
        Do NOT reintroduce a join hint or skip the materialise-first step - the same
        shape of query against ComplianceScheduleOn's 29.4M rows without it measured
        183,502 ms on a real tenant versus 157 ms scoped-first (sql/23's own numbers).
        @WindowStart/@WindowEnd are REQUIRED, always caller-resolved from a period-
        picker choice (last 30/60/90 days, or a quarter) - no fiscal-year default here,
        that convention stays specific to TimelinessFY alone.                          */
    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51082, N'DEPARTMENTS DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51082, N'DEPARTMENTS DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    IF OBJECT_ID('tempdb..#active') IS NOT NULL DROP TABLE #active;
    SELECT DISTINCT cso.ComplianceInstanceID
    INTO #active
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd;
    CREATE CLUSTERED INDEX IX_active ON #active (ComplianceInstanceID);

    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #active a WHERE a.ComplianceInstanceID = i.ComplianceInstanceID);

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
        ImprisonmentOverdue   INT            NOT NULL,   -- [ADDED 2026-09-13] consequence ranking
        CriticalInstances     INT            NOT NULL,
        DistinctUsers         INT            NOT NULL,
        BranchesCovered       INT            NOT NULL,
        -- derived
        OverdueRank           INT            NULL,
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (DepartmentID, DepartmentName, Instances, Overdue, NoInstanceOwner,
                  ImprisonmentInstances, ImprisonmentOverdue, CriticalInstances, DistinctUsers, BranchesCovered)
    SELECT
        d.DepartmentID, d.DepartmentName,
        COUNT(i.ComplianceInstanceID),
        SUM(CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.ComplianceInstanceID IS NOT NULL AND w.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 AND o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
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

    /*  [ADDED 2026-09-13] CONSEQUENCE, not rate. A-WORST-DEPT ranks by
        OverduePct - on a live pilot that put a 69-obligation department with
        ZERO imprisonment exposure at rank 1, above one with 1,440 overdue
        obligations of which ~46% carry personal liability. Both assertions are
        emitted; the composition layer chooses.                                */
    IF EXISTS (SELECT 1 FROM #rows WHERE ImprisonmentOverdue > 0)
    INSERT #assert
    SELECT TOP 1 'A-WORST-DEPT-EXP','imprisonment_overdue_count', DepartmentName,
           ImprisonmentOverdue, NULL, (SELECT SUM(ImprisonmentOverdue) FROM #rows),
           NULL, NULL, 'worse',
           N'ranked by CONSEQUENCE - overdue obligations carrying personal liability - '
         + N'not by overdue rate. A higher rate on a smaller, liability-free portfolio '
         + N'is a different and lesser problem.'
    FROM #rows WHERE ImprisonmentOverdue > 0 ORDER BY ImprisonmentOverdue DESC, Overdue DESC;


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
           CONCAT(N'', ScopeLabel, N' has ', Value, N'% of obligations with no INSTANCE-LEVEL owner'),
           AssertionId,
           N'NOT "nobody is doing this" - most of these have a performer named on each occurrence. '
         + N'They lack an owner on the obligation itself.'
    FROM #assert WHERE AssertionId LIKE 'A-OWN-[0-9]%';

    INSERT #find
    SELECT 'F-OWN-AGG','high',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' departments (', VsComparatorPP,
                  N'%) have 10%+ of obligations with no INSTANCE-LEVEL owner'),
           AssertionId,
           N'NOT "nobody is doing this" - most of these have a performer named on each occurrence. '
         + N'They lack an owner on the obligation itself.'
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
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'ownership_has_two_mechanisms'   THEN 'TenantNoInstanceOwnerPct'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'unassigned_department'                THEN 'UnassignedPct'
                   WHEN 'departments_unused'                   THEN 'DepartmentsWithObligations'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to obligations with a scheduled occurrence between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. An obligation with no occurrence in this window is excluded entirely, not just its '
                    + N'overdue figures - it will not appear in any row here even if it exists cumulatively.') AS Detail
        UNION ALL
        SELECT 'ownership_has_two_mechanisms' AS Issue,
               N'RegTrack assigns a performer by TWO mechanisms: ComplianceAssignment (on the '
             + N'obligation) and ComplianceScheduleOn.Performerid (on each occurrence, 99.8% '
             + N'populated). This metric counts only the FIRST. Most obligations it counts DO '
             + N'have someone named per occurrence - what is missing is accountability for the '
             + N'obligation itself. NEVER present it as "nobody is doing this". NoOwnerAnywhere '
             + N'is the stricter measure.' AS Detail
        UNION ALL
        SELECT 'flow_metric_drift',
               N'Overdue is a live figure and moves between runs; stock metrics are stable.'
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

    DROP TABLE #inst; DROP TABLE #active; DROP TABLE #ovd; DROP TABLE #owned; DROP TABLE #people;
    DROP TABLE #dept; DROP TABLE #rows; DROP TABLE #detector;
    DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Departments restored (pre-v2).';
GO
-------------------------------------------------------------------------- usp_Insights_Dimension_Act
IF OBJECT_ID('dbo.usp_Insights_Dimension_Act', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Act;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Act
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
        THROW 51090, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. SCOPED INSTANCE BASE ----------------------------------------*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.Imprisonment,
        s.ActID,
        s.CategoryId
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;

    CREATE CLUSTERED INDEX IX_inst ON #inst (ActID, ComplianceInstanceID);

    /*  [ADDED 2026-09-25] HARD WINDOW GATE - caller-supplied period, no FY default.
        Act's own population (ComplianceInstance via tvfInsightsScopedInstances) carries
        no per-row date - an instance persists across years while its real due dates
        live on ComplianceScheduleOn, one row per recurring occurrence (a single
        instance can have dozens). "Which instances are in scope for the selected
        period" is answered at the SCHEDULE level, same proven pattern as sql/23
        TimelinessFY: #inst is already materialised and indexed above, so find which
        of those instances had >=1 scheduled occurrence in the window, then narrow
        #inst itself down to just those. Every downstream step (overdue join, Act
        member list, row aggregation, reconciliation) already reads from #inst, so
        they inherit the window for free - nothing else in this file changes.
        Do NOT reintroduce a join hint or skip the materialise-first step - the same
        shape of query against ComplianceScheduleOn's 29.4M rows without it measured
        183,502 ms on a real tenant versus 157 ms scoped-first (sql/23's own numbers).
        @WindowStart/@WindowEnd are REQUIRED, always caller-resolved from a period-
        picker choice (last 30/60/90 days, or a quarter) - no fiscal-year default here,
        that convention stays specific to TimelinessFY alone.                          */
    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51093, N'ACT DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51093, N'ACT DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    IF OBJECT_ID('tempdb..#active') IS NOT NULL DROP TABLE #active;
    SELECT DISTINCT cso.ComplianceInstanceID
    INTO #active
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd;
    CREATE CLUSTERED INDEX IX_active ON #active (ComplianceInstanceID);

    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #active a WHERE a.ComplianceInstanceID = i.ComplianceInstanceID);

    /*-- 2. OVERDUE ------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;

    /*-- 3. MEMBER LIST = the Acts actually in scope ---------------------
       Unlike Location or Nature there is no bounded master worth walking -
       the Act table is national. The member list is the set of Acts this
       scope touches, which is why an Act with zero instances cannot exist
       here and no "unused member" declaration is needed.               */
    IF OBJECT_ID('tempdb..#act') IS NOT NULL DROP TABLE #act;
    SELECT DISTINCT a.ID AS ActID, a.Name AS ActName, a.State, a.StateID,
           a.RegulatorID, a.ComplianceCategoryId AS CategoryId, a.StartDate
    INTO #act
    FROM Act a
    WHERE EXISTS (SELECT 1 FROM #inst i WHERE i.ActID = a.ID);

    /*-- 4. ROWS ---------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        ActID                 INT            NOT NULL PRIMARY KEY,
        ActName               NVARCHAR(500)  NULL,
        State                 NVARCHAR(200)  NULL,
        RegulatorID           INT            NULL,
        CategoryId            INT            NULL,
        Instances             INT            NOT NULL,
        Overdue               INT            NOT NULL,
        OverduePct            DECIMAL(5,1)   NULL,
        ImprisonmentInstances INT            NOT NULL,
        ImprisonmentOverdue   INT            NOT NULL,   -- [ADDED 2026-09-13] consequence ranking
        BranchesCovered       INT            NOT NULL,
        -- derived
        StartDate             DATETIME       NULL,
        OverdueRank           INT            NULL,
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (ActID, ActName, State, RegulatorID, CategoryId, Instances, Overdue,
                  ImprisonmentInstances, ImprisonmentOverdue, BranchesCovered, StartDate)
    SELECT
        a.ActID, a.ActName, a.State, a.RegulatorID, a.CategoryId,
        COUNT(i.ComplianceInstanceID),
        SUM(CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 AND o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        COUNT(DISTINCT i.BranchID),
        a.StartDate
    FROM #act a
    LEFT JOIN #inst i ON i.ActID = a.ActID
    LEFT JOIN #ovd  o ON o.ComplianceInstanceID = i.ComplianceInstanceID
    GROUP BY a.ActID, a.ActName, a.State, a.RegulatorID, a.CategoryId, a.StartDate;

    /*-- 5. RECONCILIATION, with the unlinked bucket counted back --------*/
    DECLARE @rowSum      INT = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #inst);
    DECLARE @unlinked    INT = (SELECT COUNT(*) FROM #inst WHERE ActID IS NULL);
    DECLARE @orphan      INT = (SELECT COUNT(*) FROM #inst i
                                WHERE i.ActID IS NOT NULL
                                  AND NOT EXISTS (SELECT 1 FROM #act a WHERE a.ActID = i.ActID));

    IF @orphan > 0
        THROW 51091, N'ACT DIMENSION RECONCILIATION FAILED - an instance carries an ActID absent from the Act master. This is a referential break, not a linkage gap. Refusing to publish.', 1;

    IF @rowSum + @unlinked <> @scopedTotal
        THROW 51092, N'ACT DIMENSION RECONCILIATION FAILED - per-Act sums plus the unlinked bucket do not tie to the scoped instance total. Refusing to publish.', 1;

    DECLARE @hasAnyObligations BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * (SELECT COUNT(*) FROM #ovd) / @scopedTotal END;

    UPDATE #rows SET
        OverduePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END;

    DECLARE @materialityFloor INT = 50;
    DECLARE @materialMembers  INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @materialityFloor);
    DECLARE @rankDegraded     BIT = CASE WHEN @materialMembers < 2 THEN 1 ELSE 0 END;
    DECLARE @rankFloor        INT = CASE WHEN @rankDegraded = 1 THEN 1 ELSE @materialityFloor END;

    ;WITH r AS (SELECT ActID, RANK() OVER (ORDER BY OverduePct DESC) AS rk
                FROM #rows WHERE Instances >= @rankFloor)
    UPDATE #rows SET OverdueRank = r.rk FROM #rows JOIN r ON r.ActID = #rows.ActID;

    /*-- 6. ACT x STATE DIVERGENCE --------------------------------------
       Group Act rows by NAME. Where one law appears in two or more states
       and both cuts are material, the spread between its best and worst
       state is the finding. The act is its own peer, so this is inherently
       peer-relative - no absolute rate is asserted.                     */
    IF OBJECT_ID('tempdb..#spread') IS NOT NULL DROP TABLE #spread;
    SELECT r.ActName,
           COUNT(*)                AS StatesCovered,
           SUM(r.Instances)        AS Instances,
           MIN(r.OverduePct)       AS MinOverduePct,
           MAX(r.OverduePct)       AS MaxOverduePct,
           MAX(r.OverduePct) - MIN(r.OverduePct) AS SpreadPP
    INTO #spread
    FROM #rows r
    WHERE r.Instances >= @rankFloor AND r.State IS NOT NULL
    GROUP BY r.ActName
    HAVING COUNT(*) >= 2;

    /*-- 7. DETECTIONS ---------------------------------------------------*/
    DECLARE @medianStart DATETIME =
        (SELECT TOP 1 PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY CAST(StartDate AS FLOAT)) OVER ()
         FROM #rows WHERE StartDate IS NOT NULL);

    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyObligations = 1 AND Instances >= @rankFloor
                  AND EXISTS (SELECT 1 FROM #spread s WHERE s.ActName = #rows.ActName AND s.SpreadPP >= 20.0)
                 THEN ',act_state_divergence' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND Instances >= @rankFloor
                  AND @medianStart IS NOT NULL AND StartDate > @medianStart
                  AND OverduePct > @tenantOverduePct
                 THEN ',emerging_law_adoption_lag' ELSE '' END
        , 1, 1, '');

    /*  Regulator concentration is a TENANT-level fact, not a per-Act flag:
        the share of the estate governed by the single largest regulator. */
    DECLARE @topRegulatorShare DECIMAL(5,1) = NULL;
    DECLARE @topRegulatorId INT = NULL;
    IF @hasAnyObligations = 1
        SELECT TOP 1 @topRegulatorId = RegulatorID,
                     @topRegulatorShare = CAST(100.0 * SUM(Instances) / @scopedTotal AS DECIMAL(5,1))
        FROM #rows WHERE RegulatorID IS NOT NULL
        GROUP BY RegulatorID ORDER BY SUM(Instances) DESC;

    SELECT
        'control_totals'             AS ResultSet,
        @scopedTotal                 AS ScopedInstances,
        @rowSum                      AS SumOfRows,
        CAST(1 AS BIT)               AS Reconciled,
        (SELECT COUNT(*) FROM #ovd)  AS OverdueInstances,
        @tenantOverduePct            AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows) AS ActsReported,
        (SELECT COUNT(DISTINCT ActName) FROM #rows) AS DistinctActNames,
        (SELECT COUNT(DISTINCT State) FROM #rows WHERE State IS NOT NULL) AS StatesCovered,
        (SELECT COUNT(*) FROM #spread) AS ActsSpanningMultipleStates,
        @unlinked                    AS UnlinkedInstances,
        CAST(CASE WHEN @scopedTotal = 0 THEN 0
                  ELSE 100.0 * @unlinked / @scopedTotal END AS DECIMAL(5,1)) AS UnlinkedPct,
        @topRegulatorId              AS LargestRegulatorId,
        @topRegulatorShare           AS LargestRegulatorSharePct;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*-- 8. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @material INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'act_state_divergence', @material,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%act_state_divergence%')
    UNION ALL SELECT 'emerging_law_adoption_lag', @material,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%emerging_law_adoption_lag%');

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
    SELECT TOP 1 'A-WORST-ACT','overdue_pct',
           CONCAT(ActName, CASE WHEN State IS NULL THEN N'' ELSE CONCAT(N' (', State, N')') END),
           OverduePct,OverdueRank,@rankable,
           @tenantOverduePct, OverduePct - @tenantOverduePct,
           CASE WHEN OverduePct > @tenantOverduePct THEN 'worse' ELSE 'better' END,
           NULLIF(CONCAT(
               CASE WHEN @rankDegraded = 1
                    THEN CONCAT(N'degraded_ranking_sample: no Act reaches the ', @materialityFloor,
                                N'-instance materiality floor. ') ELSE N'' END,
               CASE WHEN @tiedAtTop > 1
                    THEN CONCAT(N'tied_at_top: ', @tiedAtTop, N' Acts share this rate - not uniquely the highest. ')
                    ELSE N'' END), N'')
    FROM #rows WHERE Instances >= @rankFloor ORDER BY OverduePct DESC, Instances DESC;

    /*  [ADDED 2026-09-13] CONSEQUENCE, not rate. See the note in sql/05: on a
        live pilot the rate-ranked finding put a 69-obligation area with ZERO
        imprisonment exposure at rank 1. Both assertions are emitted; the
        composition layer chooses. Emitted only where exposure exists to rank. */
    IF EXISTS (SELECT 1 FROM #rows WHERE ImprisonmentOverdue > 0)
    INSERT #assert
    SELECT TOP 1 'A-WORST-ACT-EXP','imprisonment_overdue_count', ActName, ImprisonmentOverdue, NULL,
           (SELECT SUM(ImprisonmentOverdue) FROM #rows), NULL, NULL, 'worse',
           N'ranked by CONSEQUENCE - overdue obligations carrying personal liability - not by rate.'
    FROM #rows WHERE ImprisonmentOverdue > 0 ORDER BY ImprisonmentOverdue DESC, Overdue DESC;


    /*  The state-divergence assertion carries the SPREAD, not a rate, because
        the finding is that the same law is executed differently - the law is
        not the variable. */
    IF (SELECT EmitMode FROM #detector WHERE Detector='act_state_divergence') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-DIV-' + CAST(ROW_NUMBER() OVER (ORDER BY s.Instances DESC) AS VARCHAR(5)),
               'overdue_spread_pp', s.ActName, s.SpreadPP, NULL, s.StatesCovered,
               s.MinOverduePct, s.MaxOverduePct - s.MinOverduePct, 'worse',
               N'act_state_divergence: the same law, executed differently by state - the law is not the variable'
        FROM #spread s WHERE s.SpreadPP >= 20.0 ORDER BY s.Instances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='act_state_divergence') = 'aggregate'
        INSERT #assert
        SELECT 'A-DIV-AGG','acts_diverging_by_state',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - execution varies by state across many laws, not a handful of exceptions'
        FROM #detector WHERE Detector='act_state_divergence';

    IF (SELECT EmitMode FROM #detector WHERE Detector='emerging_law_adoption_lag') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-LAG-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
               'overdue_pct', ActName, OverduePct, NULL, NULL,
               @tenantOverduePct, OverduePct - @tenantOverduePct, 'worse',
               N'emerging_law_adoption_lag: recency inferred from Act.StartDate, NOT from a confirmed emerging-law list - state as a hypothesis'
        FROM #rows WHERE Flags LIKE '%emerging_law_adoption_lag%' ORDER BY Instances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='emerging_law_adoption_lag') = 'aggregate'
        INSERT #assert
        SELECT 'A-LAG-AGG','recent_laws_with_adoption_lag',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - recency inferred from Act.StartDate, not a confirmed emerging-law list'
        FROM #detector WHERE Detector='emerging_law_adoption_lag';

    IF @topRegulatorShare IS NOT NULL
    INSERT #assert
    VALUES ('A-REG','regulator_share_pct',
            CONCAT(N'regulator ', @topRegulatorId), @topRegulatorShare, NULL, NULL, NULL, NULL, NULL, NULL);

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS ----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-WORST-ACT','high',
           CONCAT(N'', ScopeLabel, N' has the highest overdue rate at ', Value, N'%'),
           'A-WORST-ACT,A-TENANT', NULL
    FROM #assert WHERE AssertionId = 'A-WORST-ACT' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-DIV','high',
           CONCAT(N'', ScopeLabel, N' varies by ', Value, N' points across ', OfN, N' states'),
           AssertionId,
           N'The law is identical in each state - the variation is execution, not regulation. Do not attribute it to the law being harder somewhere.'
    FROM #assert WHERE AssertionId LIKE 'A-DIV-[0-9]%';

    INSERT #find
    SELECT 'F-DIV-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' Acts (', VsComparatorPP,
                  N'%) are executed materially differently across states'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-DIV-AGG';

    INSERT #find
    SELECT 'F-LAG','medium',
           CONCAT(N'', ScopeLabel, N' runs at ', Value, N'% overdue, above the tenant average'),
           AssertionId,
           N'Recency is inferred from Act.StartDate, not a confirmed emerging-law list. Present as a hypothesis to verify, never as an established adoption problem.'
    FROM #assert WHERE AssertionId LIKE 'A-LAG-[0-9]%';

    INSERT #find
    SELECT 'F-LAG-AGG','info',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' recently-started Acts (', VsComparatorPP,
                  N'%) run above the tenant overdue rate'),
           AssertionId,
           N'Recency inferred from Act.StartDate. Present as a hypothesis, not a conclusion.'
    FROM #assert WHERE AssertionId = 'A-LAG-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY ------------------------------------------------*/
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                                THEN 'ScopedInstances'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'emerging_law_proxy'                   THEN 'ActsReported'
                   WHEN 'acts_without_state'                   THEN 'StatesCovered'
                   WHEN 'instances_not_linked_to_an_act'       THEN 'UnlinkedPct'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to obligations with a scheduled occurrence between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. An obligation with no occurrence in this window is excluded entirely, not just its '
                    + N'overdue figures - it will not appear in any row here even if it exists cumulatively.') AS Detail
        UNION ALL
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'emerging_law_proxy',
               N'"Emerging law" is not a field in the schema. Adoption-lag detection uses Act.StartDate '
             + N'relative to this tenant''s own Act population as a proxy. Any adoption-lag statement is a '
             + N'hypothesis to confirm with the BA, not an established finding. (Open item.)'
        WHERE EXISTS (SELECT 1 FROM #rows WHERE Flags LIKE '%emerging_law_adoption_lag%')
        UNION ALL
        SELECT 'acts_without_state',
               CONCAT(N'', COUNT(*), N' Act(s) in this scope carry no State value, so they are excluded '
                    + N'from the Act-by-state comparison.')
        FROM #rows WHERE State IS NULL HAVING COUNT(*) > 0
        UNION ALL
        SELECT 'instances_not_linked_to_an_act',
               CONCAT(N'', @unlinked, N' obligation(s) in this scope are not linked to any Act. They are '
                    + N'counted in the tenant total but appear in NO Act row, so every per-Act figure '
                    + N'below excludes them and the rows do not sum to the scoped total. A configuration '
                    + N'gap, not a defect.')
        WHERE @unlinked > 0
    ) q;

    DROP TABLE #inst; DROP TABLE #active; DROP TABLE #ovd; DROP TABLE #act; DROP TABLE #spread;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Act restored (pre-v2).';
GO
-------------------------------------------------------------------------- usp_Insights_Dimension_Users
IF OBJECT_ID('dbo.usp_Insights_Dimension_Users', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Users;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Users
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
        THROW 51100, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. SCOPED INSTANCE BASE ----------------------------------------*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT s.ComplianceInstanceID, s.BranchID, s.Imprisonment
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;

    CREATE CLUSTERED INDEX IX_inst ON #inst (ComplianceInstanceID);

    /*  [ADDED 2026-09-25] HARD WINDOW GATE - caller-supplied period, no FY default.
        Users' own population (ComplianceInstance via tvfInsightsScopedInstances) carries
        no per-row date - an instance persists across years while its real due dates
        live on ComplianceScheduleOn, one row per recurring occurrence (a single
        instance can have dozens). "Which instances are in scope for the selected
        period" is answered at the SCHEDULE level, same proven pattern as sql/23
        TimelinessFY and sql/11 Act: #inst is already materialised and indexed above,
        so find which of those instances had >=1 scheduled occurrence in the window,
        then narrow #inst itself down to just those. #asg (built next, section 2) joins
        #inst for BOTH performer and reviewer roles, so both inherit the window
        identically - there is only one instance population in this dimension, not two.
        Every downstream step (quality, timing, per-user rollup, reconciliation) already
        reads from #inst/#asg, so they inherit the window for free too.
        Do NOT reintroduce a join hint or skip the materialise-first step - the same
        shape of query against ComplianceScheduleOn's 29.4M rows without it measured
        183,502 ms on a real tenant versus 157 ms scoped-first (sql/23's own numbers).
        @WindowStart/@WindowEnd are REQUIRED, always caller-resolved from a period-
        picker choice (last 30/60/90 days, or a quarter) - no fiscal-year default here,
        that convention stays specific to TimelinessFY alone.                          */
    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51104, N'USERS DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51104, N'USERS DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    IF OBJECT_ID('tempdb..#active') IS NOT NULL DROP TABLE #active;
    SELECT DISTINCT cso.ComplianceInstanceID
    INTO #active
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd;
    CREATE CLUSTERED INDEX IX_active ON #active (ComplianceInstanceID);

    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #active a WHERE a.ComplianceInstanceID = i.ComplianceInstanceID);

    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;

    /*-- 2. ASSIGNMENTS - the grain of this dimension --------------------*/
    IF OBJECT_ID('tempdb..#asg') IS NOT NULL DROP TABLE #asg;
    SELECT DISTINCT ca.UserID, ca.RoleID, ca.ComplianceInstanceID
    INTO #asg
    FROM ComplianceAssignment ca
    JOIN #inst i ON i.ComplianceInstanceID = ca.ComplianceInstanceID
    WHERE ca.UserID > 0;

    CREATE CLUSTERED INDEX IX_asg ON #asg (UserID, ComplianceInstanceID);

    /*-- 3. ON-TIME COMPLETION, PERFORMER ONLY, OWN WORK -----------------
       Facet B2. Timeliness comes from the dictionary; resolved_terminal
       carries no timeliness and is therefore excluded from the denominator
       by construction, exactly as spec 6.4 requires.                    */
    IF OBJECT_ID('tempdb..#quality') IS NOT NULL DROP TABLE #quality;
    SELECT a.UserID,
           COUNT(*)                                                     AS CompletedEvents,
           SUM(CASE WHEN d.Timeliness = 'on_time' THEN 1 ELSE 0 END)     AS OnTimeEvents
    INTO #quality
    FROM #asg a
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = a.ComplianceInstanceID
    JOIN ComplianceTransaction t  ON t.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = t.StatusId
    WHERE a.RoleID = 3 AND d.ClosureClass = 'completed' AND d.Timeliness IS NOT NULL
    GROUP BY a.UserID;

    /*-- 3b. COMPLETION TIMING - HOW early/late, not just whether ---------
       [ADDED 2026-09-13] New facet, PERFORMER ONLY, OWN WORK - same population as
       #quality above (RoleID=3, ClosureClass='completed'), never blended with
       reviewer timing, which would need a different date pair (submission vs
       review date) and is not built here. Timeliness (facet B2 above) only says
       on_time/delayed; this says BY HOW MUCH, in real days, from real dates:
       [DECIDED 2026-09-13] Dated is the SYSTEM RECORD date - a real timestamp on 100%
       of rows. StatusChangedOn is the USER-STATED completion date - midnight on 86.6%
       of rows, i.e. a date not a timestamp. Record date is used because a completion
       that was never recorded cannot be evidenced to a regulator. The two disagree
       materially: tenant 1817 medians of +18 days (record) and -1 day (stated) - the
       same events, opposite conclusions. The choice is therefore DECLARED in
       data_quality below, never left implicit.

       ComplianceScheduleOn.ScheduleOn (the due date) vs ComplianceTransaction.Dated
       (the actual completion-event date - the same column
       tvfInsightsLatestStatus itself already orders by, confirmed live on tenant
       1300, 2026-09-13). Negative = finished early, positive = finished late.

       [TRAP - confirmed live] A handful of events show a gap of over 1000 days in
       either direction - bulk-migration/backdated-schedule artifacts, the same
       class of noise CLAUDE.md sec.5 already documents for this table, not real
       human behaviour. A MEAN would let one such row wreck a user's entire
       reading (measured live: one user's average flipped to -160 days off a
       37-event sample because of a handful of these). Excluded here at |gap| >
       365 days - no normal compliance cycle runs a year early or late - and the
       excluded count is declared tenant-wide in data_quality below, never
       silently dropped. The per-user figure itself is still a MEDIAN
       (PERCENTILE_CONT), not a mean, as further protection even within the
       365-day band.                                                        */
    IF OBJECT_ID('tempdb..#timing') IS NOT NULL DROP TABLE #timing;
    SELECT a.UserID,
           DATEDIFF(day, cso.ScheduleOn, t.Dated) AS DaysLate
    INTO #timing
    FROM #asg a
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = a.ComplianceInstanceID
    JOIN ComplianceTransaction t  ON t.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = t.StatusId
    WHERE a.RoleID = 3 AND d.ClosureClass = 'completed' AND d.Timeliness IS NOT NULL;

    DECLARE @timingOutliersExcluded INT = (SELECT COUNT(*) FROM #timing WHERE ABS(DaysLate) > 365);

    /*  [ADDED 2026-09-13] EarlyCount/LateCount/OnTimeCount/EarlyPct - the split behind the
        median, not just the median itself. EarlyPct is DATE-based (DaysLate < 0), never to be
        confused with the existing status-based OnTimePct in #quality above - a different
        population and a different definition of "on time", kept as separate, differently-named
        fields per CLAUDE.md sec.4a's own residual-naming rule (never conflate two distinct
        counts under one name). Same 365-day exclusion window as the median. */
    IF OBJECT_ID('tempdb..#medtiming') IS NOT NULL DROP TABLE #medtiming;
    SELECT DISTINCT UserID,
           CAST(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY CAST(DaysLate AS FLOAT))
                OVER (PARTITION BY UserID) AS DECIMAL(9,1)) AS MedianDaysEarlyLate,
           COUNT(*) OVER (PARTITION BY UserID) AS TimingSampleSize,
           SUM(CASE WHEN DaysLate < 0 THEN 1 ELSE 0 END) OVER (PARTITION BY UserID) AS EarlyCount,
           SUM(CASE WHEN DaysLate > 0 THEN 1 ELSE 0 END) OVER (PARTITION BY UserID) AS LateCount,
           SUM(CASE WHEN DaysLate = 0 THEN 1 ELSE 0 END) OVER (PARTITION BY UserID) AS OnTimeCount
    INTO #medtiming
    FROM #timing
    WHERE ABS(DaysLate) <= 365;

    DECLARE @tenantMedianDaysEarlyLate DECIMAL(9,1) = (
        SELECT TOP 1 CAST(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY CAST(DaysLate AS FLOAT)) OVER () AS DECIMAL(9,1))
        FROM #timing WHERE ABS(DaysLate) <= 365);

    /*-- 4. ENGAGEMENT - 12-month login window, keyed by EMAIL -----------*/
    IF OBJECT_ID('tempdb..#login') IS NOT NULL DROP TABLE #login;
    SELECT u.ID AS UserID, COUNT(l.ID) AS Logins12m
    INTO #login
    FROM [User] u
    LEFT JOIN UserLoginTrack l
           ON l.Email = u.Email
          AND l.LoginDate >= DATEADD(MONTH, -12, @AsOf)
          AND l.LoginDate <  @AsOf
    WHERE u.CustomerID = @CustomerID AND u.IsDeleted = 0
    GROUP BY u.ID;

    /*-- 5. ROWS - one per user holding assignments ----------------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        UserID                BIGINT         NOT NULL PRIMARY KEY,
        UserName              NVARCHAR(300)  NULL,
        IsActive              BIT            NULL,
        Instances             INT            NOT NULL,   -- distinct instances held, any role
        PerformerInstances    INT            NOT NULL,
        ReviewerInstances     INT            NOT NULL,
        OtherRoleInstances    INT            NOT NULL,   -- [TRAP] RoleID outside {3,4} - e.g. RoleID 6, seen live on
                                                           -- tenant 1403, not yet in DIMENSION_SPECS.md. Never blended
                                                           -- into Performer/Reviewer - surfaced separately so an
                                                           -- undocumented role cannot silently vanish into a total.
        Overdue               INT            NOT NULL,
        OverduePct            DECIMAL(5,1)   NULL,
        ImprisonmentInstances INT            NOT NULL,
        ImprisonmentOverdue   INT            NOT NULL,   -- [ADDED 2026-09-13] consequence ranking
        BranchesCovered       INT            NOT NULL,
        Logins12m             INT            NOT NULL,
        EngagementBand        VARCHAR(20)    NULL,
        CompletedEvents       INT            NOT NULL,
        OnTimeEvents          INT            NOT NULL,
        OnTimePct             DECIMAL(5,1)   NULL,
        QuadrantOverlay       VARCHAR(30)    NULL,
        MedianDaysEarlyLate   DECIMAL(9,1)   NULL,   -- NULL = no qualifying completed event (never 0 - 0 is a real "right on the day")
        TimingSampleSize      INT            NULL,   -- how many completed events the median above is drawn from
        EarlyCount            INT            NULL,
        LateCount             INT            NULL,
        OnTimeCount           INT            NULL,   -- DaysLate = 0 exactly - NOT the same population/definition as OnTimeEvents above
        EarlyPct              DECIMAL(5,1)   NULL,   -- date-based (DaysLate < 0) - NOT the same as OnTimePct, which is status-based
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (UserID, UserName, IsActive, Instances, PerformerInstances, ReviewerInstances,
                  OtherRoleInstances, Overdue, ImprisonmentInstances, ImprisonmentOverdue, BranchesCovered, Logins12m,
                  CompletedEvents, OnTimeEvents)
    SELECT
        a.UserID,
        LTRIM(RTRIM(CONCAT(u.FirstName, N' ', u.LastName))),
        u.IsActive,
        COUNT(DISTINCT a.ComplianceInstanceID),
        COUNT(DISTINCT CASE WHEN a.RoleID = 3 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.RoleID = 4 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.RoleID NOT IN (3,4) THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN i.Imprisonment = 1 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN i.Imprisonment = 1 AND o.ComplianceInstanceID IS NOT NULL THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT i.BranchID),
        ISNULL(MAX(lg.Logins12m), 0),
        ISNULL(MAX(q.CompletedEvents), 0),
        ISNULL(MAX(q.OnTimeEvents), 0)
    FROM #asg a
    JOIN #inst i         ON i.ComplianceInstanceID = a.ComplianceInstanceID
    LEFT JOIN #ovd o     ON o.ComplianceInstanceID = a.ComplianceInstanceID
    LEFT JOIN [User] u   ON u.ID = a.UserID
    LEFT JOIN #login lg  ON lg.UserID = a.UserID
    LEFT JOIN #quality q ON q.UserID = a.UserID
    GROUP BY a.UserID, u.FirstName, u.LastName, u.IsActive;

    /*-- 6. RECONCILIATION - DISTINCT-INSTANCE UNION, never a sum --------
       [TRAP] A GUARD THAT CANNOT FAIL IS NOT A GUARD.
       @unassigned is DEFINED as @scopedTotal - @assignedUnion, so testing
       "@assignedUnion + @unassigned = @scopedTotal" is an algebraic identity,
       and #asg is INNER JOINed to #inst so "@unassigned < 0" is unreachable
       too. Both look like reconciliation and neither can ever fire.
       The checks below count from an INDEPENDENT direction - from the instance
       side rather than the assignment side - so a fan-out or a scope leak
       actually shows up as a disagreement between two counts.            */
    DECLARE @scopedTotal   INT = (SELECT COUNT(*) FROM #inst);
    DECLARE @assignedUnion INT = (SELECT COUNT(DISTINCT ComplianceInstanceID) FROM #asg);
    DECLARE @unassigned    INT = @scopedTotal - @assignedUnion;
    DECLARE @rowSum        INT = (SELECT ISNULL(SUM(Instances),0) FROM #rows);

    /*  Same quantity, counted from #inst instead of #asg. */
    DECLARE @assignedViaInst INT = (
        SELECT COUNT(*) FROM #inst i
        WHERE EXISTS (SELECT 1 FROM #asg a WHERE a.ComplianceInstanceID = i.ComplianceInstanceID));

    IF @assignedViaInst <> @assignedUnion
        THROW 51101, N'USERS DIMENSION RECONCILIATION FAILED - the distinct-instance union counted from the assignment side disagrees with the count from the instance side. A join is fanning out or an out-of-scope assignment has leaked in. Refusing to publish.', 1;

    /*  No single user can hold more distinct instances than exist in the union.
        This is what actually catches the 155% class of bug at the row grain. */
    IF EXISTS (SELECT 1 FROM #rows WHERE Instances > @assignedUnion)
        THROW 51102, N'USERS DIMENSION RECONCILIATION FAILED - a user row claims more distinct instances than the whole assigned union contains. A join is fanning out. Refusing to publish.', 1;

    /*  Every assigned instance appears in at least one user row, so the sum of
        per-user counts must be at least the union. Less means rows were lost. */
    IF @rowSum < @assignedUnion
        THROW 51103, N'USERS DIMENSION RECONCILIATION FAILED - the sum of per-user instance counts is below the distinct-instance union, so assigned instances are missing from the rows. Refusing to publish.', 1;

    DECLARE @hasAnyObligations BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * (SELECT COUNT(*) FROM #ovd) / @scopedTotal END;

    UPDATE #rows SET
        OverduePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END,
        OnTimePct  = CASE WHEN CompletedEvents = 0 THEN NULL
                          ELSE 100.0 * OnTimeEvents / CompletedEvents END,
        EngagementBand = CASE WHEN Logins12m >= 100 THEN 'power'
                              WHEN Logins12m >= 26  THEN 'frequent'
                              WHEN Logins12m >= 6   THEN 'moderate'
                              WHEN Logins12m >= 1   THEN 'seldom'
                              ELSE 'never' END;

    /*  MedianDaysEarlyLate stays NULL for a user with zero qualifying events -
        never defaulted to 0, which would misreport them as "always on the day". */
    UPDATE r SET
        r.MedianDaysEarlyLate = mt.MedianDaysEarlyLate,
        r.TimingSampleSize    = mt.TimingSampleSize,
        r.EarlyCount          = mt.EarlyCount,
        r.LateCount           = mt.LateCount,
        r.OnTimeCount         = mt.OnTimeCount,
        r.EarlyPct            = CASE WHEN mt.TimingSampleSize = 0 THEN NULL
                                      ELSE 100.0 * mt.EarlyCount / mt.TimingSampleSize END
    FROM #rows r
    JOIN #medtiming mt ON mt.UserID = r.UserID;

    /*  The 2x2 overlay. Engagement on one axis, quality on the other - the
        third cell (disengaged but current) is the dependency risk that login
        frequency alone can never find. Users with no completed work have no
        quality reading and are left unclassified rather than assumed.      */
    DECLARE @medianOnTime DECIMAL(5,1) =
        (SELECT TOP 1 PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY OnTimePct) OVER ()
         FROM #rows WHERE OnTimePct IS NOT NULL);

    UPDATE #rows SET QuadrantOverlay =
        CASE WHEN OnTimePct IS NULL OR @medianOnTime IS NULL THEN NULL
             WHEN EngagementBand IN ('power','frequent') AND OnTimePct >= @medianOnTime THEN 'engaged_quality'
             WHEN EngagementBand IN ('power','frequent') AND OnTimePct <  @medianOnTime THEN 'engaged_slipping'
             WHEN OnTimePct >= @medianOnTime THEN 'disengaged_current'
             ELSE 'disengaged_slipping' END;

    /*-- 7. DETECTIONS ---------------------------------------------------*/
    DECLARE @medianLoad DECIMAL(9,2) =
        (SELECT TOP 1 PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY CAST(PerformerInstances AS FLOAT)) OVER ()
         FROM #rows WHERE PerformerInstances > 0);

    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyObligations = 1 AND Instances > 0 AND Logins12m = 0
                 THEN ',never_logged_in_holding_assignments' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND Instances > 0 AND IsActive = 0
                 THEN ',deactivated_holding_work' ELSE '' END +
            /*  Peer-relative: three times this tenant's own median performer load,
                never an absolute count - load varies by an order of magnitude
                between tenants. */
            CASE WHEN @hasAnyObligations = 1 AND @medianLoad IS NOT NULL AND @medianLoad > 0
                  AND PerformerInstances > @medianLoad * 3
                 THEN ',overloaded_performer' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND ReviewerInstances > 0
                  AND PerformerInstances = 0
                 THEN ',review_only_user' ELSE '' END
        , 1, 1, '');

    /*  single_reviewer_dependency is an INSTANCE-level fact, not a user-level
        one: instances whose only reviewer is one person. Counted as a tenant
        figure so it cannot be double-counted across users. */
    DECLARE @soleReviewerInstances INT = (
        SELECT COUNT(*) FROM (
            SELECT a.ComplianceInstanceID
            FROM #asg a WHERE a.RoleID = 4
            GROUP BY a.ComplianceInstanceID
            HAVING COUNT(DISTINCT a.UserID) = 1) x);

    SELECT
        'control_totals'              AS ResultSet,
        @scopedTotal                  AS ScopedInstances,
        @assignedUnion                AS AssignedInstancesDistinct,
        CAST(1 AS BIT)                AS Reconciled,
        @unassigned                   AS UnassignedInstances,
        (SELECT COUNT(*) FROM #ovd)   AS OverdueInstances,
        @tenantOverduePct             AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows)  AS UsersReported,
        @rowSum                       AS SumOfPerUserInstances,
        @medianOnTime                 AS TenantMedianOnTimePct,
        @medianLoad                   AS TenantMedianPerformerLoad,
        @soleReviewerInstances        AS InstancesWithSoleReviewer,
        @tenantMedianDaysEarlyLate    AS TenantMedianDaysEarlyLate,
        @timingOutliersExcluded       AS TimingOutliersExcluded;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*-- 8. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @allUsers INT = (SELECT COUNT(*) FROM #rows);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'never_logged_in_holding_assignments', @allUsers,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%never_logged_in_holding_assignments%')
    UNION ALL SELECT 'deactivated_holding_work', @allUsers,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%deactivated_holding_work%')
    UNION ALL SELECT 'overloaded_performer', @allUsers,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%overloaded_performer%');

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

    /*-- 9. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    INSERT #assert VALUES ('A-TENANT','overdue_pct',N'tenant',@tenantOverduePct,NULL,NULL,NULL,NULL,NULL,NULL);

    /*  CONCENTRATION - the 155% trap. Distinct-instance union of the top ten
        users by load, over the assigned union. Never a sum of per-user counts. */
    IF @assignedUnion > 0
    BEGIN
        DECLARE @top10Union INT = (
            SELECT COUNT(DISTINCT a.ComplianceInstanceID)
            FROM #asg a
            WHERE a.UserID IN (SELECT TOP 10 UserID FROM #rows ORDER BY Instances DESC));

        INSERT #assert
        VALUES ('A-CONC','top10_share_of_assigned_pct',N'tenant',
                CAST(100.0 * @top10Union / @assignedUnion AS DECIMAL(5,1)),
                NULL, @assignedUnion, NULL, NULL, NULL,
                N'distinct-instance union, not a sum of per-user counts - summing double-counts paired performer/reviewer work');
    END

    /*  ENGAGEMENT BANDS - facet B1. The caveat is MANDATORY on every band
        assertion. Never-login users showed the LOWEST overdue rate on the
        reference tenant because they are nominal reviewers, not because they
        are the best compliers. */
    INSERT #assert
    SELECT 'A-BAND-' + CAST(ROW_NUMBER() OVER (ORDER BY MIN(Logins12m) DESC) AS VARCHAR(5)),
           'overdue_pct', CONCAT(N'engagement band: ', EngagementBand),
           CAST(CASE WHEN SUM(Instances) = 0 THEN 0
                     ELSE 100.0 * SUM(Overdue) / SUM(Instances) END AS DECIMAL(5,1)),
           NULL, COUNT(*), @tenantOverduePct, NULL, NULL,
           N'confounded_by_role_mix: login frequency measures ENGAGEMENT, not compliance quality. Low overdue in a disengaged band reflects nominal reviewer roles on work others keep current.'
    FROM #rows WHERE @hasAnyObligations = 1
    GROUP BY EngagementBand;

    /*  DEPENDENCY RISK - the cell login frequency alone cannot find. */
    IF EXISTS (SELECT 1 FROM #rows WHERE QuadrantOverlay = 'disengaged_current')
    INSERT #assert
    SELECT 'A-DEPEND','users_disengaged_but_current',N'tenant',
           COUNT(*), NULL, @allUsers, NULL, NULL, NULL,
           N'dependency_risk: current work held by users who are not present. Not a performance verdict - a continuity one.'
    FROM #rows WHERE QuadrantOverlay = 'disengaged_current';

    IF (SELECT EmitMode FROM #detector WHERE Detector='deactivated_holding_work') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-DEACT-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
               'instances_held', UserName, Instances, NULL, NULL, NULL, NULL, NULL,
               N'deactivated_holding_work: account is deactivated but still holds live assignments'
        FROM #rows WHERE Flags LIKE '%deactivated_holding_work%' ORDER BY Instances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='deactivated_holding_work') = 'aggregate'
        INSERT #assert
        SELECT 'A-DEACT-AGG','deactivated_users_holding_work',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - deactivated accounts holding live work is a tenant-wide provisioning pattern'
        FROM #detector WHERE Detector='deactivated_holding_work';

    IF (SELECT EmitMode FROM #detector WHERE Detector='overloaded_performer') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-LOAD-' + CAST(ROW_NUMBER() OVER (ORDER BY PerformerInstances DESC) AS VARCHAR(5)),
               'performer_instances', UserName, PerformerInstances, NULL, NULL,
               @medianLoad, PerformerInstances - @medianLoad, 'worse',
               N'overloaded_performer: measured against this tenant''s own median performer load'
        FROM #rows WHERE Flags LIKE '%overloaded_performer%' ORDER BY PerformerInstances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='overloaded_performer') = 'aggregate'
        INSERT #assert
        SELECT 'A-LOAD-AGG','overloaded_performers',N'tenant',
               Flagged, NULL, Eligible, @medianLoad, FlaggedPct, 'worse',
               N'aggregate - load is concentrated across many users, not a handful'
        FROM #detector WHERE Detector='overloaded_performer';

    IF @soleReviewerInstances > 0 AND @assignedUnion > 0
    INSERT #assert
    VALUES ('A-SOLEREV','instances_with_sole_reviewer',N'tenant',@soleReviewerInstances,
            NULL,@assignedUnion,NULL,NULL,NULL,
            N'single_reviewer_dependency: counted at INSTANCE level so it cannot be double-counted across users');


    /*  [ADDED 2026-09-13] CONSEQUENCE, not rate. See the note in sql/05: on a
        live pilot the rate-ranked finding put a 69-obligation area with ZERO
        imprisonment exposure at rank 1. Both assertions are emitted; the
        composition layer chooses. Emitted only where exposure exists to rank. */
    IF EXISTS (SELECT 1 FROM #rows WHERE ImprisonmentOverdue > 0)
    INSERT #assert
    SELECT TOP 1 'A-WORST-USER-EXP','imprisonment_overdue_count', UserName, ImprisonmentOverdue, NULL,
           (SELECT SUM(ImprisonmentOverdue) FROM #rows), NULL, NULL, 'worse',
           N'ranked by CONSEQUENCE - overdue obligations carrying personal liability - not by '
         + N'rate or by load. A user holding many low-consequence items is a capacity question; '
         + N'this is a liability one.'
    FROM #rows WHERE ImprisonmentOverdue > 0 ORDER BY ImprisonmentOverdue DESC, Overdue DESC;

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS ----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-CONC','medium',
           CONCAT(N'The ten busiest users hold ', Value, N'% of all assigned obligations'),
           AssertionId,
           N'This is a distinct-instance union. Do not add per-user counts together - that double-counts paired performer/reviewer work and can exceed 100%.'
    FROM #assert WHERE AssertionId = 'A-CONC';

    INSERT #find
    SELECT 'F-DEPEND','high',
           CONCAT(N'', CAST(Value AS INT), N' user(s) hold work that is current but are not logging in'),
           'A-DEPEND',
           N'MUST NOT be presented as good performance. This is a continuity risk: live obligations are assigned to people who are not present. Someone else is carrying them.'
    FROM #assert WHERE AssertionId = 'A-DEPEND';

    INSERT #find
    SELECT 'F-DEACT','high',
           CONCAT(N'', ScopeLabel, N' is deactivated but still holds ', CAST(Value AS INT), N' live obligation(s)'),
           AssertionId,
           N'The account is disabled; the work is not. Do not report this as resolved.'
    FROM #assert WHERE AssertionId LIKE 'A-DEACT-[0-9]%';

    INSERT #find
    SELECT 'F-DEACT-AGG','high',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' users (', VsComparatorPP,
                  N'%) are deactivated but still hold live obligations'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-DEACT-AGG';

    INSERT #find
    SELECT 'F-LOAD','medium',
           CONCAT(N'', ScopeLabel, N' performs ', CAST(Value AS INT),
                  N' obligations, against a tenant median of ', CAST(ComparatorValue AS INT)),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-LOAD-[0-9]%';

    INSERT #find
    SELECT 'F-LOAD-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' users (', VsComparatorPP,
                  N'%) carry more than three times the median performer load'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-LOAD-AGG';

    INSERT #find
    SELECT 'F-SOLEREV','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' assigned obligations depend on a single reviewer'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-SOLEREV';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY ------------------------------------------------*/
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding.
        [EXTENDED 2026-09-13] implausible_completion_gaps/undocumented_role_id are this session's
        own additions (completion-timing, OtherRoleInstances) - not in the handoff's own mapping,
        added here so they follow the same binding convention. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'engagement_is_not_quality'            THEN 'LoginBand'
                   WHEN 'users_without_quality_reading'        THEN 'OnTimePct'
                   WHEN 'unassigned_instances'                 THEN 'UnassignedInstances'
                   WHEN 'login_keyed_by_email'                 THEN 'LoginBand'
                   WHEN 'timing_measured_from_record_date'     THEN 'MedianDaysEarlyLate'
                   WHEN 'recording_lag'                        THEN 'MedianDaysEarlyLate'
                   WHEN 'implausible_completion_gaps'          THEN 'MedianDaysEarlyLate'
                   WHEN 'undocumented_role_id'                 THEN 'OtherRoleInstances'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to obligations with a scheduled occurrence between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. An obligation with no occurrence in this window is excluded entirely, not just its '
                    + N'overdue figures - it will not appear against any user here even if it exists '
                    + N'cumulatively.') AS Detail
        UNION ALL
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'engagement_is_not_quality',
               N'Login frequency measures ENGAGEMENT and adoption, never compliance quality. On the '
             + N'reference tenant the never-login band showed the LOWEST overdue rate because those users '
             + N'are nominal reviewers on work active performers keep current. Every engagement assertion '
             + N'carries caveat confounded_by_role_mix and must be cited with it.'
        UNION ALL
        SELECT 'users_without_quality_reading',
               CONCAT(N'', COUNT(*), N' user(s) have no completed performer work in this scope, so they '
                    + N'have no on-time reading and are unclassified in the engagement/quality overlay '
                    + N'rather than assumed to be either.')
        FROM #rows WHERE OnTimePct IS NULL HAVING COUNT(*) > 0
        UNION ALL
        SELECT 'unassigned_instances',
               CONCAT(N'', @unassigned, N' obligation(s) in this scope have no assigned user at all and '
                    + N'therefore appear in no user row.')
        WHERE @unassigned > 0
        UNION ALL
        SELECT 'timing_measured_from_record_date',
               N'MedianDaysEarlyLate is measured from ComplianceTransaction.Dated - the SYSTEM RECORD '
             + N'date, when the completion was entered. A second date exists: StatusChangedOn, the date '
             + N'the user states the work was done. They are NOT the same and the choice changes the SIGN '
             + N'of the answer. Measured live 2026-09-13 on tenant 1817: median 18 days LATE by record '
             + N'date, 1 day EARLY by stated date - the same events, opposite conclusions. Record date is '
             + N'used deliberately: a completion that was never recorded cannot be evidenced to a '
             + N'regulator. NEVER present this as "when the work was done" - it is when the work was '
             + N'RECORDED.'
        UNION ALL
        SELECT 'recording_lag',
               N'Across four production tenants the record date is later than the user-stated date on 74% '
             + N'to 99.6% of completed events, never earlier, by a mean of 23 to 66 days. That lag is the '
             + N'distance between doing the work and being able to prove it, and it inflates every '
             + N'days-late figure here by roughly that amount. Treat MedianDaysEarlyLate as RECORDING '
             + N'timeliness, not working timeliness.'
        UNION ALL
        SELECT 'implausible_completion_gaps',
               CONCAT(N'', @timingOutliersExcluded, N' completed event(s) show a gap of more than 365 days '
                    + N'between due date and completion date - almost certainly bulk-migration or '
                    + N'backdated-schedule artifacts, not real behaviour. Excluded from every '
                    + N'MedianDaysEarlyLate figure in this report, tenant-wide and per-user.')
        WHERE @timingOutliersExcluded > 0
        UNION ALL
        SELECT 'login_keyed_by_email',
               N'UserLoginTrack is keyed by Email, not UserID. A user whose email changed, or who shares an '
             + N'address, may have an inaccurate login count. Engagement bands are indicative, not exact.'
        UNION ALL
        SELECT 'undocumented_role_id',
               CONCAT(N'RoleID(s) ', ids.List,
                      N' appear on ', cnt.InstanceCount,
                      N' assignment(s) in this scope and are not RoleID 3 (performer) or 4 (reviewer). ',
                      N'Not classified as performer or reviewer work - counted only in OtherRoleInstances. ',
                      N'DIMENSION_SPECS.md needs a BA-signed definition before this can be classified.')
        FROM (SELECT STRING_AGG(CAST(RoleID AS VARCHAR(10)), ', ') AS List
              FROM (SELECT DISTINCT RoleID FROM #asg WHERE RoleID NOT IN (3,4)) r) ids
        CROSS JOIN (SELECT COUNT(DISTINCT ComplianceInstanceID) AS InstanceCount
                    FROM #asg WHERE RoleID NOT IN (3,4)) cnt
        WHERE ids.List IS NOT NULL
    ) q;

    DROP TABLE #inst; DROP TABLE #active; DROP TABLE #ovd; DROP TABLE #asg; DROP TABLE #quality;
    DROP TABLE #timing; DROP TABLE #medtiming;
    DROP TABLE #login; DROP TABLE #rows; DROP TABLE #detector;
    DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Users restored (pre-v2).';
GO
-------------------------------------------------------------------------- usp_Insights_Dimension_Internal
IF OBJECT_ID('dbo.usp_Insights_Dimension_Internal', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Internal;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Internal
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
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd;
    CREATE CLUSTERED INDEX IX_statActive ON #statActive (ComplianceInstanceID);

    DELETE s FROM #stat s
    WHERE NOT EXISTS (SELECT 1 FROM #statActive a WHERE a.ComplianceInstanceID = s.ComplianceInstanceID);

    IF OBJECT_ID('tempdb..#statOvd') IS NOT NULL DROP TABLE #statOvd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #statOvd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #stat s ON s.ComplianceInstanceID = o.ComplianceInstanceID;

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
    WHERE ii.IsDeleted = 0 AND cb.IsDeleted = 0 AND cb.Status = 1 AND cb.CustomerID = @CustomerID;

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
PRINT 'usp_Insights_Dimension_Internal restored (pre-v2).';
GO
-------------------------------------------------------------------------- usp_Insights_Dimension_TimelinessFY
IF OBJECT_ID('dbo.usp_Insights_Dimension_TimelinessFY', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_TimelinessFY;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_TimelinessFY
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
      AND cso.ScheduleOn >= @prevWindowStart AND cso.ScheduleOn < @WindowEnd;

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
PRINT 'usp_Insights_Dimension_TimelinessFY restored (pre-v2).';
GO
-------------------------------------------------------------------------- usp_Insights_FreeMonthly_LoadFacts
IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_LoadFacts', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_LoadFacts;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_LoadFacts
    @UserID          INT,
    @CustomerID      INT,
    @CurrMonthStart  DATE,
    @AsOf            DATETIME,
    @AllowedBranches VARCHAR(MAX) = NULL     -- entitlement filter, see header; NULL = unfiltered
AS
BEGIN
    SET NOCOUNT ON;

    /*===================================================================
      0. INPUT CONTRACT - fail closed before reading anything
    ===================================================================*/
    IF @UserID IS NULL OR @CustomerID IS NULL OR @CurrMonthStart IS NULL OR @AsOf IS NULL
        THROW 51235, N'FREE MONTHLY - WINDOW INPUT MISSING: @UserID, @CustomerID, @CurrMonthStart and @AsOf are all required. The caller resolves the edition and passes concrete values. Refusing to compute.', 1;

    /*  Digits and commas only, no empty element, no id longer than 10 digits
        (INT overflow). Checked before tvfInsightsEntitledScopePairs parses it. */
    IF @AllowedBranches IS NOT NULL
       AND (   @AllowedBranches = ''
            OR @AllowedBranches COLLATE Latin1_General_BIN2 LIKE '%[^0-9,]%'   -- BIN2: see CLAUDE.md Sec.3 on LIKE
            OR @AllowedBranches LIKE ',%'
            OR @AllowedBranches LIKE '%,'
            OR @AllowedBranches LIKE '%,,%'
            OR @AllowedBranches LIKE '%[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%')
        THROW 51234, N'FREE MONTHLY - @AllowedBranches IS MALFORMED. Expected NULL or a comma-separated list of branch ids (digits and commas only, no blanks). Refusing to compute.', 1;

    IF DATEPART(DAY, @CurrMonthStart) <> 1
        THROW 51236, N'FREE MONTHLY - @CurrMonthStart IS NOT THE FIRST OF A MONTH. It must be the 1st of the edition month, computed from the edition in C#. Refusing to compute.', 1;

    DECLARE @CurrStart      DATETIME = CAST(@CurrMonthStart AS DATETIME);
    DECLARE @PrevMonthStart DATETIME = DATEADD(MONTH, -1, @CurrStart);
    DECLARE @NextMonthStart DATETIME = DATEADD(MONTH,  1, @CurrStart);

    IF @AsOf < @CurrStart OR @AsOf >= @NextMonthStart
        THROW 51237, N'FREE MONTHLY - @AsOf FALLS OUTSIDE THE EDITION MONTH. Most likely a clock mismatch (a UTC @AsOf against a local-time month). Pass @AsOf in the same clock as ComplianceScheduleOn.ScheduleOn. Refusing to compute.', 1;

    /*===================================================================
      1. SCOPE + DICTIONARY PRE-FLIGHT
    ===================================================================*/
    /*  The entitled branches, materialised once. Every scoped read below joins
        this, so the entitlement filter is applied in exactly one place.     */
    IF OBJECT_ID('tempdb..#lf_branch') IS NOT NULL DROP TABLE #lf_branch;
    SELECT DISTINCT ep.BranchID
    INTO #lf_branch
    FROM dbo.tvfInsightsEntitledScopePairs(@UserID, @CustomerID, @AllowedBranches) ep;
    CREATE CLUSTERED INDEX IX_lf_branch ON #lf_branch (BranchID);

    IF NOT EXISTS (SELECT 1 FROM #lf_branch)
        THROW 51230, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant (after the entitlement filter, when one is passed). Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- THROWs 51001 on a dictionary gap; returns no grid

    IF OBJECT_ID('tempdb..#lf_risk') IS NOT NULL DROP TABLE #lf_risk;
    SELECT TRY_CAST(p.RawValue AS INT) AS RawValue, p.Meaning
    INTO #lf_risk
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'RiskType'
      AND TRY_CAST(p.RawValue AS INT) IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM #lf_risk WHERE Meaning = N'Critical')
        THROW 51239, N'DICTIONARY GAP - InsightsEnumPolarity has no current RiskType row meaning Critical. The monthly tier never compares RiskType to a literal; seed the BA-verified mapping and re-run. Refusing to compute.', 1;

    /*===================================================================
      2. SCOPED INSTANCES - materialised FIRST (scope-first, then reach)
    ===================================================================*/
    INSERT #inst (ComplianceInstanceID, BranchID, CategoryId, ActID, ComplianceID,
                  DepartmentID, Imprisonment, RiskType, RiskClass)
    SELECT s.ComplianceInstanceID,
           s.BranchID,
           s.CategoryId,
           s.ActID,
           s.ComplianceID,
           ci.DepartmentID,
           CAST(ISNULL(s.Imprisonment, 0) AS BIT),
           s.RiskType,
           r.Meaning
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
    JOIN #lf_branch eb         ON eb.BranchID = s.BranchID     -- entitlement filter; a no-op when @AllowedBranches IS NULL
    JOIN ComplianceInstance ci ON ci.ID = s.ComplianceInstanceID
    LEFT JOIN #lf_risk r       ON r.RawValue = s.RiskType;

    CREATE NONCLUSTERED INDEX IX_inst_branch ON #inst (BranchID) INCLUDE (ActID, Imprisonment, RiskClass);

    /*  Instance-level owner fallback. MIN(UserID) = deterministic pick when an
        instance has several assignees. UserID > 0 excludes placeholder rows. */
    IF OBJECT_ID('tempdb..#lf_owner') IS NOT NULL DROP TABLE #lf_owner;
    SELECT ca.ComplianceInstanceID,
           MIN(CASE WHEN ca.RoleID = 3 THEN ca.UserID END) AS PerformerID,
           MIN(CASE WHEN ca.RoleID = 4 THEN ca.UserID END) AS ReviewerID
    INTO #lf_owner
    FROM #inst i
    JOIN ComplianceAssignment ca ON ca.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE ca.UserID > 0
      AND ca.RoleID IN (3, 4)      -- 3 = performer, 4 = reviewer (role ids, not statuses)
    GROUP BY ca.ComplianceInstanceID;

    CREATE CLUSTERED INDEX IX_lf_owner ON #lf_owner (ComplianceInstanceID);

    /*===================================================================
      3. ONE READ: window schedules + overdue stock, latest status resolved once
         [PERF] explicit TOP 1 seek on IX_CT_CSO_Dated_ID - never the view.
         [PERF] no join hints anywhere (CLAUDE.md Sec.5, 877 ms -> 26,492 ms).
    ===================================================================*/
    INSERT #sched (ComplianceScheduleOnID, ComplianceInstanceID, BranchID, ScheduleOn,
                   WindowPart, PerformerID, PerformerSource, ReviewerID, ReviewerSource,
                   NeverTouched, Outcome, IsOverdue, DaysPastDue, AgeBand)
    SELECT
        cso.ID,
        i.ComplianceInstanceID,
        i.BranchID,
        cso.ScheduleOn,
        CASE WHEN cso.ScheduleOn >= @PrevMonthStart AND cso.ScheduleOn < @CurrStart THEN 'prev_month'
             WHEN cso.ScheduleOn >= @CurrStart      AND cso.ScheduleOn <= @AsOf    THEN 'curr_elapsed'
             WHEN cso.ScheduleOn >  @AsOf           AND cso.ScheduleOn < @NextMonthStart THEN 'curr_remaining'
             ELSE NULL END,
        CASE WHEN cso.Performerid IS NOT NULL AND cso.Performerid <> 0 THEN cso.Performerid
             ELSE o.PerformerID END,
        CASE WHEN cso.Performerid IS NOT NULL AND cso.Performerid <> 0 THEN 'schedule'
             WHEN o.PerformerID IS NOT NULL                          THEN 'instance'
             ELSE 'none' END,
        CASE WHEN cso.Reviewerid IS NOT NULL AND cso.Reviewerid <> 0 THEN cso.Reviewerid
             ELSE o.ReviewerID END,
        CASE WHEN cso.Reviewerid IS NOT NULL AND cso.Reviewerid <> 0 THEN 'schedule'
             WHEN o.ReviewerID IS NOT NULL                           THEN 'instance'
             ELSE 'none' END,
        CAST(CASE WHEN lt.ID IS NULL THEN 1 ELSE 0 END AS BIT),
        CASE WHEN lt.ID IS NULL                                               THEN 'open'
             WHEN d.StatusId IS NULL                                          THEN 'unclassified'
             WHEN d.ClosureClass = 'completed' AND d.Timeliness = 'on_time'   THEN 'completed_on_time'
             WHEN d.ClosureClass = 'completed' AND d.Timeliness = 'delayed'   THEN 'completed_late'
             WHEN d.ClosureClass = 'completed'                                THEN 'completed_untimed'
             WHEN d.ClosureClass = 'resolved_terminal'                        THEN 'resolved_terminal'
             WHEN d.ClosureClass = 'open'                                     THEN 'open'
             ELSE 'unclassified' END,
        CAST(CASE WHEN cso.ScheduleOn <= @AsOf
                   AND (d.OverdueEligible = 1 OR lt.ID IS NULL) THEN 1 ELSE 0 END AS BIT),
        CASE WHEN cso.ScheduleOn <= @AsOf AND (d.OverdueEligible = 1 OR lt.ID IS NULL)
             THEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) END,
        CASE WHEN cso.ScheduleOn <= @AsOf AND (d.OverdueEligible = 1 OR lt.ID IS NULL)
             THEN CASE WHEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) <= 30 THEN 'd000_030'
                       WHEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) <= 60 THEN 'd031_060'
                       WHEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) <= 90 THEN 'd061_090'
                       ELSE 'd091_plus' END END
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    OUTER APPLY (SELECT TOP 1 t.StatusId, t.ID
                 FROM ComplianceTransaction t
                 WHERE t.ComplianceScheduleOnID = cso.ID
                 ORDER BY t.Dated DESC, t.ID DESC) lt
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = lt.StatusId
    LEFT JOIN #lf_owner o                  ON o.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.IsActive = 1
      AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn < @NextMonthStart
      AND (    cso.ScheduleOn >= @PrevMonthStart
           OR (cso.ScheduleOn <= @AsOf AND (d.OverdueEligible = 1 OR lt.ID IS NULL)) );

    CREATE NONCLUSTERED INDEX IX_sched_window ON #sched (WindowPart, Outcome) INCLUDE (ComplianceInstanceID, BranchID);
    CREATE NONCLUSTERED INDEX IX_sched_overdue ON #sched (IsOverdue, AgeBand) INCLUDE (ComplianceInstanceID, BranchID, PerformerID);
    CREATE NONCLUSTERED INDEX IX_sched_perf ON #sched (PerformerID) INCLUDE (WindowPart, Outcome, IsOverdue);

    /*===================================================================
      4. STRUCTURAL INVARIANTS - these hold REGARDLESS of data, so they
         THROW (CLAUDE.md Sec.11). A failure means this CODE is wrong.
    ===================================================================*/
    IF EXISTS (SELECT 1 FROM #sched WHERE Outcome = 'unclassified')
        THROW 51238, N'DICTIONARY GAP - a scoped schedule has a latest status the dictionary cannot place in a closure class (status id absent from vInsightsStatusCurrent, or an unknown ClosureClass). Refusing to guess.', 1;

    IF EXISTS (SELECT 1 FROM #sched WHERE WindowPart IS NULL AND IsOverdue = 0)
        THROW 51231, N'FREE MONTHLY RECONCILIATION FAILED - a row outside the window is not overdue. Rows outside the window may exist only as overdue stock; the load predicate has drifted. Refusing to publish.', 1;

    IF EXISTS (SELECT 1 FROM #sched WHERE WindowPart = 'curr_remaining' AND IsOverdue = 1)
        THROW 51232, N'FREE MONTHLY RECONCILIATION FAILED - a schedule due AFTER @AsOf is marked overdue. The never-touched-is-overdue ruling applies to PAST-DUE schedules only. Refusing to publish.', 1;

    IF EXISTS (SELECT 1 FROM #sched
               WHERE (IsOverdue = 1 AND (DaysPastDue IS NULL OR AgeBand IS NULL))
                  OR (IsOverdue = 0 AND (DaysPastDue IS NOT NULL OR AgeBand IS NOT NULL)))
        THROW 51233, N'FREE MONTHLY RECONCILIATION FAILED - overdue age fields are inconsistent with IsOverdue. Refusing to publish.', 1;

    DROP TABLE #lf_owner;
    DROP TABLE #lf_risk;
    DROP TABLE #lf_branch;
END
GO
PRINT 'usp_Insights_FreeMonthly_LoadFacts restored (pre-v2).';
GO
-------------------------------------------------------------------------- usp_Insights_GoldenInvariants
IF OBJECT_ID('dbo.usp_Insights_GoldenInvariants', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_GoldenInvariants;
GO
CREATE PROCEDURE dbo.usp_Insights_GoldenInvariants
    @CustomerID INT,
    @AsOf       DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*  SCOPE OF THIS SUITE: STRUCTURAL INVARIANTS ONLY.

        Every assertion here must hold REGARDLESS of what the data contains -
        they are algebra and graph traversal, not observations about a
        particular dataset. That is why they can THROW: a failure means the
        CODE is wrong.

        Data-sanity checks (e.g. "imprisonment items should concentrate on
        RiskType 3") do NOT belong here. They can legitimately fail on
        legitimate data - a test environment with arbitrary values will fail
        them forever, and a permanently-red suite gets disabled, which costs
        far more than the check was worth. Those live in
        usp_Insights_StatusDataQuality and WARN.                              */

    DECLARE @results TABLE (TestId VARCHAR(10), TestName NVARCHAR(140),
                            Passed BIT, Detail NVARCHAR(400));

    /*-- G-1  dictionary coverage ------------------------------------------*/
    DECLARE @unmapped INT = (SELECT COUNT(*) FROM ComplianceStatus cs
        WHERE NOT EXISTS (SELECT 1 FROM dbo.vInsightsStatusCurrent d WHERE d.StatusId=cs.ID));
    INSERT @results VALUES ('G-1', N'All ComplianceStatus values are mapped',
        CASE WHEN @unmapped=0 THEN 1 ELSE 0 END,
        CONCAT(N'Unmapped status count = ', @unmapped, N' (must be 0)'));

    /*-- G-2  overdue definition invariant ---------------------------------
        new = old - pastdue(7,9) - pastdue(17) + pastdue(18)
        Drift-proof: every term is measured in the same instant.            */
    /*  [PERF] Latest status is resolved ONCE into #latest and reused by G-2,
        G-3 and G-9. Three separate joins to RecentComplianceTransactionView
        timed out in production (45.7M-row non-indexed view; tenant filter not
        pushed through). The explicit function runs in ~0.6 s for 52K schedules. */
    IF OBJECT_ID('tempdb..#latest') IS NOT NULL DROP TABLE #latest;
    SELECT ls.ComplianceScheduleOnID, ls.ComplianceInstanceID, ls.StatusId, ls.ScheduleOn,
           ls.LatestTransactionId, d.OverdueEligible, d.ClosureClass
    INTO #latest
    FROM dbo.tvfInsightsLatestStatus(@CustomerID, @AsOf) ls
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = ls.StatusId;

    /*  Overdue now has two sources (see tvfInsightsOverdueSchedules):
          (a) overdue-eligible latest status   - the identity below holds for these
          (b) no transaction at all            - BA ruling; added as a separate term
        The identity is checked over (a) only, then (b) is added to both sides,
        so it stays an exact algebraic check rather than being loosened.       */
    DECLARE @old INT,@new INT,@c79 INT,@c17 INT,@c18 INT,@never INT;
    SELECT @old = SUM(CASE WHEN StatusId NOT IN (4,5,15,18) THEN 1 ELSE 0 END),
           @new = SUM(CASE WHEN OverdueEligible=1 THEN 1 ELSE 0 END),
           @c79 = SUM(CASE WHEN StatusId IN (7,9) THEN 1 ELSE 0 END),
           @c17 = SUM(CASE WHEN StatusId=17 THEN 1 ELSE 0 END),
           @c18 = SUM(CASE WHEN StatusId=18 THEN 1 ELSE 0 END)
    FROM #latest WHERE StatusId IS NOT NULL;
    SET @never = (SELECT COUNT(*) FROM #latest WHERE LatestTransactionId IS NULL);
    INSERT @results VALUES ('G-2', N'Overdue definition invariant (old to new reconciles)',
        CASE WHEN ISNULL(@new,0)=ISNULL(@old,0)-ISNULL(@c79,0)-ISNULL(@c17,0)+ISNULL(@c18,0) THEN 1 ELSE 0 END,
        CONCAT(N'new=',ISNULL(@new,0),N' old=',ISNULL(@old,0),N' pastdue(7,9)=',ISNULL(@c79,0),
               N' pastdue(17)=',ISNULL(@c17,0),N' pastdue(18)=',ISNULL(@c18,0),
               N' | plus ',ISNULL(@never,0),N' never-touched schedules also overdue (BA ruling)'));

    /*-- G-3  completed items are never overdue ----------------------------*/
    DECLARE @compOvd INT = (SELECT COUNT(*) FROM #latest WHERE OverdueEligible=1 AND ClosureClass='completed');
    INSERT @results VALUES ('G-3', N'No completed item is counted overdue',
        CASE WHEN @compOvd=0 THEN 1 ELSE 0 END,
        CONCAT(N'Completed-but-overdue rows = ',@compOvd,N' (must be 0)'));

    /*-- G-4  resolved_terminal carries no timeliness ----------------------*/
    DECLARE @termTime INT = (SELECT COUNT(*) FROM dbo.vInsightsStatusCurrent
        WHERE ClosureClass='resolved_terminal' AND Timeliness IS NOT NULL);
    INSERT @results VALUES ('G-4', N'resolved_terminal carries no timeliness',
        CASE WHEN @termTime=0 THEN 1 ELSE 0 END,
        CONCAT(N'resolved_terminal rows with timeliness = ',@termTime,N' (must be 0)'));

    /*-- G-5  recursive rollup ties to the control total -------------------
        [FIX - found in UAT] The control total MUST use the same estate
        definition as the dimension procedures, which exclude instances whose
        Compliance master is soft-deleted. Without the c.IsDeleted filter this
        reported 4,884 against a dimension total of 4,814 on a real tenant - a
        70-instance disagreement between two parts of the same system, with
        each side reconciling internally, which is what made it invisible.  */
    DECLARE @control INT,@rollup INT;
    SELECT @control=COUNT(*)
    FROM ComplianceInstance i
    JOIN CustomerBranch cb ON cb.ID=i.CustomerBranchID
    JOIN Compliance c ON c.ID=i.ComplianceID
    WHERE cb.CustomerID=@CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1 AND i.IsDeleted=0 AND c.IsDeleted=0;

    ;WITH tree AS (SELECT BranchID FROM dbo.tvfInsightsEntityTree(@CustomerID))
    SELECT @rollup=COUNT(i.ID)
    FROM tree
    LEFT JOIN ComplianceInstance i ON i.CustomerBranchID=tree.BranchID AND i.IsDeleted=0
    LEFT JOIN Compliance c ON c.ID=i.ComplianceID AND c.IsDeleted=0
    WHERE i.ID IS NULL OR c.ID IS NOT NULL;

    INSERT @results VALUES ('G-5', N'Recursive rollup ties to tenant control total',
        CASE WHEN ISNULL(@control,0)=ISNULL(@rollup,0) THEN 1 ELSE 0 END,
        CONCAT(N'control=',ISNULL(@control,0),N' rollup=',ISNULL(@rollup,0),
               N' gap=',ISNULL(@control,0)-ISNULL(@rollup,0),
               N' (a gap means a node was dropped)'));

    /*-- G-6 REMOVED - moved to usp_Insights_StatusDataQuality as a WARNING.
        It asserted that imprisonment items concentrate on RiskType 3. That is
        TRUE of production (98.7% across 528 tenants) but is a property of the
        DATA, not the code. A test environment with arbitrary values inverted
        it completely (96.8% on RiskType 0) and turned this whole suite red
        permanently - which is how regression suites get switched off.      */

    /*-- G-7  scope is two-dimensional ------------------------------------*/
    DECLARE @scopeRows INT,@scopeNoCat INT;
    SELECT @scopeRows=COUNT(*),
           @scopeNoCat=SUM(CASE WHEN ea.ComplianceCatagoryID IS NULL OR ea.ComplianceCatagoryID=0 THEN 1 ELSE 0 END)
    FROM EntitiesAssignment ea JOIN CustomerBranch cb ON cb.ID=ea.BranchID
    WHERE cb.CustomerID=@CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1;
    INSERT @results VALUES ('G-7', N'All scope rows are category-specific (2-D)',
        CASE WHEN ISNULL(@scopeRows,0)=0 OR ISNULL(@scopeNoCat,0)=0 THEN 1 ELSE 0 END,
        CONCAT(N'scope rows=',ISNULL(@scopeRows,0),N' without category=',ISNULL(@scopeNoCat,0)));

    /*-- G-8  2-D scope never returns MORE than branch-only ----------------*/
    DECLARE @s2d INT,@sbr INT;
    ;WITH pairs AS (
        SELECT DISTINCT ea.BranchID, ea.ComplianceCatagoryID AS CategoryId
        FROM EntitiesAssignment ea JOIN CustomerBranch cb ON cb.ID=ea.BranchID
        WHERE cb.CustomerID=@CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1),
    inst AS (
        SELECT i.ID, i.CustomerBranchID AS BranchID, a.ComplianceCategoryId AS CategoryId
        FROM ComplianceInstance i
        JOIN CustomerBranch cb ON cb.ID=i.CustomerBranchID AND cb.IsDeleted = 0 AND cb.Status = 1
        JOIN Compliance c ON c.ID=i.ComplianceID AND c.IsDeleted=0
        JOIN Act a ON a.ID=c.ActID
        WHERE cb.CustomerID=@CustomerID AND i.IsDeleted=0)
    SELECT @s2d=(SELECT COUNT(DISTINCT i.ID) FROM pairs p JOIN inst i
                   ON i.BranchID=p.BranchID AND i.CategoryId=p.CategoryId),
           @sbr=(SELECT COUNT(DISTINCT i.ID) FROM (SELECT DISTINCT BranchID FROM pairs) p
                   JOIN inst i ON i.BranchID=p.BranchID);
    INSERT @results VALUES ('G-8', N'2-D scope never returns more than branch-only',
        CASE WHEN ISNULL(@s2d,0)<=ISNULL(@sbr,0) THEN 1 ELSE 0 END,
        CONCAT(N'2-D=',ISNULL(@s2d,0),N' branch-only=',ISNULL(@sbr,0),
               N' | branch-only would leak ',ISNULL(@sbr,0)-ISNULL(@s2d,0),N' instances'));

    /*-- G-9  unknown-status volume is immaterial --------------------------*/
    /*  G-9 measures UNKNOWN status among schedules that HAVE a transaction. A
        schedule with no transaction at all is an absence, not an unknown, and
        is declared by usp_Insights_StatusDataQuality instead. Conflating the
        two made this invariant fail on 115 bulk-created, never-touched
        schedules that the old view had silently hidden.                     */
    DECLARE @pdAll INT,@nullSt INT;
    SELECT @pdAll=COUNT(*), @nullSt=SUM(CASE WHEN StatusId IS NULL THEN 1 ELSE 0 END)
    FROM #latest WHERE LatestTransactionId IS NOT NULL;
    INSERT @results VALUES ('G-9', N'Unknown-status volume is immaterial and declared',
        CASE WHEN ISNULL(@pdAll,0)=0 OR ISNULL(@nullSt,0)*100.0/@pdAll<=0.10 THEN 1 ELSE 0 END,
        CONCAT(N'NULL-status past-due rows = ',ISNULL(@nullSt,0),N' of ',ISNULL(@pdAll,0)));

    DROP TABLE #latest;

    SELECT TestId, TestName,
           CASE WHEN Passed=1 THEN 'PASS' ELSE '*** FAIL ***' END AS Result, Detail
    FROM @results ORDER BY TestId;

    IF EXISTS (SELECT 1 FROM @results WHERE Passed=0)
        THROW 51002, N'GOLDEN REGRESSION FAILED - do not ship. See result set for the failing assertion.', 1;
END
GO
PRINT 'usp_Insights_GoldenInvariants restored (pre-v2).';
GO
