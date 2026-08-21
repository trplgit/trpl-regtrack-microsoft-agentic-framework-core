/*===========================================================================
  RegTrack Insights - Phase 1c
  DIGEST SUPPRESSION - durable per-recipient opt-out and bounce suppression

  Spec reference : design doc 5.4 (opt-out), 10.8 (bounce and unsubscribe)
  Error block    : 51140-51149

  -- [NON-NEGOTIABLE] OPT-OUT MUST SURVIVE TIER CHANGES --------------------
  Spec 5.4: opt-out is DURABLE and must survive tier changes. If it were stored
  on the product mapping, an upgrade to product 19 and back to 18 would delete
  the row and silently re-subscribe someone who asked to stop - which is both a
  trust failure and, for unsolicited mail, a legal one.

  So suppression lives in its OWN table, keyed on (CustomerID, UserID) and
  touched by nothing else. Mapping rows can come and go beneath it; this does
  not care. Nothing in this file references ProductMapping or
  UserCustomerMapping, and that is the point.

  -- ONE TABLE, TWO SOURCES ------------------------------------------------
  Unsubscribe (the recipient asked) and hard bounce (the address is dead) have
  the same effect - stop sending - and differ only in Reason. Splitting them
  into two tables would mean two places to check, and a recipient missing from
  one of them still gets mail.

  -- RE-SUBSCRIBING IS DELIBERATE ------------------------------------------
  There is a proc to remove a suppression, but it is not something the pipeline
  ever calls. Re-subscribing is an explicit administrative act, never a side
  effect of a mapping change.

  -- ADDITIVE. Creates one new table. Touches nothing that already exists.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.InsightsDigestSuppression', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsDigestSuppression (
        CustomerID       INT           NOT NULL,
        UserID           BIGINT        NOT NULL,
        /*  unsubscribed - the recipient asked
            hard_bounce  - the provider reported the address as permanently undeliverable
            manual       - suppressed by ops                                            */
        Reason           VARCHAR(20)   NOT NULL,
        SuppressedAtUtc  DATETIME2(0)  NOT NULL CONSTRAINT DF_IDS_At DEFAULT (SYSUTCDATETIME()),
        Detail           NVARCHAR(400) NULL,
        CONSTRAINT PK_InsightsDigestSuppression PRIMARY KEY (CustomerID, UserID),
        CONSTRAINT CK_IDS_Reason CHECK (Reason IN ('unsubscribed', 'hard_bounce', 'manual'))
    );
END
GO

/*---------------------------------------------------------------------------
  SUPPRESS - idempotent. Called by the unsubscribe endpoint and the bounce
  webhook, both of which live in the RegTrack API (the worker has no ingress).

  An existing suppression is NOT overwritten: the FIRST reason is the true one.
  A hard bounce arriving after someone unsubscribed must not rewrite history
  into "the address was dead", which would justify re-subscribing them later.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_DigestSuppress', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_DigestSuppress;
GO
CREATE PROCEDURE dbo.usp_Insights_DigestSuppress
    @CustomerID INT,
    @UserID     BIGINT,
    @Reason     VARCHAR(20),
    @Detail     NVARCHAR(400) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @Reason NOT IN ('unsubscribed', 'hard_bounce', 'manual')
        THROW 51140, N'DIGEST SUPPRESSION - unknown reason. Expected unsubscribed, hard_bounce or manual.', 1;

    INSERT dbo.InsightsDigestSuppression (CustomerID, UserID, Reason, Detail)
    SELECT @CustomerID, @UserID, @Reason, @Detail
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.InsightsDigestSuppression WITH (UPDLOCK, HOLDLOCK)
        WHERE CustomerID = @CustomerID AND UserID = @UserID);

    SELECT CustomerID, UserID, Reason, SuppressedAtUtc, Detail
    FROM dbo.InsightsDigestSuppression
    WHERE CustomerID = @CustomerID AND UserID = @UserID;
END
GO

/*---------------------------------------------------------------------------
  UNSUPPRESS - administrative only. The pipeline never calls this.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_DigestUnsuppress', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_DigestUnsuppress;
GO
CREATE PROCEDURE dbo.usp_Insights_DigestUnsuppress
    @CustomerID INT,
    @UserID     BIGINT
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.InsightsDigestSuppression
    WHERE CustomerID = @CustomerID AND UserID = @UserID;
END
GO

/*---------------------------------------------------------------------------
  LIST - for support ("why did this person stop receiving the digest?")
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_DigestSuppressionList', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_DigestSuppressionList;
GO
CREATE PROCEDURE dbo.usp_Insights_DigestSuppressionList
    @CustomerID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT s.CustomerID, s.UserID, u.Email, s.Reason, s.SuppressedAtUtc, s.Detail
    FROM dbo.InsightsDigestSuppression s
    LEFT JOIN [User] u ON u.ID = s.UserID
    WHERE @CustomerID IS NULL OR s.CustomerID = @CustomerID
    ORDER BY s.SuppressedAtUtc DESC;
END
GO

PRINT 'Digest suppression store installed.';
GO
