/*===========================================================================
  RegTrack Insights - agent reasoning capture

  AGENT REASONING LOG

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

PRINT 'InsightsAgentReasoningLog installed.';
GO
