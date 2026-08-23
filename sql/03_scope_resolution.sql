/*===========================================================================
  RegTrack Insights - Phase 1a, Step 3
  SCOPE RESOLUTION  (the security boundary)

  Spec reference : RegTrack_Insights_System_Design_v1.md Sec.5.5
  Purpose        : Resolve a management user's authorised scope and constrain
                   every query to it. Deterministic. No LLM anywhere near this.

  -- THE CENTRAL TRAP -------------------------------------------------------
  SCOPE IS TWO-DIMENSIONAL: (BranchID x ComplianceCatagoryID).

  Measured on a reference tenant: 1,060 EntitiesAssignment rows across 11 users
  (~96 each) because each row is a (branch, category) PAIR. All 11 users were
  category-specific; none had a null or zero category.

  A service that filters on BRANCH ONLY shows an EHS manager the Labour and
  Secretarial data they are not authorised for - a scope leak INSIDE a single
  tenant. Subtler than a cross-entity leak and far easier to miss, because both
  parties belong to the same customer.

  -- OTHER TRAPS ------------------------------------------------------------
  - Column is MISSPELLED: EntitiesAssignment.ComplianceCatagoryID ("Catagory")
  - Category resolves ONLY via ComplianceInstance -> Compliance -> Act.ComplianceCategoryId
    (Compliance, ComplianceInstance and ComplianceSubType have NO category column)
  - EMPTY SCOPE => DENY. Never "no restriction" - that inverts the security model.
  - tenant_wide requires BOTH axes. On a reference tenant, 3 users had all 16
    branches but only 1 had all 9 categories. The other 2 are FUNCTIONAL heads
    (all locations, one function) and must not be classified as tenant-wide CCOs.
  - Source is EntitiesAssignment, NOT ComplianceCategoryMgmtUser (see Sec.5.5.4:
    CM is a strict subset that excludes configured-but-dormant sites, which are
    themselves a key finding the engine exists to surface).

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

/*---------------------------------------------------------------------------
  1. SCOPE PAIRS - the unit of authorisation
     Returns the (BranchID, CategoryId) tuples a user is authorised for.
     Active branches only. An empty result means DENY (enforced by callers).
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.tvfInsightsScopePairs', 'IF') IS NOT NULL
    DROP FUNCTION dbo.tvfInsightsScopePairs;
GO
CREATE FUNCTION dbo.tvfInsightsScopePairs (@UserID INT, @CustomerID INT)
RETURNS TABLE
AS
RETURN
(
    SELECT DISTINCT
        ea.BranchID                 AS BranchID,
        ea.ComplianceCatagoryID     AS CategoryId   -- NOTE: schema misspelling
    FROM EntitiesAssignment ea
    JOIN CustomerBranch cb ON cb.ID = ea.BranchID
    JOIN Customer       cu ON cu.ID = cb.CustomerID
    WHERE ea.UserID      = @UserID
      AND cb.CustomerID  = @CustomerID
      AND cb.IsDeleted   = 0        -- active branch
      AND cu.IsDeleted   = 0        -- active tenant
);
GO

/*---------------------------------------------------------------------------
  2. SCOPE CLASSIFICATION - must read BOTH axes
     tenant_wide <=> ALL active branches AND ALL tenant categories.
     Anything less is entity_scoped or functional.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_ClassifyScope', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_ClassifyScope;
GO
CREATE PROCEDURE dbo.usp_Insights_ClassifyScope
    @UserID     INT,
    @CustomerID INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @scopeBranches INT, @scopeCats INT,
            @tenantBranches INT, @tenantCats INT;

    SELECT @scopeBranches = COUNT(DISTINCT BranchID),
           @scopeCats     = COUNT(DISTINCT CategoryId)
    FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID);

    SELECT @tenantBranches = COUNT(*)
    FROM CustomerBranch WHERE CustomerID = @CustomerID AND IsDeleted = 0;

    -- tenant category universe = categories appearing in ANY user's assignments
    SELECT @tenantCats = COUNT(DISTINCT ea.ComplianceCatagoryID)
    FROM EntitiesAssignment ea
    JOIN CustomerBranch cb ON cb.ID = ea.BranchID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0;

    SELECT
        @UserID          AS UserID,
        @CustomerID      AS CustomerID,
        @scopeBranches   AS ScopeBranches,
        @tenantBranches  AS TenantBranches,
        @scopeCats       AS ScopeCategories,
        @tenantCats      AS TenantCategories,
        CASE
            WHEN @scopeBranches = 0 THEN 'DENY'
            WHEN @scopeBranches >= @tenantBranches
             AND @scopeCats     >= @tenantCats     THEN 'tenant_wide'
            WHEN @scopeBranches >= @tenantBranches
             AND @scopeCats     <  @tenantCats     THEN 'functional'   -- all sites, some functions
            ELSE 'entity_scoped'
        END AS ScopeClass,
        CASE WHEN @scopeBranches = 0 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS IsDenied;
END
GO

/*---------------------------------------------------------------------------
  3. SCOPE-CONSTRAINED INSTANCES - the pre-flight predicate

     [SPEC Sec.14 / D3] Scope is enforced PRE-FLIGHT in the query predicate, so
     aggregates are computed only over in-scope rows and cannot leak in summary
     form. The post-flight audit (Sec.4) is a second net, not the primary control.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.tvfInsightsScopedInstances', 'IF') IS NOT NULL
    DROP FUNCTION dbo.tvfInsightsScopedInstances;
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
      AND cb.IsDeleted  = 0
      AND i.IsDeleted   = 0
      AND c.IsDeleted   = 0
);
GO

/*---------------------------------------------------------------------------
  4. POST-FLIGHT AUDIT - two-dimensional
     Re-verify EVERY returned row against the scope pair set, on BOTH axes.
     Any violation THROWs; the caller must never publish.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_AuditScope', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_AuditScope;
GO
CREATE PROCEDURE dbo.usp_Insights_AuditScope
    @UserID     INT,
    @CustomerID INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @branchViolations INT, @categoryViolations INT, @pairViolations INT;

    /*  [TRAP] SQL Server rejects a NOT EXISTS subquery used inside a CASE that is
        itself an argument to an aggregate (Msg 130, "Cannot perform an aggregate
        function on an expression containing an aggregate or a subquery"). It fires
        at CREATE PROCEDURE time, so the procedure is NEVER created - and because a
        failed CREATE does not stop later batches, the trailing PRINT still says
        "installed". Same trap as usp_Insights_StatusDataQuality in sql/01.

        EXISTS is legal in a SELECT list, so flag each row first, then SUM the flags. */
    ;WITH scoped AS (SELECT * FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID)),
          pairs  AS (SELECT * FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID)),
          flagged AS (
        SELECT
            CASE WHEN EXISTS (SELECT 1 FROM pairs p WHERE p.BranchID = s.BranchID)
                 THEN 0 ELSE 1 END AS BranchViolation,
            CASE WHEN EXISTS (SELECT 1 FROM pairs p WHERE p.CategoryId = s.CategoryId)
                 THEN 0 ELSE 1 END AS CategoryViolation,
            CASE WHEN EXISTS (SELECT 1 FROM pairs p WHERE p.BranchID   = s.BranchID
                                                     AND p.CategoryId = s.CategoryId)
                 THEN 0 ELSE 1 END AS PairViolation
        FROM scoped s)
    SELECT @branchViolations   = SUM(BranchViolation),
           @categoryViolations = SUM(CategoryViolation),
           @pairViolations     = SUM(PairViolation)
    FROM flagged;

    SET @branchViolations   = ISNULL(@branchViolations, 0);
    SET @categoryViolations = ISNULL(@categoryViolations, 0);
    SET @pairViolations     = ISNULL(@pairViolations, 0);

    IF (@branchViolations + @categoryViolations + @pairViolations) > 0
        THROW 51010, N'SCOPE AUDIT FAILED - out-of-scope rows detected. Refusing to publish.', 1;

    SELECT @branchViolations   AS BranchViolations,
           @categoryViolations AS CategoryViolations,
           @pairViolations     AS PairViolations,
           CAST(1 AS BIT)      AS ScopeAuditPassed;
END
GO

/*---------------------------------------------------------------------------
  5. PROVISIONING CHECK  (spec Sec.5.5.5)
     A user mapped to RegInsights (18/19) but WITHOUT scope rows is "entitled
     but scopeless": fail-closed correctly returns an empty report, which is
     safe but looks broken - and generates support pressure to widen scopes,
     which is how security boundaries erode socially (pre-mortem D5).
     Run this in the provisioning runbook and as an ops alert.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_FindScopelessUsers', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FindScopelessUsers;
GO
CREATE PROCEDURE dbo.usp_Insights_FindScopelessUsers
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        ucm.CustomerID,
        ucm.UserID,
        ucm.ProductID,
        CASE ucm.ProductID WHEN 18 THEN 'RegInsights Basic'
                           WHEN 19 THEN 'RegInsights Pro' END AS ProductName,
        u.IsActive  AS UserIsActive,
        N'Mapped to RegInsights but has no EntitiesAssignment scope rows - '
      + N'will receive an empty report. Configure scope before go-live.' AS Issue
    FROM UserCustomerMapping ucm
    JOIN [User]   u  ON u.ID  = ucm.UserID
    JOIN Customer cu ON cu.ID = ucm.CustomerID
    WHERE ucm.ProductID IN (18, 19)
      AND ucm.IsActive = 0            -- INVERTED: 0 = enabled
      AND u.IsDeleted  = 0
      AND cu.IsDeleted = 0
      AND NOT EXISTS (
            SELECT 1 FROM dbo.tvfInsightsScopePairs(ucm.UserID, ucm.CustomerID)
      );
END
GO

PRINT 'Scope resolution installed.';
GO
