/*===========================================================================
  RegTrack Insights - API support
  ELIGIBLE TENANTS - the server-side IDOR guard (docs/API_CONTRACTS.md, Sec.1)

  Authored by Claude Code; deployed to production 2026-09-03. Pulled from
  sys.sql_modules and committed here so the repo matches what is running.

  Returns every tenant the caller may generate a report for, with tier and
  scope class. The API MUST call this on every request and reject any
  client-supplied tenant id not in the result - never trust the client.

  Eligibility is the conjunction of four tests (design spec Sec.5.6.2):
    1. Customer.IsDeleted = 0
    2. RegInsights product mapped AND enabled (ProductMapping.IsActive = 0 - INVERTED)
    3. The user has scope rows (EntitiesAssignment) on OPERATING branches
    4. [ADDED 2026-09-28, product decision] The user holds the Management role:
       User.RoleID -> Role.Code = 'MGMT', and the user is active and not deleted.
       Company Admins, performers, reviewers etc. are NOT eligible, even with
       EntitiesAssignment rows (on tenant 1285, 4 of the 13 users with scope rows
       were not Management). Resolved by Role.Code, never by a hardcoded RoleID.
       This is User.RoleID (the compliance module role), NOT User.LicenseRoleID -
       the licence module's own role only narrows what the Licence dimension
       shows (sql/21), it does not decide who may use Insights.

  No error block: this proc never THROWs - an ineligible tenant is simply
  absent from the result, and an empty result means DENY.
===========================================================================*/

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

    /*-- 0. Management role only - see header test 4. Not Management = empty
       result = DENY, same as every other failed test here (no THROW).      */
    IF NOT EXISTS (SELECT 1 FROM [User] u
                   JOIN Role r ON r.ID = u.RoleID
                   WHERE u.ID = @UserID
                     AND u.IsDeleted = 0
                     AND u.IsActive = 1
                     AND r.Code = 'MGMT')
    BEGIN
        SELECT CAST(NULL AS INT) AS TenantId, CAST(NULL AS NVARCHAR(200)) AS Name,
               CAST(NULL AS VARCHAR(10)) AS Tier, CAST(NULL AS VARCHAR(20)) AS ScopeClass
        WHERE 1 = 0;
        RETURN;
    END

    /*-- 1. The user's scope footprint, per customer. Active branches only. --*/
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

    /*-- 2. The tenant universe for those customers (mirrors ClassifyScope). --*/
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

    /*-- 3. Entitlement, resolved paid-wins. 18 = Basic, 19 = Pro. ---------*/
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

    /*-- 4. The conjunction - INNER JOIN on every axis. -----------------------*/
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

PRINT 'Eligible tenants installed.';
GO
