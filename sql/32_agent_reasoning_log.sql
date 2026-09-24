/*===========================================================================
  RegTrack Insights - agent reasoning capture

  AGENT REASONING LOG

  Error block    : 51150-51159
  [FIXED 2026-09-21] The purge guard below originally used 51060/51061 - those
  codes are already live and permanently owned by sql/08_dimension_risk.sql
  (SCOPE DENIED / RECONCILIATION FAILED). Renumbered into 51150-51159, a
  genuinely free block per CLAUDE.md's own allocation table - same
  never-validate-a-second-file-by-memory reasoning that renumbered this same
  file from sql/31 to sql/32 in the first place (real collision, found before
  deploy, not after).

  -- WHY THIS EXISTS -------------------------------------------------------
  Every LLM-calling agent (Narrate, NarrativeReflection, ReportHtml) can now
  request the vendor's own summary of its reasoning for that call
  (MafAgentFactory sets ResponseReasoningSummaryVerbosity.Auto). That text
  is a SUMMARY the model wrote about itself, never the raw chain-of-thought -
  OpenAI's terms forbid extracting raw reasoning by any other means.

  This table is where that summary lives PERMANENTLY. It is deliberately
  NOT dt.Payloads (the Durable Task hub's own storage): that table is real,
  already measured at 232MB+ for a small test population, and is slated for
  a future purge job. Anything meant to survive that purge needs a home
  outside DTFx entirely - this is that home.

  -- APPEND-ONLY, ONE ROW PER REAL LLM CALL --------------------------------
  Never UPDATEd. No idempotency guard on RunId (unlike sql/20's token
  ledger): a single RunId can legitimately produce several rows for the
  SAME stage - RenderHtmlActivity's own retry loop (up to 3 attempts) makes
  a genuinely new LLM call, with its own genuinely new reasoning, on each
  retry. Deduplicating on RunId+Stage would throw away real, distinct
  reasoning from real, distinct calls.

  -- [RENUMBERED 2026-09-13] Was sql/31, which collided with
     31_freetier_insight_json.sql. Two files sharing a number in a sequenced
     deployment resolve by alphabetical luck, not by design. This is 32.

  -- [ADDED 2026-09-13] RETENTION -----------------------------------------
  The header above correctly identifies unbounded growth as the reason this
  table exists apart from dt.Payloads - and then, as first written, reproduced
  it: append-only, NVARCHAR(MAX), no purge, no dedup, forever.

  Rough shape of the growth: 3 LLM stages, up to 3 attempts each, per run, per
  tenant, weekly. At ~600 entitled tenants that is up to ~5,400 unbounded-length
  rows a week and roughly 280,000 a year. dt.Payloads reached 232MB on a SMALL
  TEST population; this table serves the whole book.

  Retention is an ops/compliance setting, never a literal here. The 90-day
  FLOOR below is deliberately much shorter than the snapshot table's 400 days,
  because these two tables answer different questions:

      InsightsMetricSnapshot   - a TREND spine. Deleting last year destroys the
                                 year-over-year comparison it exists for.
      InsightsAgentReasoningLog - an INCIDENT record. It answers "why did the
                                 agent say that, in the report a customer is
                                 querying?" That question arrives days or weeks
                                 after a send, not a year.

  90 days spans a full quarterly reporting cycle plus a support tail. Shorter
  than that and a customer querying last quarter's report finds nothing.

  IDEMPOTENT (install script). Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.InsightsAgentReasoningLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsAgentReasoningLog (
        Id                BIGINT IDENTITY(1,1) NOT NULL,
        RunId             NVARCHAR(200) NOT NULL,
        Stage             NVARCHAR(50) NOT NULL,
        ReasoningSummary  NVARCHAR(MAX) NOT NULL,
        RecordedAtUtc     DATETIME2(0) NOT NULL CONSTRAINT DF_InsightsAgentReasoningLog_RecordedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_InsightsAgentReasoningLog PRIMARY KEY CLUSTERED (Id)
    );

    -- The only query this table is expected to serve today: every reasoning
    -- row for one run, in call order.
    CREATE NONCLUSTERED INDEX IX_InsightsAgentReasoningLog_RunId
        ON dbo.InsightsAgentReasoningLog (RunId, Id);
END
GO

/*  [ADDED 2026-09-13] The purge reads RecordedAtUtc across the whole table.
    Without this index that is a full scan of an NVARCHAR(MAX) heap - the one
    operation guaranteed to run against the largest version of this table.   */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('dbo.InsightsAgentReasoningLog')
                 AND name = 'IX_InsightsAgentReasoningLog_Purge')
    CREATE NONCLUSTERED INDEX IX_InsightsAgentReasoningLog_Purge
        ON dbo.InsightsAgentReasoningLog (RecordedAtUtc);
GO

/*---------------------------------------------------------------------------
  PURGE - deletes in batches, oldest first.

  [TRAP] Batched, not one statement. A single DELETE over hundreds of
  thousands of NVARCHAR(MAX) rows takes a lock escalation to the whole table
  and blocks every agent trying to WRITE its reasoning while the purge runs.
  Small committed batches keep the writers moving.

  Returns the row count so a scheduled job can log it and alert if the number
  stops falling - a purge that silently deletes nothing looks identical to a
  purge that has nothing to do.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_AgentReasoningPurge', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_AgentReasoningPurge;
GO
CREATE PROCEDURE dbo.usp_Insights_AgentReasoningPurge
    @RetentionDays INT,
    @BatchSize     INT = 5000,
    @MaxBatches    INT = 100        -- bounded: a purge must never run unbounded
AS
BEGIN
    SET NOCOUNT ON;

    IF @RetentionDays < 90
        THROW 51150, N'AGENT REASONING LOG - retention below 90 days would discard the reasoning behind reports a customer may still be querying. Raise the retention or change this guard deliberately.', 1;

    IF @BatchSize < 1 OR @BatchSize > 50000
        THROW 51151, N'AGENT REASONING LOG - BatchSize must be between 1 and 50000. Large batches escalate to a table lock and block agents writing their reasoning.', 1;

    DECLARE @cutoff DATETIME2(0) = DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME());
    DECLARE @total INT = 0, @batch INT = 1, @n INT = 1;

    WHILE @n > 0 AND @batch <= @MaxBatches
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.InsightsAgentReasoningLog
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

/*---------------------------------------------------------------------------
  SIZE - so the growth is visible BEFORE it becomes a migration.

  dt.Payloads reached 232MB unnoticed. This proc exists so the same thing
  cannot happen quietly here: point a monitor at TotalMB and the problem
  announces itself.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_AgentReasoningSize', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_AgentReasoningSize;
GO
CREATE PROCEDURE dbo.usp_Insights_AgentReasoningSize
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        (SELECT COUNT(*) FROM dbo.InsightsAgentReasoningLog)                      AS Rows_,
        (SELECT MIN(RecordedAtUtc) FROM dbo.InsightsAgentReasoningLog)            AS OldestUtc,
        (SELECT MAX(RecordedAtUtc) FROM dbo.InsightsAgentReasoningLog)            AS NewestUtc,
        (SELECT COUNT(DISTINCT RunId) FROM dbo.InsightsAgentReasoningLog)         AS DistinctRuns,
        CAST((SELECT ISNULL(SUM(a.total_pages), 0) * 8.0 / 1024.0
              FROM sys.partitions p
              JOIN sys.allocation_units a ON a.container_id = p.hobt_id
              WHERE p.object_id = OBJECT_ID('dbo.InsightsAgentReasoningLog')) AS DECIMAL(12,1)) AS TotalMB,
        CAST((SELECT AVG(CAST(LEN(ReasoningSummary) AS BIGINT))
              FROM dbo.InsightsAgentReasoningLog) AS BIGINT)                      AS AvgSummaryChars,
        /*  Rows per run is the multiplier that decides how fast this grows.
            Above ~9 (3 stages x 3 retries) something is retrying more than
            the retry policy allows.                                          */
        CAST((SELECT 1.0 * COUNT(*) / NULLIF(COUNT(DISTINCT RunId), 0)
              FROM dbo.InsightsAgentReasoningLog) AS DECIMAL(9,2))               AS AvgRowsPerRun;
END
GO

PRINT 'InsightsAgentReasoningLog installed.';
GO
