/*===========================================================================
  RegTrack Insights - tool invocation log

  TOOL INVOCATION LOG

  Error block    : 51160-51169 (next free block after sql/32's 51150-51159,
  per CLAUDE.md's own allocation table)

  -- WHY THIS EXISTS -------------------------------------------------------
  [ADDED 2026-09-23] Real gap found live: MafAnalystNarrativeAgent's
  ReadOnlySqlFetchTool and TenantMemoryTool.WriteTenantMemoryAsync both have
  the CAPABILITY to be observed (onSqlToolInvoked / the write callback), but
  production DI wired the callback to null (PaidReportAgentsRegistration.cs)
  - so for every real report generated so far, there is no record anywhere
  of whether either tool actually fired for a given run. This table is that
  record, permanent, per real call - same reasoning and same home as
  sql/32_agent_reasoning_log.sql (NOT dt.Payloads, which is slated for a
  future purge and already measured at 232MB+ on a small test population).

  -- APPEND-ONLY, ONE ROW PER REAL TOOL CALL --------------------------------
  Never UPDATEd. A single RunId can produce zero, one, or several rows -
  ReadOnlySqlFetchTool allows up to 3 calls per narrate call
  (ReadOnlySqlFetchTool.MaxCalls), and most runs are expected to produce
  ZERO rows (the model calling the tool at all is the rare case, not the
  norm - confirmed live 2026-09-22, 7/7 natural runs never fired it). Zero
  rows for a RunId is the expected, common case, not a logging gap.

  -- RETENTION --------------------------------------------------------------
  Same 90-day floor and same reasoning as sql/32: this answers "did the
  agent actually call the tool for the report a customer is querying,
  and what did it ask" - an incident record, not a trend spine.

  IDEMPOTENT (install script). Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.InsightsToolInvocationLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsToolInvocationLog (
        Id             BIGINT IDENTITY(1,1) NOT NULL,
        RunId          NVARCHAR(200) NOT NULL,
        Stage          NVARCHAR(50) NOT NULL,
        ToolName       NVARCHAR(100) NOT NULL,
        -- The SQL text for fetch_scoped_sql_data, or a short description for
        -- write_tenant_memory (e.g. the dimension name) - never the raw memory
        -- content itself, same "don't echo the model's full free text into a
        -- permanent table beyond what's needed to diagnose" restraint
        -- ReadOnlySqlFetchTool's own error-message handling already applies.
        Detail         NVARCHAR(MAX) NOT NULL,
        Success        BIT NOT NULL,
        ResultLength   INT NULL,
        RecordedAtUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_InsightsToolInvocationLog_RecordedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_InsightsToolInvocationLog PRIMARY KEY CLUSTERED (Id)
    );

    -- The only query this table is expected to serve today: every tool call
    -- for one run, in call order - "did the agent call the tool for THIS run."
    CREATE NONCLUSTERED INDEX IX_InsightsToolInvocationLog_RunId
        ON dbo.InsightsToolInvocationLog (RunId, Id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('dbo.InsightsToolInvocationLog')
                 AND name = 'IX_InsightsToolInvocationLog_Purge')
    CREATE NONCLUSTERED INDEX IX_InsightsToolInvocationLog_Purge
        ON dbo.InsightsToolInvocationLog (RecordedAtUtc);
GO

/*---------------------------------------------------------------------------
  PURGE - same batched-delete shape as usp_Insights_AgentReasoningPurge.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_ToolInvocationPurge', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_ToolInvocationPurge;
GO
CREATE PROCEDURE dbo.usp_Insights_ToolInvocationPurge
    @RetentionDays INT,
    @BatchSize     INT = 5000,
    @MaxBatches    INT = 100
AS
BEGIN
    SET NOCOUNT ON;

    IF @RetentionDays < 90
        THROW 51160, N'TOOL INVOCATION LOG - retention below 90 days would discard the evidence behind a report a customer may still be querying. Raise the retention or change this guard deliberately.', 1;

    IF @BatchSize < 1 OR @BatchSize > 50000
        THROW 51161, N'TOOL INVOCATION LOG - BatchSize must be between 1 and 50000. Large batches escalate to a table lock and block tools writing their invocation record.', 1;

    DECLARE @cutoff DATETIME2(0) = DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME());
    DECLARE @total INT = 0, @batch INT = 1, @n INT = 1;

    WHILE @n > 0 AND @batch <= @MaxBatches
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.InsightsToolInvocationLog
        WHERE RecordedAtUtc < @cutoff;

        SET @n = @@ROWCOUNT;
        SET @total = @total + @n;
        SET @batch = @batch + 1;
    END

    SELECT @total       AS RowsPurged,
           @batch - 1   AS BatchesRun,
           @cutoff      AS CutoffUtc,
           CAST(CASE WHEN @batch > @MaxBatches AND @n > 0 THEN 1 ELSE 0 END AS BIT) AS MoreRemaining;
END
GO

PRINT 'InsightsToolInvocationLog installed.';
GO
