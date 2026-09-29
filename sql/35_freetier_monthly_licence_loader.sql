/*===========================================================================
  RegTrack Insights - Free tier, MONTHLY edition
  Error block 51240-51249.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.
  (Window-input contract failures sit in x5-x7, as in sql/34.)

  Codes in use: 51240 scope | 51241 fan-out |
  51244 dashboard licence status rules not seeded |
  51245-51247 window input | 51248 LicenceStatus not seeded |
  51249 LicenceStatus unmapped.
  (51243 RETIRED 2026-09-29 - it was "malformed @AllowedBranches"; that
   parameter no longer exists.)
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

  -- WHICH LICENCES: REGTRACK 2.0 PARITY (2026-09-29) --------------------------
  Product-owner decision: the licence set must equal the one RegTrack 2.0's
  licence dashboard counts for the same user (SP_LicenseInstanceTransactionCount,
  MGMT path, UAT source read 2026-09-29, plus the licence API's C# post-filter
  RoleID == 3). A licence counts only when ALL of these hold:
    - LIC_EntitiesAssignment gives the user its (branch, licence type) - the
      LICENCE scope, not the compliance EntitiesAssignment used by sql/34
    - the licence is not deleted; its branch is active (IsDeleted 0, Status 1)
    - its licence type exists in Lic_tbl_LicenseType_Master and is not deleted
    - its LATEST status row (by CreatedOn, as RecentLicenseTransactionView
      picks it) is active (IsActive = 1)
    - that status row's compliance schedule is mapped to the licence
      (Lic_tbl_LicenseComplianceInstanceScheduleOnMapping), is active and
      not upcoming-deleted, has at least one ComplianceTransaction, and its
      mapped instance has a performer (ComplianceAssignment RoleID 3)
  The dashboard counts one row per licence x assignee; this loader keeps ONE
  row per licence (51241), so a licence with two performers counts once.

  [SUPERSEDED FOR THE FREE TIER] "A licence with NO status row is still
  lapse-eligible", "an untyped licence still counts" and "EndDate decides a
  lapse" (sql/21 rulings above) no longer apply - the dashboard requires a
  status row and a type, and it buckets by the latest STATUS, so this loader
  does too.

  -- STATE: THE DASHBOARD'S BUCKETS, PLUS THE DATE LINES (2026-09-29) -------
  LicenceState follows the dashboard (by status id, via
  dbo.InsightsFreeDashboardStatusRule, seeded by sql/34 - never a name):
      valid         latest status Active or Expiring   (dashboard Active + Expiring)
      lapsed        latest status Expired              (dashboard Expired)
      ended_other   terminated / rejected / renewed / not applicable
      other_status  any other status (applied, registered, draft ...) with an EndDate
      no_end_date   any other status with no EndDate   (unassessable - declared)
  So lic_valid and lic_expired_total equal the dashboard's counts.
  The PERIOD lines stay date-based on purpose (the dashboard has no
  equivalent - this is the extra insight): LapsesRestOfMonth, LapsedThisMonth
  and LapsedLastMonth still read EndDate and lapse-eligibility, whatever the
  stored status says. sql/01 measured ~340 licences sitting on Active or
  Expiring after their EndDate, so "expired last month" can include a licence
  the dashboard still shows as Active - by design, not a defect.

  [REMOVED 2026-09-29] The @AllowedBranches entitlement filter is gone on the
  product owner's instruction.

  -- CONTRACT: THE CALLER CREATES, THIS PROC FILLS, NO RESULT SET ----------

    CREATE TABLE #lic (
        LicenseId          BIGINT        NOT NULL PRIMARY KEY,
        BranchID           INT           NOT NULL,
        LicenseTypeID      BIGINT        NULL,
        LicenseTypeName    NVARCHAR(MAX) NULL,
        EndDate            DATETIME      NULL,
        StatusBucket       NVARCHAR(200)  NULL,     -- NULL = no status row at all
        LicenceState       VARCHAR(12)   NOT NULL, -- valid | lapsed | ended_other | other_status | no_end_date
        RenewalInProgress  BIT           NOT NULL,
        LapsesRestOfMonth  BIT           NOT NULL, -- EndDate in [AsOf, month end), lapse-eligible (date-based)
        LapsedThisMonth    BIT           NOT NULL, -- EndDate in [CurrMonthStart, AsOf), lapse-eligible (date-based)
        LapsedLastMonth    BIT           NOT NULL  -- EndDate in the previous calendar month, lapse-eligible (date-based)
    );

  LicenceState partitions the set exactly (see STATE above).

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

    /*-- 1. SCOPE ------------------------------------------------------------
       Same gate as sql/34, so the Overview never loads licences for a user the
       compliance loader refuses. A user with compliance scope but no licence
       assignment is NOT refused - they simply hold no licences (as on the
       dashboard), and the licence lines read zero.                        */
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

    /*  The dashboard's Active / Expiring / Expired ids (sql/34 seeds them). */
    IF OBJECT_ID('tempdb..#ll_dash') IS NOT NULL DROP TABLE #ll_dash;
    SELECT r.StatusId, r.RuleName
    INTO #ll_dash
    FROM dbo.InsightsFreeDashboardStatusRule r
    WHERE r.RuleName IN ('lic_active', 'lic_expiring', 'lic_expired');

    IF NOT EXISTS (SELECT 1 FROM #ll_dash WHERE RuleName = 'lic_expired')
       OR NOT EXISTS (SELECT 1 FROM #ll_dash WHERE RuleName IN ('lic_active', 'lic_expiring'))
        THROW 51244, N'DICTIONARY GAP - dbo.InsightsFreeDashboardStatusRule has no licence Active / Expiring / Expired rules. They are seeded by sql/34; deploy sql/34 before sql/35. Refusing to compute.', 1;

    /*-- 3. CANDIDATE LICENCES - the licence scope (LIC_EntitiesAssignment) ----
       2-D on (branch, licence type), exactly as the dashboard. The INNER join
       to the type master means an untyped licence is not loaded (parity).  */
    IF OBJECT_ID('tempdb..#ll_cand') IS NOT NULL DROP TABLE #ll_cand;
    SELECT DISTINCT li.ID AS LicenseId,
           li.CustomerBranchID AS BranchID,
           lt.ID AS LicenseTypeID,
           li.EndDate
    INTO #ll_cand
    FROM Lic_tbl_LicenseInstance li
    JOIN CustomerBranch cb              ON cb.ID = li.CustomerBranchID
    JOIN Lic_tbl_LicenseType_Master lt  ON lt.ID = li.LicenseTypeID
    JOIN LIC_EntitiesAssignment lea     ON lea.BranchID      = li.CustomerBranchID
                                       AND lea.LicenseTypeID = li.LicenseTypeID
    WHERE lea.UserID    = @UserID
      AND li.CustomerID = @CustomerID
      AND li.IsDeleted  = 0
      AND lt.IsDeleted  = 0
      AND cb.IsDeleted  = 0 AND cb.Status = 1;
    CREATE CLUSTERED INDEX IX_ll_cand ON #ll_cand (LicenseId);

    /*-- 4. LATEST STATUS PER LICENCE -------------------------------------------
       Latest by CreatedOn - the column RecentLicenseTransactionView ranks on,
       so this picks the row the dashboard reads. ID DESC breaks a tie
       deterministically. [VOLUME] Some licences carry a status row for every
       calendar day (sql/21); Vinay to check row volume on large tenants.    */
    IF OBJECT_ID('tempdb..#ll_latest') IS NOT NULL DROP TABLE #ll_latest;
    ;WITH ranked AS (
        SELECT lst.LicenseID, lst.StatusID, lst.IsActive, lst.ComplianceScheduleOnID,
               ROW_NUMBER() OVER (PARTITION BY lst.LicenseID
                                  ORDER BY lst.CreatedOn DESC, lst.ID DESC) AS rn
        FROM Lic_tbl_LicenseStatusTransaction lst
        JOIN #ll_cand l ON l.LicenseId = lst.LicenseID
    )
    SELECT LicenseID, StatusID, ComplianceScheduleOnID
    INTO #ll_latest
    FROM ranked
    WHERE rn = 1
      AND IsActive = 1;                -- the dashboard requires LST.IsActive = 1 on that row
    CREATE CLUSTERED INDEX IX_ll_latest ON #ll_latest (LicenseID);

    /*-- 4b. PARITY: the licence's compliance side must be live ----------------
       The latest status row's schedule is mapped to this licence, is active,
       has a transaction, and its instance has a performer (RoleID 3 - the
       licence API keeps only those rows). EXISTS, so a licence with several
       mappings or performers still yields one row.                          */
    IF OBJECT_ID('tempdb..#ll_base') IS NOT NULL DROP TABLE #ll_base;
    SELECT c.LicenseId, c.BranchID, c.LicenseTypeID, c.EndDate, ls.StatusID
    INTO #ll_base
    FROM #ll_cand c
    JOIN #ll_latest ls ON ls.LicenseID = c.LicenseId
    WHERE EXISTS (SELECT 1
                  FROM Lic_tbl_LicenseComplianceInstanceScheduleOnMapping m
                  JOIN ComplianceScheduleOn cso ON cso.ID = m.ComplianceScheduleOnID
                                               AND cso.ComplianceInstanceID = m.ComplianceInstanceID
                  WHERE m.LicenseID              = c.LicenseId
                    AND m.ComplianceScheduleOnID = ls.ComplianceScheduleOnID
                    AND cso.IsActive             = 1
                    AND cso.IsUpcomingNotDeleted = 1
                    AND EXISTS (SELECT 1 FROM ComplianceTransaction t
                                WHERE t.ComplianceScheduleOnID = cso.ID)
                    AND EXISTS (SELECT 1 FROM ComplianceAssignment ca
                                WHERE ca.ComplianceInstanceID = m.ComplianceInstanceID
                                  AND ca.RoleID = 3));       -- 3 = performer (a role id)
    CREATE CLUSTERED INDEX IX_ll_base ON #ll_base (LicenseId);

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
        /*  The dashboard's buckets, by status id (STATE in the header).     */
        CASE WHEN dx.RuleName = 'lic_expired'                         THEN 'lapsed'
             WHEN dx.RuleName IN ('lic_active', 'lic_expiring')       THEN 'valid'
             WHEN ISNULL(CAST(s.LapseEligible AS INT), 1) = 0         THEN 'ended_other'
             WHEN l.EndDate IS NULL                                   THEN 'no_end_date'
             ELSE 'other_status' END,
        CAST(CASE WHEN s.Bucket = N'in_progress' THEN 1 ELSE 0 END AS BIT),
        CAST(CASE WHEN l.EndDate >= @AsOf AND l.EndDate < @NextMonthStart
                   AND ISNULL(CAST(s.LapseEligible AS INT), 1) = 1 THEN 1 ELSE 0 END AS BIT),
        CAST(CASE WHEN l.EndDate >= @CurrStart AND l.EndDate < @AsOf
                   AND ISNULL(CAST(s.LapseEligible AS INT), 1) = 1 THEN 1 ELSE 0 END AS BIT),
        CAST(CASE WHEN l.EndDate >= @PrevMonthStart AND l.EndDate < @CurrStart
                   AND ISNULL(CAST(s.LapseEligible AS INT), 1) = 1 THEN 1 ELSE 0 END AS BIT)
    FROM #ll_base l
    LEFT JOIN Lic_tbl_LicenseType_Master lt ON lt.ID = l.LicenseTypeID
    LEFT JOIN #ll_status s                  ON s.StatusId  = l.StatusID
    LEFT JOIN #ll_dash dx                   ON dx.StatusId = l.StatusID;

    /*-- 6. STRUCTURAL: one #lic row per scoped licence (no fan-out) -----------*/
    IF (SELECT COUNT(*) FROM #lic) <> (SELECT COUNT(*) FROM #ll_base)
        THROW 51241, N'FREE MONTHLY LICENCE RECONCILIATION FAILED - loaded licence rows do not tie to the scoped licence base (join fan-out). Refusing to publish.', 1;

    DROP TABLE #ll_latest;
    DROP TABLE #ll_base;
    DROP TABLE #ll_cand;
    DROP TABLE #ll_status;
    DROP TABLE #ll_dash;
END
GO

PRINT 'Free monthly licence loader installed (usp_Insights_FreeMonthly_LoadLicences).';
GO
