/*==========================================================================
  RegTrack Insights - Phase 1d, Step 15
  ELIGIBLE TENANTS  (the IDOR guard)

  Spec reference : RegTrack_Insights_System_Design_v1.md Sec.5.6.2, Sec.5.6.3
                   docs/API_CONTRACTS.md Sec.1 (GET /api/insights/tenants)
  Purpose        : Given an authenticated user, return the tenants that user is
                   allowed to see Insights for - the server own answer, derived
                   from nothing the client supplied but the identity.

  -- THE CENTRAL TRAP ------------------------------------------------------
  This procedure IS the IDOR control. Every other Insights endpoint takes a
  client-supplied @CustomerID and must re-run this membership test against it,
  on every single request. Never cache the answer in session state and trust it
  later: 53 users span more than one customer, and a user who switches tenants
  legitimately is indistinguishable - at the HTTP layer - from one who edits the
  id in the URL. The difference is only ever this query.

  -- ELIGIBILITY IS THE CONJUNCTION OF THREE THINGS ------------------------
    1. Customer.IsDeleted = 0
    2. RegInsights mapped AND enabled  (ProductMapping.IsActive = 0 - INVERTED)
    3. The user has scope rows (EntitiesAssignment) for that customer

  Dropping any one is a leak, and (3) is the one that looks redundant and is
  not: a customer can be fully entitled while this particular user has no
  authorised branches in it. Entitlement is customer-level, scope is user-level,
  and only the pair means "this user may see this tenant".

  -- OTHER TRAPS ----------------------------------------------------------
  * ProductMapping.IsActive IS INVERTED: 0 = ENABLED, 1 = DISABLED. Never
    "correct" this to = 1 - it disables the product for every customer.
  * Tier is PAID-WINS. A tenant mid-transition is briefly mapped to both 18 and
    19; reporting basic then would downgrade a paying customer UI. This mirrors
    usp_Insights_EvaluateGate EXIT_SUPERSEDED, which resolves the same overlap
    the same way.
  * ScopeClass duplicates usp_Insights_ClassifyScope CASE deliberately - that
    proc is per-(user, tenant) and this one is set-based over all of a user
    tenants. If either changes, CHANGE BOTH: the picker badge and the report
    scope classification disagreeing is a support ticket nobody can reproduce.
  * Scope source is EntitiesAssignment, NOT ComplianceCategoryMgmtUser
    (Sec.5.5.4). Matches dbo.tvfInsightsScopePairs exactly.
  * Active branches only, again matching tvfInsightsScopePairs. A user whose
    only branches are soft-deleted has no live scope and is not eligible.

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
==========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_EligibleTenants', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_EligibleTenants;
GO
CREATE PROCEDURE dbo.usp_Insights_EligibleTenants
    @UserID     INT,
    @CustomerID INT = NULL      -- optional: single-tenant membership test for the IDOR guard
AS
BEGIN
    SET NOCOUNT ON;

    /*----------------------------------------------------------------------
      1. The user scope footprint, per customer.

         One pass over EntitiesAssignment for THIS user, joined up to the
         owning customer through CustomerBranch. Active branches only.
         A customer absent from here fails eligibility test (3).
    ----------------------------------------------------------------------*/
    CREATE TABLE #scope (
        CustomerID      INT PRIMARY KEY,
        ScopeBranches   INT NOT NULL,
        ScopeCategories INT NOT NULL);

    INSERT INTO #scope (CustomerID, ScopeBranches, ScopeCategories)
    SELECT cb.CustomerID,
           COUNT(DISTINCT ea.BranchID),
           COUNT(DISTINCT ea.ComplianceCatagoryID)   -- [TRAP] misspelled in the schema
    FROM EntitiesAssignment ea
    JOIN CustomerBranch cb ON cb.ID = ea.BranchID
    WHERE ea.UserID = @UserID
      AND cb.IsDeleted = 0
      AND cb.Status = 1
      AND (@CustomerID IS NULL OR cb.CustomerID = @CustomerID)
    GROUP BY cb.CustomerID;

    /*----------------------------------------------------------------------
      2. The tenant universe for those same customers.

         Denominator for the scope classification: how many active branches
         the customer has, and how many distinct categories appear across ANY
         user assignments. Both mirror usp_Insights_ClassifyScope.
    ----------------------------------------------------------------------*/
    CREATE TABLE #universe (
        CustomerID       INT PRIMARY KEY,
        TenantBranches   INT NOT NULL,
        TenantCategories INT NOT NULL);

    INSERT INTO #universe (CustomerID, TenantBranches, TenantCategories)
    SELECT cb.CustomerID,
           COUNT(DISTINCT cb.ID),
           COUNT(DISTINCT ea.ComplianceCatagoryID)
    FROM CustomerBranch cb
    LEFT JOIN EntitiesAssignment ea ON ea.BranchID = cb.ID
    WHERE cb.IsDeleted = 0
      AND cb.Status = 1
      AND cb.CustomerID IN (SELECT CustomerID FROM #scope)
    GROUP BY cb.CustomerID;

    /*----------------------------------------------------------------------
      3. Entitlement, resolved paid-wins.

         Product 18 = RegInsights Basic (free), 19 = RegInsights Pro (paid).
         IsActive = 0 means ENABLED.
    ----------------------------------------------------------------------*/
    CREATE TABLE #entitled (
        CustomerID INT PRIMARY KEY,
        Tier       VARCHAR(10) NOT NULL);

    INSERT INTO #entitled (CustomerID, Tier)
    SELECT pm.CustomerID,
           CASE WHEN MAX(CASE WHEN pm.ProductID = 19 THEN 1 ELSE 0 END) = 1
                THEN 'pro' ELSE 'basic' END
    FROM ProductMapping pm
    WHERE pm.ProductID IN (18, 19)
      AND pm.IsActive = 0                            -- [TRAP] INVERTED
      AND pm.CustomerID IN (SELECT CustomerID FROM #scope)
    GROUP BY pm.CustomerID;

    /*----------------------------------------------------------------------
      4. The conjunction. An INNER JOIN on every axis - a customer missing
         from any one of the three sets simply does not appear.
    ----------------------------------------------------------------------*/
    SELECT
        c.ID    AS TenantId,
        c.Name  AS Name,
        e.Tier  AS Tier,
        CASE
            WHEN s.ScopeBranches >= u.TenantBranches
             AND s.ScopeCategories >= u.TenantCategories THEN 'tenant_wide'
            WHEN s.ScopeBranches >= u.TenantBranches     THEN 'functional'
            ELSE 'entity_scoped'
        END     AS ScopeClass
    FROM #scope s
    JOIN #universe u ON u.CustomerID = s.CustomerID
    JOIN #entitled e ON e.CustomerID = s.CustomerID
    JOIN Customer   c ON c.ID        = s.CustomerID
    WHERE c.IsDeleted = 0
      AND s.ScopeBranches > 0          -- empty scope is DENY, never "unrestricted"
    ORDER BY c.Name;

    DROP TABLE #scope;
    DROP TABLE #universe;
    DROP TABLE #entitled;
END
GO

PRINT 'Eligible tenants (IDOR guard) installed.';
GO
