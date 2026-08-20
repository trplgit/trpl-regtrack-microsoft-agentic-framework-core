/*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
  RegTrack Insights â€” Phase 1b, Step 1
  LOCATION DIMENSION

  Spec reference : RegTrack_Insights_System_Design_v1.md Â§7.3 (worked exemplar)
  Purpose        : The reference implementation. Every other dimension follows
                   this pattern â€” rows, control totals, typed assertions with
                   COMPUTED comparatives, findings backed by assertion ids,
                   and declared data quality.

  â”€â”€ CONTRACT â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  Emits FIVE result sets, which the .NET layer serialises into the dimension
  JSON of Â§7.2. SQL computes; .NET assembles and adds provenance.

    1. control_totals   â€” MUST reconcile or the proc THROWs
    2. rows             â€” one per branch (leaf AND intermediate)
    3. assertions       â€” typed facts INCLUDING comparatives
    4. findings         â€” each backed by assertion ids
    5. data_quality     â€” declared gaps

  â”€â”€ WHY ASSERTIONS ARE COMPUTED HERE, NOT PHRASED LATER (spec Â§6.10) â”€â”€â”€â”€â”€â”€â”€â”€
  The narrative agent may only assert what exists in the assertion set. So
  "worst location" is sayable ONLY because rank = 1 is computed below. This is
  what makes the claim-checker complete by construction rather than best-effort.

  â”€â”€ SCOPE â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  All queries are constrained by the 2-D (branch Ã— category) scope pair set.
  Branch-only scoping would leak across categories WITHIN a tenant â€” measured
  at up to 119,797 instances on one production tenant.

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/

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

    /*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
      0. PRE-FLIGHT â€” fail closed before computing anything
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51030, N'SCOPE DENIED â€” user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- THROWs on dictionary gap

    /*  The RiskType value meaning Critical comes from the DICTIONARY, never from a
        literal. `RiskType = 3` in a WHERE clause is an enum literal - non-negotiable
        #4 - and if the mapping is ever wrong or re-ruled, a literal makes this proc
        silently report the wrong critical count with no error. Fail closed if the
        dictionary cannot supply it.                                                 */
    DECLARE @criticalRisk INT = (
        SELECT TRY_CAST(p.RawValue AS INT)
        FROM dbo.InsightsEnumPolarity p
        JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
        WHERE p.Semantic = 'RiskType' AND p.Meaning LIKE N'Critical%');

    IF @criticalRisk IS NULL
        THROW 51032, N'DICTIONARY GAP - no RiskType value is mapped to Critical in InsightsEnumPolarity. Refusing to compute a critical-risk count.', 1;

    /*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
      1. SCOPED INSTANCE BASE
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/
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

    /*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
      2. OVERDUE (dictionary-driven, affirmative form)
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/
    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;   -- scope-constrained

    /*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
      3. ASSIGNMENT + LIFETIME CLOSURE FACTS
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/
    IF OBJECT_ID('tempdb..#owned') IS NOT NULL DROP TABLE #owned;
    SELECT DISTINCT ca.ComplianceInstanceID
    INTO #owned
    FROM ComplianceAssignment ca
    JOIN #inst i ON i.ComplianceInstanceID = ca.ComplianceInstanceID
    WHERE ca.RoleID = 3 AND ca.UserID > 0;      -- RoleID 3 = performer

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

    /*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
      4. PER-BRANCH ROWS  (leaf AND intermediate â€” never leaf-only)

      [TRAP] BUILD FROM THE BRANCH LIST, NOT FROM THE INSTANCES.
      Driving this from #inst silently DROPS every branch with zero
      obligations â€” and one of those cases is a genuine finding:

        â€¢ grouping / holding nodes with 0 instances  â†’ normal, expected
        â€¢ a LEAF with 0 children AND 0 instances     â†’ GHOST ENTITY:
          configured in the structure with nothing tracked against it

      On the reference tenant this hid 4 of 16 active branches, one of which
      (a childless leaf) is exactly the coverage blind spot the engine exists
      to surface. Reconciliation still passes either way, because zeros add
      nothing to the sum â€” which is precisely why this defect is invisible
      without an explicit branch-count check.
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/
    /*  [TRAP] Do NOT use `SELECT ... INTO #rows` followed by
        `ALTER TABLE #rows ADD <col>` and then reference <col> in the SAME
        procedure body. T-SQL resolves names for the whole batch up front, so
        the later UPDATE fails at runtime with "Invalid column name". Inside a
        procedure you cannot insert a GO to split the batch.

        Declare the table explicitly with ALL columns â€” base and derived â€”
        then INSERT, then UPDATE the derived ones.                            */
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        BranchID              INT            NOT NULL PRIMARY KEY,
        BranchName            NVARCHAR(300)  NULL,
        NodeType              VARCHAR(20)    NULL,
        RootKind              VARCHAR(20)    NULL,
        ApexName              NVARCHAR(400)  NULL,
        Instances             INT            NOT NULL,
        Overdue               INT            NOT NULL,
        Ownerless             INT            NOT NULL,
        ImprisonmentInstances INT            NOT NULL,
        ImprisonmentOverdue   INT            NOT NULL,
        CriticalInstances     INT            NOT NULL,
        DistinctPerformers    INT            NOT NULL,
        DistinctReviewers     INT            NOT NULL,
        ClosureEventsLifetime INT            NOT NULL,
        ActiveChildren        INT            NOT NULL,
        -- derived, populated below
        OverduePct            DECIMAL(5,1)   NULL,
        OwnerlessPct          DECIMAL(5,1)   NULL,
        ClosureRatio          DECIMAL(9,2)   NULL,
        OverdueRank           INT            NULL,
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (BranchID, BranchName, NodeType, RootKind, ApexName, Instances, Overdue,
                  Ownerless, ImprisonmentInstances, ImprisonmentOverdue, CriticalInstances,
                  DistinctPerformers, DistinctReviewers, ClosureEventsLifetime, ActiveChildren)
    SELECT
        cb.ID, cb.Name, t.NodeType, t.RootKind, t.ApexName,
        COUNT(i.ComplianceInstanceID),
        SUM(CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.ComplianceInstanceID IS NOT NULL
                  AND w.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 AND o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.RiskType = @criticalRisk THEN 1 ELSE 0 END),
        ISNULL(MAX(p.Performers), 0),
        ISNULL(MAX(p.Reviewers), 0),
        ISNULL(MAX(cl.ClosureEventsLifetime), 0),
        (SELECT COUNT(*) FROM CustomerBranch ch WHERE ch.ParentID = cb.ID AND ch.IsDeleted = 0)
    FROM CustomerBranch cb
    JOIN dbo.tvfInsightsEntityTree(@CustomerID) t ON t.BranchID = cb.ID
    LEFT JOIN #inst     i  ON i.BranchID = cb.ID
    LEFT JOIN #ovd      o  ON o.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN #owned    w  ON w.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN #people   p  ON p.BranchID = cb.ID
    LEFT JOIN #closures cl ON cl.BranchID = cb.ID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0
    GROUP BY cb.ID, cb.Name, t.NodeType, t.RootKind, t.ApexName;

    -- derived ratios + rank (comparatives are COMPUTED, never phrased)
    UPDATE #rows SET
        OverduePct   = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue   / Instances END,
        OwnerlessPct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Ownerless / Instances END,
        ClosureRatio = CASE WHEN Instances = 0 THEN 0 ELSE 1.0 * ClosureEventsLifetime / Instances END;

    /*  [TRAP] RANKING WITHOUT A MATERIALITY FLOOR YIELDS A TRUE BUT USELESS HERO.
        A branch holding one overdue obligation scores 100% and outranks one
        holding 900 of 1,000 at 90%. The narrative contract then LICENSES the
        claim - rank 1 exists, so "the highest overdue rate" passes the
        claim-checker, the publish gate and reflection - and the report leads on
        noise. Verified number, false story: pre-mortem D7, the exact death all
        the assertion machinery exists to prevent.

        Rank only members at or above Detectors:MaterialityFloorInstances. If
        fewer than two qualify, fall back to every member holding anything and
        DECLARE the fallback on the assertion - the same adaptive-sample pattern
        the closure-ratio peer baseline already uses below.                     */
    DECLARE @materialityFloor INT = 50;                       -- Detectors:MaterialityFloorInstances
    DECLARE @materialMembers  INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @materialityFloor);
    DECLARE @rankDegraded     BIT = CASE WHEN @materialMembers < 2 THEN 1 ELSE 0 END;
    DECLARE @rankFloor        INT = CASE WHEN @rankDegraded = 1 THEN 1 ELSE @materialityFloor END;

    ;WITH r AS (SELECT BranchID, RANK() OVER (ORDER BY OverduePct DESC) AS rk
                FROM #rows WHERE Instances >= @rankFloor)     -- immaterial members are not ranked
    UPDATE #rows SET OverdueRank = r.rk FROM #rows JOIN r ON r.BranchID = #rows.BranchID;

    /*â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      DETECTIONS

      [TRAP] "0% overdue" is NOT automatically excellence. A site with many
      configured obligations but almost no lifetime closure events has not
      begun operating the platform â€” an ONBOARDING ARTIFACT, a false star.

      â”€â”€ THE DETECTOR MUST BE PEER-RELATIVE, NOT ABSOLUTE â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      The obvious rule (ratio < 1.0 lifetime closures per instance) works on
      one tenant and fails badly on others, because the baseline ratio depends
      on platform tenure and on the tenant's compliance frequency mix.

      Measured with the ABSOLUTE rule:
          tenant A: median ratio 7.90 â†’   8% of branches flagged   (correct)
          tenant B: median ratio 1.34 â†’  57% of branches flagged   (absurd)
          tenant C: median ratio 2.26 â†’  60% of branches flagged   (absurd)
      A tenant cannot have 60% of its sites be onboarding artifacts.

      TWO-TIER RULE (implemented below):
        Tier 1 â€” if the TENANT's own median ratio < 1.0, the whole tenant is
                 newly onboarded. Per-branch artifact flags are meaningless;
                 emit a TENANT-LEVEL data_quality note instead and suppress
                 the per-branch flag.
        Tier 2 â€” otherwise flag a branch whose ratio is below 20% of the
                 tenant median, i.e. dramatically behind its own peers.

      Verified across 7 tenants: flag rates 8â€“19%, and the known artifact site
      on the reference tenant is still correctly and solely identified.
    â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€*/
    /*â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      [TRAP] THE MEDIAN SAMPLE CAN BE EMPTY.

      The natural sample is "branches with >= 50 instances". On a production
      tenant with 446 branches and 2,685 instances, the LARGEST branch held
      34 â€” so the sample was EMPTY, PERCENTILE_CONT returned NULL, ISNULL
      forced it to 0, and the tenant was declared "newly onboarded".

      That verdict happened to be right (its true average ratio was 0.11) but
      it was reached from an empty sample rather than from measurement. A
      tenant with hundreds of SMALL but HEALTHY branches would be misclassified
      identically â€” a false negative waiting to happen.

      FIX: adaptive sample. Prefer branches >= 50 instances; if none qualify,
      fall back to every branch that has any instances, and record that the
      sample was degraded so the narrative can qualify the claim.
    â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€*/
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

    -- No branch with instances at all â‡’ nothing to assess; do not claim onboarding.
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
            /*  [TRAP] SPOF IS ONLY MEANINGFUL WHERE THERE IS WORK TO DO.
                Without the Instances > 0 guard this fires on every EMPTY branch
                (0 performers satisfies <= 1), so Flagged counts all branches while
                Eligible counts only branches with obligations - producing 191 of 109
                = 175.2%, an impossible rate. Same defect class as the per-user
                concentration sum that produced 155% (spec 6.8). A location with no
                obligations cannot depend on a single person; there is nothing to do. */
            CASE WHEN Instances > 0
                  AND (DistinctPerformers <= 1 OR DistinctReviewers <= 1)
                 THEN ',single_point_of_failure' ELSE '' END +
            CASE WHEN NodeType = 'intermediate' THEN ',instances_on_intermediate_node' ELSE '' END +
            CASE WHEN RootKind = 'orphan' THEN ',orphaned_parent_deleted' ELSE '' END +
            CASE WHEN OwnerlessPct >= 10.0 THEN ',high_ownerless' ELSE '' END +
            -- GHOST ENTITY: a leaf with no children and nothing tracked against it.
            -- Distinct from a grouping/holding node, which legitimately holds 0.
            CASE WHEN Instances = 0 AND ActiveChildren = 0 THEN ',no_obligations_configured' ELSE '' END +
            CASE WHEN Instances = 0 AND ActiveChildren > 0 THEN ',grouping_node' ELSE '' END
        , 1, 1, '');

    /*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
      5. CONTROL TOTALS + MANDATORY RECONCILIATION
         (@medianRatio / @tenantIsOnboarding are set in the detection block above)
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/
    DECLARE @rowSum INT       = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @scopedTotal INT  = (SELECT COUNT(*) FROM #inst);

    IF @rowSum <> @scopedTotal
        THROW 51031, N'LOCATION DIMENSION RECONCILIATION FAILED â€” per-branch sums do not tie to the scoped instance total. Refusing to publish.', 1;

    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT COUNT(*) FROM #ovd) / @scopedTotal END;

    SELECT
        'control_totals'                       AS ResultSet,
        @scopedTotal                           AS ScopedInstances,
        @rowSum                                AS SumOfRows,
        CAST(1 AS BIT)                         AS Reconciled,
        (SELECT COUNT(*) FROM #ovd)            AS OverdueInstances,
        @tenantOverduePct                      AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows)           AS BranchesReported,
        (SELECT COUNT(*) FROM CustomerBranch
          WHERE CustomerID = @CustomerID AND IsDeleted = 0) AS ActiveBranchesInTenant,
        (SELECT COUNT(*) FROM #rows WHERE Instances = 0)    AS BranchesWithNoObligations,
        (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%no_obligations_configured%') AS GhostEntities,
        @medianRatio                           AS TenantMedianClosureRatio,
        @tenantIsOnboarding                    AS TenantIsOnboarding;

    /*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
      6. ROWS
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/
    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
      6b. DETECTOR EMISSION POLICY  â˜… applies to EVERY dimension â˜…

      â”€â”€ THE SYSTEMIC LESSON â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      Every absolute-threshold detector built for this dimension produced
      sane results on the tenant it was designed against and absurd results
      elsewhere. Measured flag rates across 8 production tenants:

         detector            best tenant     worst tenant
         onboarding_artifact      8%              60%
         ghost_entity             1.3%            66%
         single_point_of_failure  4.6%            84%
         high_ownerless           1.2%            76%

      A finding that fires on 84% of a tenant's locations is not a finding â€”
      it is a description of how that tenant operates, and emitting 32
      separate high-severity items is noise a CCO cannot act on.

      â”€â”€ THE POLICY â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      For every detector, compare flagged members against eligible members:

        flagged_pct >  20%  â†’  EMIT ONE AGGREGATE finding (severity medium)
                               "N of M locations (X%) show <pattern>"
                               and SUPPRESS the individual findings.

        flagged_pct <= 20%  â†’  EMIT INDIVIDUAL findings, capped at the top 5
                               by materiality (instance count), so a large
                               tenant with a low rate still cannot flood the
                               report.

      This makes findings scale-invariant: the same code produces 1 finding
      for a 16-branch tenant and 1 aggregate for an 819-branch tenant, and
      never 117 items.
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/
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
    UNION ALL SELECT 'high_ownerless', @withObl,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%high_ownerless%');

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

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
      7. TYPED ASSERTIONS  (comparatives COMPUTED here â€” spec Â§6.10)
         Emission governed by #detector.EmitMode above.
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/
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

    DECLARE @branchCount INT = (SELECT COUNT(*) FROM #rows);

    -- A-TENANT: the baseline every comparative is measured against
    INSERT #assert VALUES ('A-TENANT','overdue_pct',N'tenant',@tenantOverduePct,NULL,NULL,NULL,NULL,NULL,NULL);

    /*  [TRAP] A "worst location" claim needs something to compare against.
        Production has many SINGLE-BRANCH tenants; rank 1 of 1 is vacuous and
        would read as criticism of the only site they have. Require >= 2
        rankable members before emitting any comparative.                    */
    DECLARE @rankable INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor);

    /*  Ties are the other half of the problem. RANK() gives every member at the
        same rate rank 1, so "the highest" can be true of twenty branches at once.
        The caveat travels WITH the value - the prompt contract forbids citing a
        value without its caveat - so the narrative cannot imply uniqueness.     */
    DECLARE @tiedAtTop INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor AND OverdueRank = 1);

    -- worst location by overdue rate (suppressed when there is nothing to compare)
    IF @rankable >= 2
    INSERT #assert
    SELECT TOP 1 'A-WORST','overdue_pct',BranchName,OverduePct,OverdueRank,@rankable,
           @tenantOverduePct, OverduePct - @tenantOverduePct,
           CASE WHEN OverduePct > @tenantOverduePct THEN 'worse' ELSE 'better' END,
           NULLIF(CONCAT(
               CASE WHEN @rankDegraded = 1
                    THEN CONCAT(N'degraded_ranking_sample: no member reaches the ', @materialityFloor,
                                N'-instance materiality floor, so ranking is across all members holding obligations. ')
                    ELSE N'' END,
               CASE WHEN @tiedAtTop > 1
                    THEN CONCAT(N'tied_at_top: ', @tiedAtTop, N' members share this rate - it is not uniquely the highest. ')
                    ELSE N'' END), N'')
    FROM #rows WHERE Instances >= @rankFloor ORDER BY OverduePct DESC, Instances DESC;

    -- onboarding artifacts: value is the closure RATIO, with the guard caveat
    INSERT #assert
    SELECT 'A-ONB-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
           'closure_ratio', BranchName, ClosureRatio, NULL, NULL,
           NULL, NULL, NULL,
           N'onboarding_artifact: not a top performer â€” site has not begun operating'
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
               N'aggregate â€” thin staffing is a tenant-wide pattern, not per-site exceptions'
        FROM #detector WHERE Detector='single_point_of_failure';

    -- ownerless concentration vs tenant rate
    DECLARE @tenantOwnerlessPct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT ISNULL(SUM(Ownerless),0) FROM #rows) / @scopedTotal END;

    IF (SELECT EmitMode FROM #detector WHERE Detector='high_ownerless') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-OWN-' + CAST(ROW_NUMBER() OVER (ORDER BY Ownerless DESC) AS VARCHAR(5)),
               'ownerless_pct', BranchName, OwnerlessPct, NULL, NULL,
               @tenantOwnerlessPct, OwnerlessPct - @tenantOwnerlessPct, 'worse', NULL
        FROM #rows WHERE Flags LIKE '%high_ownerless%' ORDER BY Ownerless DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='high_ownerless') = 'aggregate'
        INSERT #assert
        SELECT 'A-OWN-AGG','locations_high_ownerless',N'tenant',
               Flagged, NULL, Eligible, @tenantOwnerlessPct, FlaggedPct, 'worse',
               N'aggregate â€” unassigned ownership is a tenant-wide pattern'
        FROM #detector WHERE Detector='high_ownerless';

    /*â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
      GHOST ENTITIES â€” two-tier, for the same reason as the onboarding
      detector: what is a per-site finding on one tenant is a structural
      pattern on another.

      Measured across 8 production tenants, ghost entities as a share of
      active branches ranged from 1.3% to 66%:
          1490:   1 of 16  ( 6%)   â†’ individually meaningful
          1832:   2 of 151 ( 1.3%) â†’ individually meaningful
          1472:  91 of 819 (11%)   â†’ a pattern
            29: 117 of 176 (66%)   â†’ a pattern
             5: 147 of 264 (56%)   â†’ a pattern
          1308: 181 of 331 (55%)   â†’ a pattern

      Emitting 117 separate high-severity findings is noise no CCO can use.
      When empty locations are the MAJORITY pattern, the tenant is very
      likely maintaining a location master (HR/ERP import) of which only a
      subset is compliance-relevant â€” one aggregate statement, not N findings.

      RULE: <= 5 ghosts AND < 15% of branches â†’ individual findings (high)
            otherwise                          â†’ ONE aggregate finding (medium)
    â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€*/

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
               N'aggregate â€” empty locations are a structural pattern for this tenant, not per-site exceptions'
        FROM #detector WHERE Detector='ghost_entity';

    -- instance-bearing intermediate nodes (would vanish from a leaf-only rollup)
    INSERT #assert
    SELECT 'A-INT-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
           'instances_on_intermediate_node', BranchName, Instances, NULL, NULL, NULL, NULL, NULL,
           N'held directly on a non-leaf node'
    FROM #rows WHERE Flags LIKE '%instances_on_intermediate_node%' AND Instances > 0;

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
      8. FINDINGS â€” every one backed by assertion ids
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (
        FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400),
        NarrativeGuard NVARCHAR(500) NULL
    );

    INSERT #find
    SELECT 'F-WORST','high',
           CONCAT(N'', ScopeLabel, N' has the highest overdue rate at ', Value, N'%'),
           'A-WORST,A-TENANT', NULL
    FROM #assert WHERE AssertionId = 'A-WORST' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-ONB','info',
           CONCAT(N'', ScopeLabel, N''' s clean record is an onboarding artifact, not performance'),
           AssertionId, 
           N'MUST NOT be presented as a top performer. Lifetime closure events are far below configured obligations.'
    FROM #assert WHERE AssertionId LIKE 'A-ONB-[0-9]%';   -- consistent with F-SPOF / F-OWN / F-GHOST

    /*  [TRAP] MATCH THE NUMBERED ASSERTIONS ONLY, NEVER THE AGGREGATE.
        'A-SPOF-%' also matches 'A-SPOF-AGG', so the aggregate assertion produced a
        SECOND, individual-shaped finding reading "tenant depends on a single person
        for performance or review" - duplicating F-SPOF-AGG and asserting nonsense
        about a scope label of 'tenant'. The emission policy exists precisely to make
        these mutually exclusive. Anchor on [0-9], as F-GHOST already does.          */
    INSERT #find
    SELECT 'F-SPOF','medium',
           CONCAT(N'', ScopeLabel, N' depends on a single person for performance or review'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-SPOF-[0-9]%';

    INSERT #find
    SELECT 'F-OWN','high',
           CONCAT(N'', ScopeLabel, N' has ', Value, N'% of obligations with no assigned owner'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-OWN-[0-9]%';   -- [0-9] excludes A-OWN-AGG

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

    /*â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
      9. DATA QUALITY â€” declared, never silent
    â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
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

    DROP TABLE #inst; DROP TABLE #ovd; DROP TABLE #owned; DROP TABLE #people;
    DROP TABLE #closures; DROP TABLE #rows; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Location dimension installed.';
GO

