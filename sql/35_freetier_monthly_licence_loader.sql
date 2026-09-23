/*===========================================================================
  RegTrack Insights - Free tier, MONTHLY edition
  Error block 51240-51249.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.
  (Window-input contract failures sit in x5-x7, as in sql/34.)

  Codes in use: 51240 scope | 51241 fan-out | 51245-51247 window input |
  51248 LicenceStatus not seeded | 51249 LicenceStatus unmapped.
  (51242 RETIRED 2026-09-20 - a licence type id absent from the type master is
   a data gap, declared as untyped, not a refusal. See section 3 below.)

  SHARED LICENCE LOADER for the monthly free tier
  (sql/36 Overview - one licence line every month; sql/41 Licence slot).

  STATUS : RUN ON UAT 2026-09-20 (sql/42, tenants 5 / 23 / 1285 / 1355).
           This revision retires 51242 - REDEPLOY THIS FILE.

  -- SOURCE AND RULINGS -----------------------------------------------------
  The Lic_tbl_* licence register, with the BA rulings of sql/21 replicated
  exactly. sql/21 is NOT called and NOT modified.

    - EndDate decides a lapse. Status only EXCLUDES licences that ended another
      way: terminated, rejected, renewed (EndDate superseded), not_applicable.
    - A licence with NO status row is still lapse-eligible - absence is not
      exclusion (sql/21: 15 licences dropped silently without this rule).
    - Lic_tbl_StatusMaster has duplicate / whitespace-variant names. Bucket by
      ID via InsightsEnumPolarity (Semantic = 'LicenceStatus'), never by name.
    - Renewal in progress = latest status bucket 'in_progress'. sql/01 records
      why the split matters: of 1,554 lapsed licences, 311 had a renewal in
      progress and 1,228 showed no visible action. The second number is the
      finding; a single lapse count hides it.

  -- [DEFECT NOTED, NOT FIXED HERE] ------------------------------------------
  sql/06 (today's weekly free email) still counts "licences lapsing" as
  Compliance.ComplianceType = 2 on compliance schedules. sql/21's header
  records that definition as the one it was built to replace, and says sql/06
  "was fixed separately" - the file shows it was not. The monthly tier uses
  the licence register. sql/06 is left untouched on instruction; reported.

  -- SCOPE: BRANCH-ONLY, identical to sql/21 ---------------------------------
  Licences are not linked to a compliance category without
  Lic_tbl_LicenseComplianceInstanceMapping. A recipient never sees a licence
  outside their authorised branches, but this is a narrower guarantee than the
  2-D compliance scope. Every caller declares it in data_quality.

  -- CONTRACT: THE CALLER CREATES, THIS PROC FILLS, NO RESULT SET ----------

    CREATE TABLE #lic (
        LicenseId          BIGINT        NOT NULL PRIMARY KEY,
        BranchID           INT           NOT NULL,
        LicenseTypeID      BIGINT        NULL,
        LicenseTypeName    NVARCHAR(MAX) NULL,
        EndDate            DATETIME      NULL,
        StatusBucket       NVARCHAR(200)  NULL,     -- NULL = no status row at all
        LicenceState       VARCHAR(12)   NOT NULL, -- valid | lapsed | ended_other | no_end_date
        RenewalInProgress  BIT           NOT NULL,
        LapsesRestOfMonth  BIT           NOT NULL, -- valid now, EndDate before month end, lapse-eligible
        LapsedThisMonth    BIT           NOT NULL, -- lapsed, EndDate in [CurrMonthStart, AsOf)
        LapsedLastMonth    BIT           NOT NULL  -- lapsed, EndDate in the previous calendar month
    );

  LicenceState partitions the set exactly:
      no_end_date   EndDate IS NULL                      (unassessable - declared)
      valid         EndDate >= @AsOf
      lapsed        EndDate <  @AsOf AND lapse-eligible
      ended_other   EndDate <  @AsOf AND terminated / rejected / renewed / n.a.

  IDEMPOTENT. PURE ASCII (CLAUDE.md Sec.5a). Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_LoadLicences', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeMonthly_LoadLicences;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_LoadLicences
    @UserID          INT,
    @CustomerID      INT,
    @CurrMonthStart  DATE,
    @AsOf            DATETIME
AS
BEGIN
    SET NOCOUNT ON;

    /*-- 0. INPUT CONTRACT ---------------------------------------------------*/
    IF @UserID IS NULL OR @CustomerID IS NULL OR @CurrMonthStart IS NULL OR @AsOf IS NULL
        THROW 51245, N'FREE MONTHLY LICENCES - WINDOW INPUT MISSING: @UserID, @CustomerID, @CurrMonthStart and @AsOf are all required. Refusing to compute.', 1;

    IF DATEPART(DAY, @CurrMonthStart) <> 1
        THROW 51246, N'FREE MONTHLY LICENCES - @CurrMonthStart IS NOT THE FIRST OF A MONTH. Refusing to compute.', 1;

    DECLARE @CurrStart      DATETIME = CAST(@CurrMonthStart AS DATETIME);
    DECLARE @PrevMonthStart DATETIME = DATEADD(MONTH, -1, @CurrStart);
    DECLARE @NextMonthStart DATETIME = DATEADD(MONTH,  1, @CurrStart);

    IF @AsOf < @CurrStart OR @AsOf >= @NextMonthStart
        THROW 51247, N'FREE MONTHLY LICENCES - @AsOf FALLS OUTSIDE THE EDITION MONTH (likely a UTC vs local clock mismatch). Refusing to compute.', 1;

    /*-- 1. SCOPE ------------------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51240, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    /*-- 2. LicenceStatus classification, BY ID, from the dictionary ----------*/
    IF OBJECT_ID('tempdb..#ll_status') IS NOT NULL DROP TABLE #ll_status;
    SELECT TRY_CAST(p.RawValue AS INT) AS StatusId,
           p.Meaning                   AS Bucket,
           CAST(CASE WHEN p.Meaning IN (N'terminated', N'rejected', N'renewed', N'not_applicable')
                     THEN 0 ELSE 1 END AS BIT) AS LapseEligible
    INTO #ll_status
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'LicenceStatus'
      AND TRY_CAST(p.RawValue AS INT) IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM #ll_status)
        THROW 51248, N'DICTIONARY GAP - no Lic_tbl_StatusMaster ids are classified under Semantic=LicenceStatus in InsightsEnumPolarity. Names on that table are duplicated, so a name lookup is unsafe. Seed the classification and re-run. Refusing to compute.', 1;

    IF EXISTS (SELECT 1 FROM Lic_tbl_StatusMaster sm
               LEFT JOIN #ll_status s ON s.StatusId = sm.ID
               WHERE sm.IsDeleted = 0 AND s.StatusId IS NULL)
        THROW 51249, N'DICTIONARY GAP - Lic_tbl_StatusMaster has an active status absent from the LicenceStatus classification. Refusing to guess whether it is lapse-eligible. Seed it and re-run.', 1;

    /*-- 3. BRANCH-ONLY SCOPE, identical to sql/21 ------------------------------*/
    IF OBJECT_ID('tempdb..#ll_branch') IS NOT NULL DROP TABLE #ll_branch;
    SELECT DISTINCT sp.BranchID
    INTO #ll_branch
    FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp;
    CREATE CLUSTERED INDEX IX_ll_branch ON #ll_branch (BranchID);

    /*  [CORRECTED 2026-09-20, FOUND ON UAT - sql/42, tenant 5] A licence whose
        LicenseTypeID has no row in Lic_tbl_LicenseType_Master USED TO THROW
        51242 here as a referential break. That was the wrong tier: it is a
        property of the DATA, not of the code, so by CLAUDE.md Sec.11 it must
        warn, not refuse - and refusing took out the whole Overview email (the
        first Sunday of the month) for every tenant holding one such licence.

        An unusable type id is therefore treated exactly as "no type": the
        licence keeps counting in every licence total, is left out of the
        per-type comparison (licence_type_lapse_rate), and is declared in the
        callers' data_quality grid under untyped_licences. Same stance as
        licences with no EndDate and obligations whose RiskType is unmapped.

        The genuine invariants stay: 51241 (fan-out) and the callers' 51301 /
        51302 / 51303 (partition, per-location sums, typed + untyped) all still
        THROW - those mean the code is wrong.                                 */
    IF OBJECT_ID('tempdb..#ll_base') IS NOT NULL DROP TABLE #ll_base;
    SELECT li.ID AS LicenseId,
           li.CustomerBranchID AS BranchID,
           lt.ID AS LicenseTypeID,          -- NULL when the id is absent from the type master
           li.EndDate
    INTO #ll_base
    FROM Lic_tbl_LicenseInstance li
    JOIN #ll_branch b ON b.BranchID = li.CustomerBranchID
    LEFT JOIN Lic_tbl_LicenseType_Master lt ON lt.ID = NULLIF(li.LicenseTypeID, -1)
    WHERE li.CustomerID = @CustomerID
      AND li.IsDeleted  = 0;
    CREATE CLUSTERED INDEX IX_ll_base ON #ll_base (LicenseId);

    /*-- 4. LATEST STATUS PER LICENCE -------------------------------------------
       ID DESC is a tie-break on equal StatusChangeOn so the pick is
       deterministic. [VOLUME] Some licences carry a status row for every
       calendar day (sql/21); correctness is unaffected, cost is not - Vinay
       to check row volume on large tenants (sql/42).                        */
    IF OBJECT_ID('tempdb..#ll_latest') IS NOT NULL DROP TABLE #ll_latest;
    ;WITH ranked AS (
        SELECT lst.LicenseID, lst.StatusID,
               ROW_NUMBER() OVER (PARTITION BY lst.LicenseID
                                  ORDER BY lst.StatusChangeOn DESC, lst.ID DESC) AS rn
        FROM Lic_tbl_LicenseStatusTransaction lst
        JOIN #ll_base l ON l.LicenseId = lst.LicenseID
    )
    SELECT LicenseID, StatusID
    INTO #ll_latest
    FROM ranked
    WHERE rn = 1;
    CREATE CLUSTERED INDEX IX_ll_latest ON #ll_latest (LicenseID);

    /*-- 5. FILL THE CALLER'S #lic -----------------------------------------------*/
    INSERT #lic (LicenseId, BranchID, LicenseTypeID, LicenseTypeName, EndDate, StatusBucket,
                 LicenceState, RenewalInProgress, LapsesRestOfMonth, LapsedThisMonth, LapsedLastMonth)
    SELECT
        l.LicenseId,
        l.BranchID,
        l.LicenseTypeID,
        lt.Name,
        l.EndDate,
        s.Bucket,
        CASE WHEN l.EndDate IS NULL                           THEN 'no_end_date'
             WHEN l.EndDate >= @AsOf                          THEN 'valid'
             WHEN ISNULL(CAST(s.LapseEligible AS INT), 1) = 1 THEN 'lapsed'
             ELSE 'ended_other' END,
        CAST(CASE WHEN s.Bucket = N'in_progress' THEN 1 ELSE 0 END AS BIT),
        CAST(CASE WHEN l.EndDate >= @AsOf AND l.EndDate < @NextMonthStart
                   AND ISNULL(CAST(s.LapseEligible AS INT), 1) = 1 THEN 1 ELSE 0 END AS BIT),
        CAST(CASE WHEN l.EndDate >= @CurrStart AND l.EndDate < @AsOf
                   AND ISNULL(CAST(s.LapseEligible AS INT), 1) = 1 THEN 1 ELSE 0 END AS BIT),
        CAST(CASE WHEN l.EndDate >= @PrevMonthStart AND l.EndDate < @CurrStart
                   AND ISNULL(CAST(s.LapseEligible AS INT), 1) = 1 THEN 1 ELSE 0 END AS BIT)
    FROM #ll_base l
    LEFT JOIN Lic_tbl_LicenseType_Master lt ON lt.ID = l.LicenseTypeID
    LEFT JOIN #ll_latest ls                 ON ls.LicenseID = l.LicenseId
    LEFT JOIN #ll_status s                  ON s.StatusId  = ls.StatusID;

    /*-- 6. STRUCTURAL: one #lic row per scoped licence (no fan-out) -----------*/
    IF (SELECT COUNT(*) FROM #lic) <> (SELECT COUNT(*) FROM #ll_base)
        THROW 51241, N'FREE MONTHLY LICENCE RECONCILIATION FAILED - loaded licence rows do not tie to the scoped licence base (join fan-out). Refusing to publish.', 1;

    DROP TABLE #ll_latest;
    DROP TABLE #ll_base;
    DROP TABLE #ll_branch;
    DROP TABLE #ll_status;
END
GO

PRINT 'Free monthly licence loader installed (usp_Insights_FreeMonthly_LoadLicences).';
GO
