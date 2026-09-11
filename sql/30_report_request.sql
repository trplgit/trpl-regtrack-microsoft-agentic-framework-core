/*===========================================================================
  RegTrack Insights - fan-out request grouping
  REPORT REQUEST INDEX - maps one reqId to the N runIds a fan-out POST
  (dimension_selection naming several dimensions) actually created.

  -- WHY THIS EXISTS -------------------------------------------------------
  [PRODUCT DECISION, 2026-09-11] A fan-out request generates N independent
  orchestration runs (RunEndpoints.cs), each with its own runId. The
  frontend wants ONE id to poll for the whole batch's combined progress -
  this table is what lets the request-level stream endpoint find every
  runId that belongs to a given reqId.

  -- ACCESS PATTERN: EF CORE, NOT STORED PROCS ----------------------------
  Same exception as GeneratedReport (sql/18) - plain CRUD, no scope/
  entitlement/reconciliation logic to enforce in SQL. No error block
  reserved here for the same reason: there is nothing that THROWs.

  -- ADDITIVE. Creates one new table. Touches nothing that already exists.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.InsightsReportRequest', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsReportRequest (
        ReqId          UNIQUEIDENTIFIER NOT NULL,
        RunId          NVARCHAR(200)    NOT NULL,
        CreatedAtUtc   DATETIME2(0)     NOT NULL CONSTRAINT DF_InsightsReportRequest_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_InsightsReportRequest PRIMARY KEY (ReqId, RunId)
    );
END
GO

PRINT 'InsightsReportRequest index installed.';
GO
