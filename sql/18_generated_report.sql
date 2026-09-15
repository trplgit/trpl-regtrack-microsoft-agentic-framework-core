/*===========================================================================
  RegTrack Insights - Phase 1d, build order item 14
  GENERATED REPORT INDEX - the SQL half of the two-store persistence model

  Spec reference : design doc Sec.9.1 (two-store model), Sec.9.2 (envelope encryption)

  -- WHY THIS EXISTS -------------------------------------------------------
  Design doc Sec.9.1: the rendered report HTML (real names, emails, account
  identifiers - genuine PII) lives ENCRYPTED in blob storage, never in SQL.
  This table is the metadata-only index: which report, for which tenant,
  where the encrypted blob is, and what it takes to decrypt it. No PII here.

  -- ACCESS PATTERN: EF CORE, NOT STORED PROCS ----------------------------
  Every other table in this repo is proc-fronted (CLAUDE.md 7: "Dapper for
  the read path"). This one is the sole exception (CLAUDE.md 7: "EF Core
  only for the GeneratedReport index row") - it is plain CRUD with no
  scope/entitlement/reconciliation logic to enforce in SQL, so a proc layer
  would add nothing. No error block reserved here for the same reason: there
  is nothing that THROWs.

  -- ADDITIVE. Creates one new table. Touches nothing that already exists.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.GeneratedReport', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.GeneratedReport (
        Id                    UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_GeneratedReport_Id DEFAULT (NEWID()),
        CustomerId            INT              NOT NULL,
        ScopeDescriptor       NVARCHAR(200)    NOT NULL,
        ReportType            NVARCHAR(50)     NOT NULL,
        Period                NVARCHAR(50)     NOT NULL,
        GeneratedAtUtc        DATETIME2(0)     NOT NULL CONSTRAINT DF_GeneratedReport_GeneratedAt DEFAULT (SYSUTCDATETIME()),
        GeneratedByUserId     INT              NOT NULL,
        BlobContainer         NVARCHAR(100)    NOT NULL,
        BlobPath              NVARCHAR(300)    NOT NULL,
        /*  queued | complete | failed - matches API_CONTRACTS.md 4's status vocabulary,
            not the free-standing "GENERATION_FAILED" error code (that one belongs to the
            job, not this row - a row only exists once persistence itself succeeded).      */
        Status                NVARCHAR(20)     NOT NULL,
        /*  Envelope encryption (Sec.9.2, DocAI pattern) - the wrapped per-blob AES key and
            the Key Vault key identity used to wrap it. The IV is NOT a separate column: it
            is prepended to the blob's own byte stream (DocAI convention), so the decrypt
            path reads it back from there, not from this row.                              */
        EncryptedAesKey       VARBINARY(MAX)   NOT NULL,
        KeyVaultObjectName    NVARCHAR(200)    NOT NULL,
        KeyVaultObjectVersion NVARCHAR(500)    NOT NULL,
        /*  Always the literal string '0' - confirmed against the real DocAI source
            (ComplianceFileUploadService.cs): a vestigial metadata field, never a real
            cryptographic salt (this scheme uses an IV, not a KDF). Kept only so the
            metadata shape matches the existing DocAI convention byte-for-byte.             */
        KeyVaultObjectSalt    NVARCHAR(10)     NOT NULL CONSTRAINT DF_GeneratedReport_Salt DEFAULT ('0'),
        CONSTRAINT PK_GeneratedReport PRIMARY KEY (Id)
    );

    /*  The history-list and cooldown-check query pattern: "this tenant's reports of this
        type/scope, newest first". Not built yet (item 14's read path, deferred) but the
        index costs nothing to add now and everything to add later under load.            */
    CREATE INDEX IX_GeneratedReport_Lookup
        ON dbo.GeneratedReport (CustomerId, ReportType, ScopeDescriptor, Period, GeneratedAtUtc DESC);
END
GO

PRINT 'GeneratedReport index installed.';
GO
