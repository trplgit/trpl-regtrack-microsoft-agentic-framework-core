/*===========================================================================
  RegTrack Insights - Phase 1c
  FREE DIGEST ARTIFACT - Sunday generate / Monday send split

  Spec reference : ADR-0001 (2026-09-10) - split the free weekly digest into a
                   Sunday GENERATE phase and a Monday SEND phase.
  Error block    : 51210-51219

  -- WHY THIS EXISTS ------------------------------------------------------
  Until now, FreeDigestOrchestrator resolved recipients, composed, and sent
  in one run, immediately - nothing was ever saved anywhere. The new design
  generates each tenant's digest content on Sunday, saves it (encrypted,
  in blob storage - see IDigestArtifactStore), then a separate Monday job
  reads it back and sends.

  This table is the index between those two days: which tenant, which week,
  which scope group, is the content ready, has it been dispatched yet. Blob
  storage alone cannot answer any of that - see this file's own PR discussion
  for the plain-language version of this reasoning.

  ONE ROW PER (tenant, week, scope group) - matching the digest's existing
  cost-saving unit (ScopeSignature.cs, ComposeDigestActivity's own doc
  comment): everyone sharing a scope group shares one artifact, exactly as
  they already share one LLM call today.

  -- [TRAP] THIS TABLE HAS NOTHING TO DO WITH GeneratedReport --------------
  GeneratedReport (sql/18) is the PAID pipeline's persisted-report index -
  different natural key, different retention, and already-live production
  traffic. A new, separate table keeps this change fully isolated: nothing
  here can slow down or break a paying customer's report.

  -- RecipientCountAtGeneration IS NOT A SEND LIST -------------------------
  It is observability only. The actual send list is re-resolved LIVE on
  Monday (design spec 5.3 - entitlement is evaluated at execution time,
  never cached at scheduling time). Sending whoever was on the list Sunday
  night is exactly the caching-at-scheduling-time this project's own gate
  design exists to prevent.

  -- ADDITIVE. Creates one new table and seven new procedures. Touches
     nothing that already exists.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.InsightsFreeDigestArtifact', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsFreeDigestArtifact (
        ArtifactID            UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_IFDA_ArtifactID DEFAULT (NEWID()),
        CustomerID            INT              NOT NULL,
        /*  The Sunday this artifact was generated on - same "date, not datetime,
            because two runs in the same week must collide" reasoning as
            InsightsFreeDigestLog.WeekEnding (sql/15).                          */
        WeekEnding            DATE             NOT NULL,
        /*  SHA-256 hex, ScopeSignature.For's own output shape - order-independent
            fingerprint of the (BranchID, CategoryID) pairs this artifact's
            numbers were computed for.                                          */
        ScopeSignature        CHAR(64)         NOT NULL,
        RepresentativeUserID  INT              NOT NULL,
        TenantName            NVARCHAR(200)    NOT NULL,
        /*  The clock value the aggregate windows (DueNext7/30, CompletedLast7)
            were actually anchored to - NOT the moment generation ran. See
            ADR-0001 D3: anchored to the Monday send day's start, not Sunday
            evening, so "due in the next 7 days" is not already a day stale by
            the time it is read.                                                */
        AsOfUtc               DATETIME2(0)     NOT NULL,
        GeneratedAtUtc        DATETIME2(0)     NOT NULL CONSTRAINT DF_IFDA_Generated DEFAULT (SYSUTCDATETIME()),
        Source                VARCHAR(10)      NOT NULL,   -- llm | fallback
        Status                VARCHAR(12)      NOT NULL CONSTRAINT DF_IFDA_Status DEFAULT ('pending'),  -- pending | complete | expired
        /*  Observability ONLY - see the file header. Never read to decide who
            gets mailed.                                                        */
        RecipientCountAtGeneration INT         NULL,
        BlobContainer         NVARCHAR(100)    NULL,
        BlobPath              NVARCHAR(300)    NULL,
        EncryptedAesKey       VARBINARY(MAX)   NULL,
        KeyVaultObjectName    NVARCHAR(200)    NULL,
        KeyVaultObjectVersion NVARCHAR(500)    NULL,
        KeyVaultObjectSalt    NVARCHAR(10)     NOT NULL CONSTRAINT DF_IFDA_Salt DEFAULT ('0'),
        /*  Informational only - see the file header on why this cannot double
            as an idempotency mechanism.                                        */
        DispatchedAtUtc       DATETIME2(0)     NULL,
        CONSTRAINT PK_InsightsFreeDigestArtifact PRIMARY KEY (ArtifactID),
        CONSTRAINT UQ_InsightsFreeDigestArtifact UNIQUE (CustomerID, WeekEnding, ScopeSignature)
    );

    CREATE INDEX IX_IFDA_Dispatch ON dbo.InsightsFreeDigestArtifact (CustomerID, Status, DispatchedAtUtc)
        INCLUDE (WeekEnding, GeneratedAtUtc, BlobContainer, BlobPath, EncryptedAesKey, KeyVaultObjectName, KeyVaultObjectVersion, TenantName, AsOfUtc, Source, ScopeSignature, RepresentativeUserID);

    CREATE INDEX IX_IFDA_Purge ON dbo.InsightsFreeDigestArtifact (GeneratedAtUtc)
        INCLUDE (BlobContainer, BlobPath);
END
GO

/*---------------------------------------------------------------------------
  CLAIM - atomic slot reservation before the (expensive) compose step runs.
  Returns exactly one caller Claimed = 1 for a given (customer, week, scope) -
  everyone else gets 0 and must not compose again. Same "INSERT ... WHERE NOT
  EXISTS under a unique constraint" shape as usp_Insights_FreeDigestClaimSend
  (sql/15) - an expected "already claimed" outcome must not arrive as a
  thrown duplicate-key error.

  Also reclaims a STALE pending row - one whose generation attempt started
  but never called Complete or Release, most likely because the process
  crashed mid-compose. Without this a single crash would permanently orphan
  that scope group's slot for the whole week.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactClaim', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactClaim;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestArtifactClaim
    @CustomerID           INT,
    @WeekEnding           DATE,
    @ScopeSignature       CHAR(64),
    @RepresentativeUserID INT,
    @TenantName           NVARCHAR(200),
    @StaleClaimMinutes    INT = 60
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @artifactId UNIQUEIDENTIFIER;
    DECLARE @staleCutoff DATETIME2(0) = DATEADD(MINUTE, -@StaleClaimMinutes, SYSUTCDATETIME());

    /*  Reclaim a stale pending row first - same slot, fresh attempt, rather
        than leaving a dead row blocking this scope group forever.          */
    UPDATE dbo.InsightsFreeDigestArtifact WITH (UPDLOCK, HOLDLOCK)
       SET GeneratedAtUtc = SYSUTCDATETIME(),
           @artifactId    = ArtifactID
     WHERE CustomerID = @CustomerID AND WeekEnding = @WeekEnding AND ScopeSignature = @ScopeSignature
       AND Status = 'pending' AND GeneratedAtUtc < @staleCutoff;

    IF @artifactId IS NULL
    BEGIN
        INSERT dbo.InsightsFreeDigestArtifact (CustomerID, WeekEnding, ScopeSignature, RepresentativeUserID, TenantName, AsOfUtc, Source, Status)
        SELECT @CustomerID, @WeekEnding, @ScopeSignature, @RepresentativeUserID, @TenantName, SYSUTCDATETIME(), 'fallback', 'pending'
        WHERE NOT EXISTS (
            SELECT 1 FROM dbo.InsightsFreeDigestArtifact WITH (UPDLOCK, HOLDLOCK)
            WHERE CustomerID = @CustomerID AND WeekEnding = @WeekEnding AND ScopeSignature = @ScopeSignature);

        IF @@ROWCOUNT = 1
            SELECT @artifactId = ArtifactID FROM dbo.InsightsFreeDigestArtifact
            WHERE CustomerID = @CustomerID AND WeekEnding = @WeekEnding AND ScopeSignature = @ScopeSignature;
    END

    SELECT CAST(CASE WHEN @artifactId IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS Claimed,
           @artifactId AS ArtifactID;
END
GO

/*---------------------------------------------------------------------------
  COMPLETE - the artifact was composed, encrypted and written to blob.
  Fail closed: refuses to mark complete without a real blob location.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactComplete', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactComplete;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestArtifactComplete
    @ArtifactID            UNIQUEIDENTIFIER,
    @AsOfUtc               DATETIME2(0),
    @Source                VARCHAR(10),
    @RecipientCount        INT,
    @BlobContainer         NVARCHAR(100),
    @BlobPath              NVARCHAR(300),
    @EncryptedAesKey       VARBINARY(MAX),
    @KeyVaultObjectName    NVARCHAR(200),
    @KeyVaultObjectVersion NVARCHAR(500)
AS
BEGIN
    SET NOCOUNT ON;

    IF @Source NOT IN ('llm', 'fallback')
        THROW 51215, N'FREE DIGEST ARTIFACT - unknown Source. Expected llm or fallback.', 1;

    IF @BlobContainer IS NULL OR @BlobPath IS NULL OR @EncryptedAesKey IS NULL
        THROW 51212, N'FREE DIGEST ARTIFACT - cannot mark complete without a real blob location and key.', 1;

    UPDATE dbo.InsightsFreeDigestArtifact
       SET AsOfUtc               = @AsOfUtc,
           Source                = @Source,
           RecipientCountAtGeneration = @RecipientCount,
           BlobContainer         = @BlobContainer,
           BlobPath              = @BlobPath,
           EncryptedAesKey       = @EncryptedAesKey,
           KeyVaultObjectName    = @KeyVaultObjectName,
           KeyVaultObjectVersion = @KeyVaultObjectVersion,
           Status                = 'complete'
     WHERE ArtifactID = @ArtifactID;
END
GO

/*---------------------------------------------------------------------------
  RELEASE - generation failed. Delete the pending slot so the NEXT tick (or
  a manual retry) can claim it again, rather than leaving a dead row that
  blocks this scope group for the rest of the week.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactRelease', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactRelease;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestArtifactRelease
    @ArtifactID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.InsightsFreeDigestArtifact
    WHERE ArtifactID = @ArtifactID AND Status = 'pending';
END
GO

/*---------------------------------------------------------------------------
  FOR DISPATCH - what Monday's send job asks: every COMPLETE, NOT YET
  DISPATCHED artifact for this tenant, generated within the freshness
  window. An artifact older than @MaxAgeDays is EXCLUDED here (never mailed
  stale) - the caller marks it expired separately via the purge path.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactsForDispatch', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactsForDispatch;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestArtifactsForDispatch
    @CustomerID INT,
    @MaxAgeDays INT = 3
AS
BEGIN
    SET NOCOUNT ON;

    SELECT ArtifactID, CustomerID, WeekEnding, ScopeSignature, RepresentativeUserID, TenantName,
           AsOfUtc, GeneratedAtUtc, Source, BlobContainer, BlobPath,
           EncryptedAesKey, KeyVaultObjectName, KeyVaultObjectVersion
    FROM dbo.InsightsFreeDigestArtifact
    WHERE CustomerID = @CustomerID
      AND Status = 'complete'
      AND DispatchedAtUtc IS NULL
      AND GeneratedAtUtc >= DATEADD(DAY, -@MaxAgeDays, SYSUTCDATETIME());
END
GO

/*---------------------------------------------------------------------------
  MARK DISPATCHED - informational stamp only (see the file header). Never
  read as an idempotency guard - the per-recipient claim in
  InsightsFreeDigestLog (sql/15) is still the ONLY thing that guarantees
  at-most-once delivery.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactMarkDispatched', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactMarkDispatched;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestArtifactMarkDispatched
    @ArtifactID UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.InsightsFreeDigestArtifact
       SET DispatchedAtUtc = SYSUTCDATETIME()
     WHERE ArtifactID = @ArtifactID;
END
GO

/*---------------------------------------------------------------------------
  FOR PURGE - artifacts old enough to delete. The artifact has no read
  surface other than the one Monday send - see ADR-0001 D7. Retention is an
  ops/compliance setting (FreeDigest:Artifact:RetentionDays), never a
  literal here.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactsForPurge', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactsForPurge;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestArtifactsForPurge
    @RetentionDays INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT ArtifactID, BlobContainer, BlobPath
    FROM dbo.InsightsFreeDigestArtifact
    WHERE GeneratedAtUtc < DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME());
END
GO

/*---------------------------------------------------------------------------
  DELETE - removes one artifact's INDEX ROW. The caller deletes the blob
  FIRST, then calls this - an orphaned blob wastes storage; an orphaned SQL
  row pointing at a blob that no longer exists is a dangling reference that
  fails loudly the next time anything tries to read it. Prefer the former.

  [TRAP] Fails closed against deleting something still inside its own
  retention window - a caller passing the wrong id, or a race with a
  concurrent retention-window change, must not silently delete live data.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactDelete', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactDelete;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestArtifactDelete
    @ArtifactID    UNIQUEIDENTIFIER,
    @RetentionDays INT
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1 FROM dbo.InsightsFreeDigestArtifact
        WHERE ArtifactID = @ArtifactID
          AND GeneratedAtUtc >= DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME()))
        THROW 51213, N'FREE DIGEST ARTIFACT - refused to delete a row still inside its retention window.', 1;

    DELETE FROM dbo.InsightsFreeDigestArtifact WHERE ArtifactID = @ArtifactID;
END
GO

PRINT 'Free digest artifact store installed.';
GO
