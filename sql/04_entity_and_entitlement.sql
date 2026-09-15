/*===========================================================================
  RegTrack Insights - Phase 1a, Steps 4 & 5
  Error block 51020-51029.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.
  ENTITY HIERARCHY  +  ENTITLEMENT GATE

  Spec reference : RegTrack_Insights_System_Design_v1.md Sec.6.7 (hierarchy), Sec.5.3 (gate)
  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

/*===========================================================================
  PART 1 - ENTITY HIERARCHY  (Sec.6.7)

  -- THE CENTRAL TRAP -------------------------------------------------------
  INSTANCES DO NOT LIVE ONLY ON LEAVES.

  Measured: 211 instances (20 of them ownerless) sat on ONE tenant's non-leaf
  intermediate node; another tenant had 141 on a parent node. A leaf-only
  rollup silently drops them, and the loss is invisible because the report
  still "looks right".

  The defence is structural: aggregate at EVERY node, then reconcile the
  subtree sums to an independently-queried tenant control total. A gap of even
  one instance means a node was dropped.
===========================================================================*/

/*---------------------------------------------------------------------------
  1.1 Full entity tree, every node tagged with its root

  -- CRITICAL FIX: ANCHOR ON APEX **OR ORPHAN** -----------------------------
  The obvious implementation anchors only on `ParentID IS NULL`. That is WRONG
  and loses data, because the recursion filters IsDeleted = 0 at every hop and
  therefore CANNOT TRAVERSE THROUGH a soft-deleted intermediate node. Every
  active branch beneath a deleted parent becomes unreachable and silently
  vanishes from the rollup.

  Measured on production with the apex-only version:
     tenant 29  : control 1,140  rollup   130  ->  1,010 instances LOST (89%!)
     tenant 5   : control 12,905 rollup 12,021 ->    884 instances LOST
     tenant 1818: control 1,637  rollup 1,635  ->      2 instances LOST
  Tenant 29's case: ~22 active "SNG Golds" branches (46 instances each) whose
  parent grouping node 52141 had been soft-deleted.

  The reference tenant used during design had ZERO orphans, so apex-only
  reconciled perfectly there - the FOURTH instance in this project of logic
  being "correct by luck" on a single tenant.

  FIX: treat as a root any ACTIVE branch that has no ACTIVE parent - i.e.
  ParentID IS NULL, or the parent is missing / soft-deleted / in another
  tenant. This guarantees every active branch is reachable, so the rollup
  always ties to the control total. Verified: 12/12 tenants gap = 0.

  Orphan roots are FLAGGED (RootKind = 'orphan') so the report can declare
  "this group's parent entity was deleted; it is shown as a top-level group"
  rather than silently re-parenting it.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.tvfInsightsEntityTree', 'IF') IS NOT NULL
    DROP FUNCTION dbo.tvfInsightsEntityTree;
GO
CREATE FUNCTION dbo.tvfInsightsEntityTree (@CustomerID INT)
RETURNS TABLE
AS
RETURN
(
    WITH roots AS (
        SELECT cb.ID, cb.Name, cb.ParentID,
               CASE WHEN cb.ParentID IS NULL THEN 'apex' ELSE 'orphan' END AS RootKind
        FROM CustomerBranch cb
        WHERE cb.CustomerID = @CustomerID
          AND cb.IsDeleted = 0 AND cb.Status = 1
          AND (cb.ParentID IS NULL
               OR NOT EXISTS (SELECT 1 FROM CustomerBranch p
                              WHERE p.ID = cb.ParentID
                                AND p.IsDeleted = 0 AND p.Status = 1
                                AND p.CustomerID = cb.CustomerID))
    ),
    tree AS (
        SELECT r.ID, r.Name, r.ParentID,
               r.ID   AS ApexId,
               CAST(r.Name AS NVARCHAR(400)) AS ApexName,
               r.RootKind,
               0      AS Depth
        FROM roots r
        UNION ALL
        SELECT c.ID, c.Name, c.ParentID,
               t.ApexId, t.ApexName, t.RootKind, t.Depth + 1
        FROM CustomerBranch c
        JOIN tree t ON c.ParentID = t.ID
        WHERE c.CustomerID = @CustomerID
          AND c.IsDeleted  = 0
          AND c.Status     = 1
    )
    SELECT
        t.ID            AS BranchID,
        t.Name          AS BranchName,
        t.ParentID,
        t.ApexId,
        t.ApexName,
        t.RootKind,                       -- 'apex' | 'orphan'
        t.Depth,
        CASE WHEN EXISTS (SELECT 1 FROM CustomerBranch ch
                          WHERE ch.ParentID = t.ID AND ch.IsDeleted = 0 AND ch.Status = 1)
             THEN 'intermediate' ELSE 'leaf' END AS NodeType
    FROM tree t
);
GO

/*---------------------------------------------------------------------------
  1.2 Rollup with mandatory reconciliation
      THROWs if the subtree sums do not tie to the tenant control total.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_EntityRollup', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_EntityRollup;
GO
CREATE PROCEDURE dbo.usp_Insights_EntityRollup
    @CustomerID INT
AS
BEGIN
    SET NOCOUNT ON;

    -- per-node direct instance counts (leaf AND intermediate)
    IF OBJECT_ID('tempdb..#nodes') IS NOT NULL DROP TABLE #nodes;
    SELECT
        t.BranchID, t.BranchName, t.ParentID, t.ApexId, t.ApexName,
        t.RootKind, t.Depth, t.NodeType,
        COUNT(i.ID) AS DirectInstances
    INTO #nodes
    FROM dbo.tvfInsightsEntityTree(@CustomerID) t
    LEFT JOIN ComplianceInstance i
           ON i.CustomerBranchID = t.BranchID AND i.IsDeleted = 0
    GROUP BY t.BranchID, t.BranchName, t.ParentID, t.ApexId, t.ApexName,
             t.RootKind, t.Depth, t.NodeType;

    -- RECONCILE: independently-queried control total must equal the rollup
    DECLARE @control INT, @rollup INT;

    SELECT @control = COUNT(*)
    FROM ComplianceInstance i
    JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1 AND i.IsDeleted = 0;

    SELECT @rollup = SUM(DirectInstances) FROM #nodes;

    IF ISNULL(@rollup,0) <> @control
        THROW 51020, N'ENTITY ROLLUP RECONCILIATION FAILED - subtree sums do not tie to the tenant control total. A node was dropped (most likely an instance-bearing INTERMEDIATE node). Refusing to publish.', 1;

    -- ORPHAN DECLARATION: subtrees whose parent entity was soft-deleted.
    -- These must be surfaced as a data_quality entry, never silently re-parented.
    SELECT
        ApexId, ApexName,
        SUM(DirectInstances) AS SubtreeInstances,
        N'Parent entity was deleted - this group is reported as top-level. '
      + N'Verify the hierarchy with the tenant.' AS DataQualityNote
    FROM #nodes
    WHERE RootKind = 'orphan'
    GROUP BY ApexId, ApexName
    HAVING SUM(DirectInstances) > 0
    ORDER BY SubtreeInstances DESC;

    -- root-level summary
    SELECT
        ApexId, ApexName, MAX(RootKind) AS RootKind,
        COUNT(*)                    AS NodesInSubtree,
        SUM(DirectInstances)        AS SubtreeInstances,
        SUM(CASE WHEN NodeType='intermediate' THEN DirectInstances ELSE 0 END) AS InstancesOnIntermediateNodes,
        SUM(CASE WHEN NodeType='leaf'         THEN DirectInstances ELSE 0 END) AS InstancesOnLeaves
    FROM #nodes
    GROUP BY ApexId, ApexName
    ORDER BY SubtreeInstances DESC;

    -- node detail
    SELECT * FROM #nodes ORDER BY ApexName, Depth, BranchName;

    DROP TABLE #nodes;
END
GO

/*---------------------------------------------------------------------------
  1.3 tenant_shape + comparison-grain auto-selection  (Sec.6.7.2)

      Dominance rule: if an apex holds > ~70% of volume, OR is a childless
      holding shell, descend one level to find a meaningful comparison grain.
      Observed: a tenant whose holding apex carried 96% of the estate had to be
      compared one level down (division split); a balanced 6-legal-entity group
      was correctly compared at apex level.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_TenantShape', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_TenantShape;
GO
CREATE PROCEDURE dbo.usp_Insights_TenantShape
    @CustomerID          INT,
    @DominanceThreshold  DECIMAL(5,2) = 70.00
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID('tempdb..#apex') IS NOT NULL DROP TABLE #apex;
    SELECT
        t.ApexId, t.ApexName,
        SUM(CASE WHEN i.ID IS NOT NULL THEN 1 ELSE 0 END) AS SubtreeInstances,
        SUM(CASE WHEN t.BranchID <> t.ApexId THEN 1 ELSE 0 END) AS DescendantNodes
    INTO #apex
    FROM dbo.tvfInsightsEntityTree(@CustomerID) t
    LEFT JOIN ComplianceInstance i
           ON i.CustomerBranchID = t.BranchID AND i.IsDeleted = 0
    GROUP BY t.ApexId, t.ApexName;

    DECLARE @apexCount INT   = (SELECT COUNT(*) FROM #apex);
    DECLARE @total     INT   = (SELECT NULLIF(SUM(SubtreeInstances),0) FROM #apex);
    DECLARE @maxShare  DECIMAL(5,2) =
        (SELECT MAX(SubtreeInstances) * 100.0 / @total FROM #apex);
    DECLARE @childlessShell BIT =
        (SELECT CASE WHEN EXISTS (SELECT 1 FROM #apex WHERE DescendantNodes = 0
                                    AND SubtreeInstances = 0) THEN 1 ELSE 0 END);

    SELECT
        @CustomerID AS CustomerID,
        @apexCount  AS ApexEntityCount,
        CASE WHEN @apexCount = 1 THEN 'single_entity' ELSE 'multi_entity' END AS TenantShape,
        @maxShare   AS LargestApexSharePct,
        CASE
            WHEN @apexCount = 1                        THEN 'locations'
            WHEN @maxShare >= @DominanceThreshold      THEN 'descend_one_level'
            WHEN @childlessShell = 1                   THEN 'descend_one_level'
            ELSE 'apex'
        END AS ComparisonGrain,
        CASE
            WHEN @apexCount = 1                   THEN N'Single apex - skip entity comparison, lead with locations.'
            WHEN @maxShare >= @DominanceThreshold THEN CONCAT(N'Largest apex holds ', @maxShare, N'% - descend one level for a meaningful comparison.')
            WHEN @childlessShell = 1              THEN N'A childless holding shell is present - descend one level.'
            ELSE N'Apex entities are balanced - compare at apex level.'
        END AS GrainReason;

    SELECT * FROM #apex ORDER BY SubtreeInstances DESC;
    DROP TABLE #apex;
END
GO


/*===========================================================================
  PART 2 - ENTITLEMENT GATE  (Sec.5.3)

  -- THE CENTRAL TRAP -------------------------------------------------------
  ProductMapping.IsActive IS INVERTED:  0 = ENABLED,  1 = DISABLED.

  Confirmed from the catalog: the core Compliance product has ~1,865 customers
  mapped with only 7 at IsActive = 1 - i.e. 7 customers DISABLED, not 7 enabled.
  A developer who "fixes" `IsActive = 0` to `= 1` silently disables the entire
  product for every customer.

  Gate order is CHEAPEST-FIRST so an unentitled tenant costs literally nothing:
  no aggregation, no LLM call, no email.
===========================================================================*/

IF OBJECT_ID('dbo.usp_Insights_EvaluateGate', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_EvaluateGate;
GO
CREATE PROCEDURE dbo.usp_Insights_EvaluateGate
    @CustomerID INT,
    @Tier       VARCHAR(10)          -- 'free' | 'paid'
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @decision  VARCHAR(20) = 'PROCEED',
            @reason    NVARCHAR(300) = NULL,
            @recipients INT = 0;

    ---- Step 0: tenant active? -------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM Customer WHERE ID = @CustomerID AND IsDeleted = 0)
    BEGIN
        SET @decision = 'EXIT_ZERO_COST';
        SET @reason   = N'Tenant is disabled (Customer.IsDeleted = 1).';
    END

    ---- Step 1: entitled for this tier? (IsActive = 0 means ENABLED) ------
    IF @decision = 'PROCEED'
       AND NOT EXISTS (
            SELECT 1 FROM ProductMapping
            WHERE CustomerID = @CustomerID
              AND ProductID  = CASE WHEN @Tier = 'free' THEN 18 ELSE 19 END
              AND IsActive   = 0)
    BEGIN
        SET @decision = 'EXIT_ZERO_COST';
        SET @reason   = CONCAT(N'Not entitled: product ',
                        CASE WHEN @Tier='free' THEN '18 (RegInsights Basic)'
                             ELSE '19 (RegInsights Pro)' END,
                        N' is not mapped-and-enabled for this customer.');
    END

    ---- Step 2: supersession - paid suppresses free ----------------------
    IF @decision = 'PROCEED' AND @Tier = 'free'
       AND EXISTS (SELECT 1 FROM ProductMapping
                   WHERE CustomerID = @CustomerID AND ProductID = 19 AND IsActive = 0)
    BEGIN
        SET @decision = 'EXIT_SUPERSEDED';
        SET @reason   = N'Paid tier (product 19) is active - the free digest self-skips. '
                      + N'This also covers the non-atomic transition window when both are briefly mapped.';
    END

    ---- Step 3: recipients (minus opt-outs) ------------------------------
    IF @decision = 'PROCEED'
    BEGIN
        /*  [CORRECTED 2026-09-08] Management-role users, per BA ruling - both
            tiers. UserCustomerMapping cannot answer this: all 65 production
            rows carry ProductID = NULL and IsActive = 1. See sql/01.

            [CORRECTED 2026-09-10] FREE TIER ONLY subtracts durable opt-outs
            (sql/16, InsightsDigestSuppression) - that table is scoped to the
            weekly DIGEST EMAIL channel (unsubscribe / hard bounce), not to
            paid-tier report viewing. Subtracting it for @Tier = 'paid' would
            undercount a tenant's entitlement based on an unrelated channel's
            opt-out. Opt-out SURVIVES tier changes (Sec.5.4).                 */
        SELECT @recipients = COUNT(DISTINCT m.UserID)
        FROM dbo.tvfInsightsManagementUsers(@CustomerID) m
        WHERE @Tier = 'paid'
           OR NOT EXISTS (SELECT 1 FROM dbo.InsightsDigestSuppression s
                          WHERE s.CustomerID = @CustomerID AND s.UserID = m.UserID);

        IF @recipients = 0
        BEGIN
            SET @decision = 'EXIT_NO_RECIPIENTS';
            SET @reason   = N'No enabled recipients - exit before any aggregation or LLM spend.';
        END
    END

    SELECT
        @CustomerID  AS CustomerID,
        @Tier        AS Tier,
        @decision    AS Decision,
        @recipients  AS RecipientCount,
        ISNULL(@reason, N'Entitled, not superseded, recipients present - proceed to aggregation.') AS Reason,
        CASE WHEN @decision = 'PROCEED' THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS ShouldProceed;
END
GO

PRINT 'Entity hierarchy + entitlement gate installed.';
GO
