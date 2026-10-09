/*===========================================================================
  RegTrack Insights - v3 (2026-10-01): occurrence-level Entity
  Object : dbo.usp_Insights_Dimension_Entity
  Base   : the definition DEPLOYED on UAT (sql/v2/28 lineage + live hotfixes)

  Change : DirectInstances/DirectOverdue (and their subtree rollups) now count
  every real ComplianceScheduleOn OCCURRENCE in the window (one row per real due
  date), not one row per distinct ComplianceInstanceID - same conversion already
  applied and Excel-verified on Location/Risk/Nature/Departments/Act (sql/v2/37-41).

  Scope of this change, deliberately narrow: ONLY DirectInstances/DirectOverdue
  (and SubtreeInstances/SubtreeOverdue, which roll them up unchanged) move to
  occurrence grain. DirectImprisonment and DirectNoInstanceOwner stay
  INSTANCE-level (they describe the obligation itself, not something that
  multiplies per occurrence) - computed from a SEPARATE instance-level join
  (#directInst below), never from #occ, to avoid fan-out in the aggregate.

  ScopedInstances output column [FIX 2026-10-01, same defect found on the other
  5 dims] reports the DISTINCT instance count (@scopedInstancesDistinct, from
  #inst), never the occurrence count. @scopedTotal (from #occ) is the
  occurrence-level denominator driving reconciliation, OverduePct and
  ApexSharePct - unchanged in spirit, now explicit about what it counts.

  #ovd (DISTINCT ComplianceInstanceID overdue, with the 2026-09-30 out-of-window
  leak fix) is replaced by #ovdocc, joined on ComplianceScheduleOnID through
  #occ - the same occurrence-level pattern used by the other 5 dims, which
  structurally prevents that same leak (an occurrence outside the window was
  never a member of #occ to begin with).

  Error block: unchanged (51050-51059, same as the prior version).

  [FOUND LIVE 2026-10-05] #rows.BranchName NVARCHAR(300)/ApexName NVARCHAR(400)
  too narrow for the real source: CustomerBranch.Name is varchar(500), and one
  real branch in production is already 319 chars. Same bug class as the
  2026-09-23 Caveat truncation (CLAUDE.md Sec.5) - widened both to
  NVARCHAR(600) defensively. Same fix applied the same day to
  Location/Internal/CoverageGaps (same source), Act (ActName), Licence
  (LicenseTypeName).
===========================================================================*/
SET NOCOUNT ON;
GO
CREATE OR ALTER PROCEDURE dbo.usp_Insights_Dimension_Entity
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

    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51052, N'ENTITY DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51052, N'ENTITY DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    /*=====================================================================
      1b. OCCURRENCE-LEVEL ACTIVE SCHEDULES [CHANGED 2026-10-01] - one row
          per real due date, never collapsed to DISTINCT ComplianceInstanceID.
          This is what DirectInstances/DirectOverdue are now counted over.
    =====================================================================*/
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
    -- NoInstanceOwner/ApexSharePct denominator parity with the other dims).
    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #occ o WHERE o.ComplianceInstanceID = i.ComplianceInstanceID);

    /*=====================================================================
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
    =====================================================================*/
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

    /*=====================================================================
      3. OWNERSHIP - an instance with no performer is ownerless (instance-level,
         unchanged)
    =====================================================================*/
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
      4. DIRECT COUNTS PER NODE [CHANGED 2026-10-01]

      Built from the TREE (the member list), not from the instances, so a node
      holding nothing still produces a row. Every node appears - leaf AND
      intermediate.

      Occurrence-level (DirectInstances/DirectOverdue) and instance-level
      (DirectNoInstanceOwner/DirectImprisonment) facts are aggregated in TWO
      separate pre-aggregated queries, each already 1-row-per-branch before
      the join - joining #occ (many rows per instance) and #inst (one row per
      instance) in a single GROUP BY would fan the instance-level counts out
      by however many occurrences each instance has, silently inflating them.
    =====================================================================*/
    IF OBJECT_ID('tempdb..#directOcc') IS NOT NULL DROP TABLE #directOcc;
    SELECT
        t.BranchID,
        COUNT(x.ScheduleOnID) AS DirectInstances,
        SUM(CASE WHEN v.ComplianceScheduleOnID IS NOT NULL THEN 1 ELSE 0 END) AS DirectOverdue
    INTO #directOcc
    FROM dbo.tvfInsightsEntityTree(@CustomerID) t
    LEFT JOIN #occ    x ON x.BranchID = t.BranchID
    LEFT JOIN #ovdocc v ON v.ComplianceScheduleOnID = x.ScheduleOnID
    GROUP BY t.BranchID;

    IF OBJECT_ID('tempdb..#directInst') IS NOT NULL DROP TABLE #directInst;
    SELECT
        t.BranchID,
        SUM(CASE WHEN i.ComplianceInstanceID IS NOT NULL
                  AND w.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END)     AS DirectNoInstanceOwner,
        SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END)                 AS DirectImprisonment
    INTO #directInst
    FROM dbo.tvfInsightsEntityTree(@CustomerID) t
    LEFT JOIN #inst  i ON i.BranchID = t.BranchID
    LEFT JOIN #owned w ON w.ComplianceInstanceID = i.ComplianceInstanceID
    GROUP BY t.BranchID;

    IF OBJECT_ID('tempdb..#direct') IS NOT NULL DROP TABLE #direct;
    SELECT
        do_.BranchID,
        do_.DirectInstances,
        do_.DirectOverdue,
        di.DirectNoInstanceOwner,
        di.DirectImprisonment
    INTO #direct
    FROM #directOcc do_
    JOIN #directInst di ON di.BranchID = do_.BranchID;

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
        BranchName          NVARCHAR(600)  NULL,
        ParentID            INT            NULL,
        ApexId              INT            NULL,
        ApexName            NVARCHAR(600)  NULL,
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

      [FIX 2026-10-01] @scopedTotal is now the OCCURRENCE total (from #occ) -
      the denominator DirectInstances/SubtreeInstances are actually counted
      against, same convention as Location/Risk/Nature/Departments/Act.
      @scopedInstancesDistinct (from #inst, window-narrowed) is the DISTINCT
      instance count and feeds ONLY the ScopedInstances output column below -
      it must never be used as a ratio denominator, that would mix grains.
    =====================================================================*/
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #occ);
    DECLARE @scopedInstancesDistinct INT = (SELECT COUNT(*) FROM #inst);

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
    -- [FOUND LIVE 2026-10-06, same class as Act's LargestRegulatorInstances fix] The real count
    -- behind LargestApexSharePct (ApexSharePct = 100.0 * SubtreeInstances / @scopedTotal, see its
    -- own computation below) - added so the render agent cites this directly instead of deriving
    -- it from the percentage and a total, which produces an unverifiable number and gets the whole
    -- report refused by the untraceable-number gate (CLAUDE.md non-negotiable #5).
    DECLARE @maxApexInstances INT =
        (SELECT TOP 1 SubtreeInstances FROM #rows WHERE Depth = 0 ORDER BY ApexSharePct DESC, SubtreeInstances DESC);
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

      On DirectInstances (now occurrence-level, same as the other 5 dims).
      See the header - SubtreeInstances double-counts by construction and
      must never be reconciled against the control total.
    =====================================================================*/
    DECLARE @rowSum INT = (SELECT ISNULL(SUM(DirectInstances),0) FROM #rows);

    IF @rowSum <> @scopedTotal
        THROW 51051, N'ENTITY DIMENSION RECONCILIATION FAILED - per-node direct sums do not tie to the scoped occurrence total. A node was dropped. Refusing to publish.', 1;

    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT COUNT(*) FROM #ovdocc) / @scopedTotal END;

    SELECT
        'control_totals'                   AS ResultSet,
        @scopedInstancesDistinct           AS ScopedInstances,
        @rowSum                            AS SumOfRows,
        CAST(1 AS BIT)                     AS Reconciled,
        (SELECT COUNT(*) FROM #ovdocc)     AS OverdueInstances,
        @tenantOverduePct                  AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows)       AS NodesReported,
        (SELECT COUNT(*) FROM CustomerBranch
          WHERE CustomerID = @CustomerID AND IsDeleted = 0 AND Status = 1) AS ActiveBranchesInTenant,
        @apexCount                         AS ApexEntityCount,
        @tenantShape                       AS TenantShape,
        @maxApexInstances                  AS LargestApexInstances,
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
        with a percentage. Below the cap, always name the members.                 */
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
                      N' scheduled occurrence(s) in this user''s authorised scope, not the tenant total. '
                    + N'They are not comparable with an unscoped tenant-wide figure.')
        WHERE @hasAnyObligations = 1
    ) q;

    DROP TABLE #inst; DROP TABLE #occ; DROP TABLE #lt; DROP TABLE #ovdocc; DROP TABLE #ownership; DROP TABLE #owned;
    DROP TABLE #directOcc; DROP TABLE #directInst; DROP TABLE #direct;
    DROP TABLE #closure; DROP TABLE #rows; DROP TABLE #detector;
    DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Entity (v3, occurrence-level) installed.';
GO
