/*===========================================================================
  RegTrack Insights - Phase 1d, build order item 17 (cost/quota model)
  TENANT TOKEN USAGE LEDGER

  Spec reference : design doc Sec.12.3 ("Per-tenant monthly token
                   circuit-breaker - a high ceiling that should never
                   trigger normally, capping a runaway ... before it
                   becomes a bill surprise") and Sec.12.3's alerting sibling
                   (Budget:AlertAtPercentOfCeiling).

  -- WHY THIS EXISTS -------------------------------------------------------
  InsightsCostMetrics (the OTel Meter) deliberately carries NO tenant tag -
  ~2,290 tenants as a metric dimension is a cardinality explosion, and a
  tenant identifier in a metrics backend is customer data nobody audits
  there. Its own doc comment names the fix: "Per-tenant spend belongs in a
  log line or a SQL row where it can be joined, retained and purged like
  the rest of the tenant's data." This table is that SQL row - one append
  per run, regardless of whether the run finished, refused, or threw,
  because the check this enables (Sec.12.3) is about ACTUAL SPEND, not
  just successful reports.

  -- APPEND-ONLY, NOT AGGREGATED -------------------------------------------
  One row per run, never UPDATEd. Month-to-date usage is SUM(TotalTokens)
  WHERE CustomerID = @x AND RecordedAtUtc >= start-of-month, computed by the
  reader (SqlTenantTokenBudgetRepository), not maintained as a running
  total here - an append-only ledger cannot drift out of sync with itself.

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.InsightsTenantTokenUsage', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsTenantTokenUsage (
        Id             BIGINT IDENTITY(1,1) NOT NULL,
        CustomerID     INT NOT NULL,
        RunId          NVARCHAR(200) NOT NULL,
        TotalTokens    BIGINT NOT NULL,
        RecordedAtUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_InsightsTenantTokenUsage_RecordedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_InsightsTenantTokenUsage PRIMARY KEY CLUSTERED (Id)
    );

    -- The only query this table ever serves: SUM(TotalTokens) for one tenant
    -- since a point in time. Covering on TotalTokens avoids a key lookup.
    CREATE NONCLUSTERED INDEX IX_InsightsTenantTokenUsage_Customer_Recorded
        ON dbo.InsightsTenantTokenUsage (CustomerID, RecordedAtUtc)
        INCLUDE (TotalTokens);
END
GO

PRINT 'InsightsTenantTokenUsage installed.';
GO
