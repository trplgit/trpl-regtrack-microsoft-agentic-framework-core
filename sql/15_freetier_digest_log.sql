/*===========================================================================
  RegTrack Insights - Phase 1c
  FREE DIGEST SEND LOG - weekly-once and idempotency

  Spec reference : design doc 10.3 (weekly cadence), CLAUDE.md 6 (idempotency)
  Error block    : 51130-51139

  -- WHY THIS EXISTS ------------------------------------------------------
  CLAUDE.md 6 says "LLM activities must be idempotent - a replay must not
  double-bill tokens", and the intended mechanism is the Durable Task hub.
  But build order 9 ships the FREE TIER FIRST, before that substrate exists
  (step 11). So the free digest needs its own once-per-week guarantee, or a
  pod restart re-sends real email to real customers and re-bills the LLM.

  This is that guarantee, and it maps onto (runId, nodeId) later: the natural
  key here IS the node identity for a weekly digest.

  -- [TRAP] CLAIM BEFORE SENDING, NOT AFTER -------------------------------
  A check-then-send has a race: two workers both read "not sent", both send.
  The PRIMARY KEY makes the claim atomic instead - whoever inserts the row
  first wins, and the loser gets zero rows affected and skips.

  This deliberately biases toward UNDER-sending: a crash between claiming and
  sending loses that recipient's digest for the week. For weekly email that is
  the right trade - a missed digest is an inconvenience, a duplicate is a
  complaint, and at 600 tenants a duplicate storm is an incident.

  -- ADDITIVE. Creates one new table. Touches nothing that already exists.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.InsightsFreeDigestLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsFreeDigestLog (
        CustomerID    INT           NOT NULL,
        UserID        BIGINT        NOT NULL,
        /*  The Sunday that ends the digest's week. DATE, not DATETIME - the whole
            point is that two runs on different days of the same week collide.   */
        WeekEnding    DATE          NOT NULL,
        ClaimedAtUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_IFDL_Claimed DEFAULT (SYSUTCDATETIME()),
        SentAtUtc     DATETIME2(0)  NULL,
        Source        VARCHAR(10)   NULL,   -- llm | fallback
        ProviderUsed  VARCHAR(40)   NULL,
        Outcome       VARCHAR(20)   NULL,   -- sent | skipped | failed
        Detail        NVARCHAR(400) NULL,
        CONSTRAINT PK_InsightsFreeDigestLog PRIMARY KEY (CustomerID, UserID, WeekEnding)
    );
END
GO

/*---------------------------------------------------------------------------
  CLAIM - atomic. Returns Claimed = 1 to exactly one caller per
  (customer, user, week); every later caller gets 0 and must not send.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestClaimSend', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestClaimSend;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestClaimSend
    @CustomerID INT,
    @UserID     BIGINT,
    @WeekEnding DATE
AS
BEGIN
    SET NOCOUNT ON;

    /*  INSERT ... WHERE NOT EXISTS under the PK is the atomic part. A duplicate
        key would also work, but it raises an error for the NORMAL case of an
        already-sent recipient, and an expected condition should not arrive as an
        exception.                                                               */
    INSERT dbo.InsightsFreeDigestLog (CustomerID, UserID, WeekEnding)
    SELECT @CustomerID, @UserID, @WeekEnding
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.InsightsFreeDigestLog WITH (UPDLOCK, HOLDLOCK)
        WHERE CustomerID = @CustomerID AND UserID = @UserID AND WeekEnding = @WeekEnding);

    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS BIT) AS Claimed;
END
GO

/*---------------------------------------------------------------------------
  RECORD - what actually happened, for metrics and for support questions
  ("did this tenant get last week's digest, and was it the LLM or the template?")
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestRecordOutcome', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestRecordOutcome;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestRecordOutcome
    @CustomerID   INT,
    @UserID       BIGINT,
    @WeekEnding   DATE,
    @Outcome      VARCHAR(20),
    @Source       VARCHAR(10)   = NULL,
    @ProviderUsed VARCHAR(40)   = NULL,
    @Detail       NVARCHAR(400) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @Outcome NOT IN ('sent', 'skipped', 'failed')
        THROW 51130, N'FREE DIGEST LOG - unknown outcome. Expected sent, skipped or failed.', 1;

    UPDATE dbo.InsightsFreeDigestLog
       SET SentAtUtc    = CASE WHEN @Outcome = 'sent' THEN SYSUTCDATETIME() ELSE SentAtUtc END,
           Outcome      = @Outcome,
           Source       = @Source,
           ProviderUsed = @ProviderUsed,
           Detail       = @Detail
     WHERE CustomerID = @CustomerID AND UserID = @UserID AND WeekEnding = @WeekEnding;
END
GO

/*---------------------------------------------------------------------------
  RELEASE - hand the claim back when the send never happened, so a retry in the
  same week is possible. Without this, a failure would silently consume the
  week's only attempt.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestReleaseClaim', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestReleaseClaim;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestReleaseClaim
    @CustomerID INT,
    @UserID     BIGINT,
    @WeekEnding DATE
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.InsightsFreeDigestLog
    WHERE CustomerID = @CustomerID AND UserID = @UserID AND WeekEnding = @WeekEnding
      AND SentAtUtc IS NULL;
END
GO

PRINT 'Free digest send log installed.';
GO
