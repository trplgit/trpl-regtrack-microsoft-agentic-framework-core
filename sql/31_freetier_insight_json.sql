/*===========================================================================
  RegTrack Insights - Phase 1c
  FREE DIGEST INSIGHT JSON POST LOG - weekly-once and idempotency

  Spec reference : ADR-0002 (2026-09-11) - weekly per-user "current insight"
                   JSON posted to an external API, sibling to the free digest
                   email (ADR-0001).
  Error block    : 51220-51229

  -- WHY THIS EXISTS ------------------------------------------------------
  Same reasoning as InsightsFreeDigestLog (sql/15): CLAUDE.md 6 requires every
  Durable Task activity to be idempotent, and an external HTTP POST has no
  built-in at-most-once guarantee of its own - a replay after a lost ack must
  not double-POST the same user's insight for the same week.

  ONE ROW PER (customer, user, week) - this pipeline is per-recipient, unlike
  the email digest's per-scope-group artifact (sql/29). The narrative content
  itself is never persisted here or anywhere else; only the claim and the
  outcome are recorded, because the JSON is POSTed once and never re-read by
  this system.

  -- [TRAP] CLAIM BEFORE POSTING, NOT AFTER -------------------------------
  Same atomic-INSERT-under-the-PK shape as sql/15 - a check-then-post races
  between concurrent workers, the PRIMARY KEY does not.

  -- ADDITIVE. Creates one new table and three new procedures. Touches
     nothing that already exists.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.InsightsFreeDigestJsonLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsFreeDigestJsonLog (
        CustomerID    INT           NOT NULL,
        UserID        BIGINT        NOT NULL,
        /*  The Sunday that ends the insight's week - DATE, not DATETIME, same
            "two runs in the same week must collide" reasoning as sql/15.    */
        WeekEnding    DATE          NOT NULL,
        ClaimedAtUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_IFDJL_Claimed DEFAULT (SYSUTCDATETIME()),
        PostedAtUtc   DATETIME2(0)  NULL,
        Outcome       VARCHAR(20)   NULL,   -- posted | failed
        Detail        NVARCHAR(400) NULL,
        CONSTRAINT PK_InsightsFreeDigestJsonLog PRIMARY KEY (CustomerID, UserID, WeekEnding)
    );
END
GO

/*---------------------------------------------------------------------------
  CLAIM - atomic. Returns Claimed = 1 to exactly one caller per
  (customer, user, week); every later caller gets 0 and must not POST.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_InsightJsonClaimPost', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_InsightJsonClaimPost;
GO
CREATE PROCEDURE dbo.usp_Insights_InsightJsonClaimPost
    @CustomerID INT,
    @UserID     BIGINT,
    @WeekEnding DATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT dbo.InsightsFreeDigestJsonLog (CustomerID, UserID, WeekEnding)
    SELECT @CustomerID, @UserID, @WeekEnding
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.InsightsFreeDigestJsonLog WITH (UPDLOCK, HOLDLOCK)
        WHERE CustomerID = @CustomerID AND UserID = @UserID AND WeekEnding = @WeekEnding);

    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS BIT) AS Claimed;
END
GO

/*---------------------------------------------------------------------------
  RECORD - what actually happened, for observability and support questions
  ("did this recipient's insight JSON post last week?").
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_InsightJsonRecordOutcome', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_InsightJsonRecordOutcome;
GO
CREATE PROCEDURE dbo.usp_Insights_InsightJsonRecordOutcome
    @CustomerID INT,
    @UserID     BIGINT,
    @WeekEnding DATE,
    @Outcome    VARCHAR(20),
    @Detail     NVARCHAR(400) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @Outcome NOT IN ('posted', 'failed')
        THROW 51220, N'FREE DIGEST INSIGHT JSON - unknown outcome. Expected posted or failed.', 1;

    UPDATE dbo.InsightsFreeDigestJsonLog
       SET PostedAtUtc = CASE WHEN @Outcome = 'posted' THEN SYSUTCDATETIME() ELSE PostedAtUtc END,
           Outcome     = @Outcome,
           Detail      = @Detail
     WHERE CustomerID = @CustomerID AND UserID = @UserID AND WeekEnding = @WeekEnding;
END
GO

/*---------------------------------------------------------------------------
  RELEASE - hand the claim back when the POST never succeeded (transient
  failure), so a retry within the same week is possible. Without this a
  transient error would silently consume the week's only attempt.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_InsightJsonReleaseClaim', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_InsightJsonReleaseClaim;
GO
CREATE PROCEDURE dbo.usp_Insights_InsightJsonReleaseClaim
    @CustomerID INT,
    @UserID     BIGINT,
    @WeekEnding DATE
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.InsightsFreeDigestJsonLog
    WHERE CustomerID = @CustomerID AND UserID = @UserID AND WeekEnding = @WeekEnding
      AND PostedAtUtc IS NULL;
END
GO

PRINT 'Free digest insight JSON post log installed.';
GO
