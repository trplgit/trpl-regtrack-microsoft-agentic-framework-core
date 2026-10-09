/*===========================================================================
  RegTrack Insights - v4 free tier (2026-10-06)
  03 - dbo.usp_Insights_FreeMonthly_LoadLicences

  WHY: the free tier now follows the RegTrack Detailed Report / licence report
  export (2026-10-06), not the management dashboard. For licences the
  reference is SP_LicenseMyReport_V2 (statutory branch, MGMT). ADR
  "Free tier monthly digest = RegTrack Detailed Report export",
  Sec. 3 decision 7, Sec. 5.3, Sec. 6, Sec. 7.2.

  WHAT CHANGED vs the prod base (_current_prod/usp_Insights_FreeMonthly_LoadLicences.sql):
   1. @AsOfDate = CAST(@AsOf AS DATE). The licence windows split at the as-at
      DATE, not the @AsOf instant:
        LapsesRestOfMonth = EndDate in [@AsOfDate, @NextMonthStart)
        LapsedThisMonth   = EndDate in [@CurrStart, @AsOfDate)
      LapsedLastMonth is unchanged.
      [2026-10-07] All three windows count by END DATE ONLY (any status), to equal the licence
      report's End Date column; LapseEligible no longer gates them.
   2. #ll_dash (valid / lapsed status ids) now comes from the current
      dictionary version, Semantic 'LicenceReportStatus' (sql/v2/22), by
      Meaning: 'Active' / 'Expiring' -> valid, 'Expired' -> lapsed. It no
      longer reads the InsightsFreeDashboardStatusRule lic_* rows. Labels are
      matched by exact Meaning, never by id; 'Validity Expired' is NOT
      'Expired'. 51244 (reworded) refuses if any of the three is missing.
   3. #ll_cand: EndDate is NULL for a perpetual licence
      (Lic_tbl_LicenseInstance.IsPermanantActive = 1), as LR 582.
   4. #ll_latest: the RecentLicenseTransactionView tied set - every LST row at
      the licence's MAX(CreatedOn) over ALL of its rows (RLTV 14-27), then kept
      only if IsActive = 1 (LR 623) and its StatusID exists in
      Lic_tbl_StatusMaster (RLTV inner join; no IsDeleted filter, deleted ids
      13/14 are still live latest statuses). Prod took rn = 1 by
      CreatedOn DESC, ID DESC and then required IsActive = 1 on that one row,
      so a licence whose tied set mixes IsActive 0 and 1 rows is now KEPT.
   5. #ll_base: a licence qualifies only if it has at least one licence-report
      master row (LR 309-381: mapping row x active RoleID-3 user of THIS
      tenant, Compliance_Master + Act + ComplianceCategory present) whose
      mapped schedule is live and belongs to the mapped instance (LR 524-530,
      628-630), has transactions at its MAX(Dated) (LR 631-632), whose
      compliance has a Compliance_Description row (LR 635-636), and at which
      a kept tied LST row points (LR 624-627). Prod accepted any performer
      (active or not) and checked no master rows / description.
      Representative status = the highest-ID qualifying kept tied LST row.
   6. #lic gets ExportRows (the caller's new LAST column) = licence-report
      export rows for the licence (ADR Sec. 5.3):
        SUM over master rows r of TL x R x Dn x TBn x CSDn
      U2 SWITCH: that is ONE expression in section 5 below; set it to 1 for
      distinct-licence grain. Inclusion (ReportRows >= 1) does not change.

  UNCHANGED: input contract (51245-51247), scope gate (51240), LicenceStatus
  classification (51248, 51249), licence scope (LIC_EntitiesAssignment,
  2-D branch x licence type), type master, branch filters, #lic column
  contract (plus ExportRows), the row-level fan-out check 51241, and no
  result set is emitted. No new error codes (block 51240-51249; 51242/51243
  are retired and not used).

  DEPENDENCY: install AFTER 01_slot_procs.sql. The INSERT names
  #lic.ExportRows, which the callers' #lic has only after 01; a reversed
  install compiles (deferred name resolution) but fails at RUN time.

  ASCII only (CLAUDE.md Sec. 5a). No join hints. No subquery inside an
  aggregate (Msg 130): every factor is materialised first, then summed.
===========================================================================*/
SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Insights_FreeMonthly_LoadLicences
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

    /*  [v4] Windows split at the as-at DATE (ADR Sec. 3 decision 5/7).       */
    DECLARE @AsOfDate DATE = CAST(@AsOf AS DATE);

    /*-- 1. SCOPE ------------------------------------------------------------
       Same gate as sql/34, so the Overview never loads licences for a user the
       compliance loader refuses. A user with compliance scope but no licence
       assignment is NOT refused - they simply hold no licences, and the
       licence lines read zero.                                              */
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

    /*  [v4] Valid / lapsed ids = the licence report's own Status labels
        (Semantic 'LicenceReportStatus', sql/v2/22), by exact Meaning:
        Active / Expiring -> valid, Expired -> lapsed. 'Validity Expired'
        is a different label and is deliberately NOT matched.            */
    IF OBJECT_ID('tempdb..#ll_dash') IS NOT NULL DROP TABLE #ll_dash;
    SELECT TRY_CAST(p.RawValue AS INT) AS StatusId,
           p.Meaning                   AS Label,
           CAST(CASE WHEN p.Meaning = N'Expired' THEN 'lapsed' ELSE 'valid' END AS VARCHAR(12)) AS DashState
    INTO #ll_dash
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'LicenceReportStatus'
      AND p.Meaning IN (N'Active', N'Expiring', N'Expired')
      AND TRY_CAST(p.RawValue AS INT) IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM #ll_dash WHERE Label = N'Active')
       OR NOT EXISTS (SELECT 1 FROM #ll_dash WHERE Label = N'Expiring')
       OR NOT EXISTS (SELECT 1 FROM #ll_dash WHERE Label = N'Expired')
        THROW 51244, N'DICTIONARY GAP - the current dictionary has no Semantic=LicenceReportStatus row for one of the labels Active / Expiring / Expired. Deploy sql/v2/22 (precheck gate G1) and re-run. Refusing to compute.', 1;

    /*-- 3. CANDIDATE LICENCES - the licence scope (LIC_EntitiesAssignment) ----
       2-D on (branch, licence type), as the licence report's MGMT path
       (LR 82-88, 365-369). The INNER join to the type master means an
       untyped licence is not loaded (parity, LR 340-341).
       [v4] Perpetual licence -> EndDate NULL (LR 582).                      */
    IF OBJECT_ID('tempdb..#ll_cand') IS NOT NULL DROP TABLE #ll_cand;
    SELECT DISTINCT li.ID AS LicenseId,
           li.CustomerBranchID AS BranchID,
           lt.ID AS LicenseTypeID,
           CASE WHEN li.IsPermanantActive = 1 THEN NULL ELSE li.EndDate END AS EndDate
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

    /*-- 4. LATEST STATUS PER LICENCE = RLTV's TIED SET ------------------------
       [v4] Every LST row at the licence's MAX(CreatedOn), the max taken over
       ALL of its rows (RLTV 14-27; ties are not broken). Of those, keep the
       rows with IsActive = 1 (LR 623) whose StatusID exists in
       Lic_tbl_StatusMaster (RLTV inner join - no IsDeleted filter there).
       RLTV itself is NOT joined (CLAUDE.md Sec. 5); its logic is copied.
       [VOLUME] Some licences carry a status row for every calendar day
       (sql/21); Vinay to check row volume on large tenants.              */
    IF OBJECT_ID('tempdb..#ll_latest') IS NOT NULL DROP TABLE #ll_latest;
    ;WITH mx AS (
        SELECT lst.LicenseID, MAX(lst.CreatedOn) AS MaxCreatedOn
        FROM Lic_tbl_LicenseStatusTransaction lst
        JOIN #ll_cand c ON c.LicenseId = lst.LicenseID
        GROUP BY lst.LicenseID
    )
    SELECT lst.ID AS LstId, lst.LicenseID, lst.StatusID, lst.ComplianceScheduleOnID
    INTO #ll_latest
    FROM mx
    JOIN Lic_tbl_LicenseStatusTransaction lst ON lst.LicenseID = mx.LicenseID
                                             AND lst.CreatedOn = mx.MaxCreatedOn
    JOIN Lic_tbl_StatusMaster sm              ON sm.ID = lst.StatusID
    WHERE lst.IsActive = 1;
    CREATE CLUSTERED INDEX IX_ll_latest ON #ll_latest (LicenseID, ComplianceScheduleOnID);

    /*  TL per (licence, schedule): kept tied rows pointing at that schedule
        (LR 627), and the highest kept LST id there (the label candidate).  */
    IF OBJECT_ID('tempdb..#ll_tl') IS NOT NULL DROP TABLE #ll_tl;
    SELECT lt.LicenseID, lt.ComplianceScheduleOnID,
           COUNT(*)      AS TL,
           MAX(lt.LstId) AS MaxLstId
    INTO #ll_tl
    FROM #ll_latest lt
    WHERE lt.ComplianceScheduleOnID IS NOT NULL
    GROUP BY lt.LicenseID, lt.ComplianceScheduleOnID;
    CREATE CLUSTERED INDEX IX_ll_tl ON #ll_tl (LicenseID, ComplianceScheduleOnID);

    /*-- 4b. LICENCE-REPORT MASTER ROWS (LR 309-381, DISTINCT) ----------------
       One row per (licence, mapping instance, mapping schedule, active
       RoleID-3 user of this tenant). Compliance_Master, Act and
       ComplianceCategory must exist (LR 352-357, no IsDeleted filter there).
       The mapped schedule must be live and belong to the mapped instance
       (LR 524-530, 628-630), and a kept tied LST row must point at it
       (inner join #ll_tl). Every other master column depends only on the
       licence or the mapping, so it does not split rows.                   */
    IF OBJECT_ID('tempdb..#ll_master') IS NOT NULL DROP TABLE #ll_master;
    SELECT DISTINCT
           c.LicenseId,
           c.BranchID,
           m.ComplianceInstanceID,
           m.ComplianceScheduleOnID,
           ci.ComplianceId,
           ca.UserID
    INTO #ll_master
    FROM #ll_cand c
    JOIN Lic_tbl_LicenseComplianceInstanceScheduleOnMapping m ON m.LicenseID = c.LicenseId
    JOIN #ll_tl tl                ON tl.LicenseID              = c.LicenseId
                                 AND tl.ComplianceScheduleOnID = m.ComplianceScheduleOnID
    JOIN ComplianceScheduleOn cso ON cso.ID                    = m.ComplianceScheduleOnID
                                 AND cso.ComplianceInstanceID  = m.ComplianceInstanceID
    JOIN ComplianceInstance ci    ON ci.ID                     = m.ComplianceInstanceID
    JOIN ComplianceAssignment ca  ON ca.ComplianceInstanceID   = ci.ID
    JOIN [User] u                 ON u.ID                      = ca.UserID
    JOIN dbo.Compliance_Master cm ON cm.ID                     = ci.ComplianceId
    JOIN dbo.Act a                ON a.ID                      = cm.ActID
    JOIN dbo.ComplianceCategory cc ON cc.ID                    = a.ComplianceCategoryId
    WHERE ca.RoleID                = 3            -- 3 = performer (a role id), LR 364
      AND u.CustomerID             = @CustomerID  -- active user of this tenant, LR 243-248
      AND u.IsDeleted              = 0
      AND u.IsActive               = 1
      AND cso.IsActive             = 1
      AND cso.IsUpcomingNotDeleted = 1;
    CREATE CLUSTERED INDEX IX_ll_master ON #ll_master (LicenseId, ComplianceScheduleOnID);

    /*-- 4c. EXPORT-ROW FACTORS (ADR Sec. 5.3), one grouped read per key -------*/

    /*  R per schedule: ComplianceTransaction rows at the schedule's
        MAX(Dated), all statuses (RCTV LEFT-joins ComplianceStatus, so no
        status filter; LR 631-632, RCTV 10-15). Must be >= 1 (inner).      */
    IF OBJECT_ID('tempdb..#ll_r') IS NOT NULL DROP TABLE #ll_r;
    ;WITH s AS (
        SELECT DISTINCT ms.ComplianceScheduleOnID FROM #ll_master ms
    ), mx AS (
        SELECT t.ComplianceScheduleOnID, MAX(t.Dated) AS MaxDated
        FROM ComplianceTransaction t
        JOIN s ON s.ComplianceScheduleOnID = t.ComplianceScheduleOnID
        GROUP BY t.ComplianceScheduleOnID
    )
    SELECT mx.ComplianceScheduleOnID, COUNT(*) AS R
    INTO #ll_r
    FROM mx
    JOIN ComplianceTransaction t ON t.ComplianceScheduleOnID = mx.ComplianceScheduleOnID
                                AND t.Dated                  = mx.MaxDated
    GROUP BY mx.ComplianceScheduleOnID;
    CREATE CLUSTERED INDEX IX_ll_r ON #ll_r (ComplianceScheduleOnID);

    /*  Dn per compliance: Compliance_Description rows (LR 635-636).
        Must be >= 1 (inner).                                              */
    IF OBJECT_ID('tempdb..#ll_dn') IS NOT NULL DROP TABLE #ll_dn;
    ;WITH k AS (
        SELECT DISTINCT ms.ComplianceId FROM #ll_master ms
    )
    SELECT k.ComplianceId, COUNT(*) AS Dn
    INTO #ll_dn
    FROM k
    JOIN Compliance_Description cd ON cd.ComplianceID = k.ComplianceId
    GROUP BY k.ComplianceId;
    CREATE CLUSTERED INDEX IX_ll_dn ON #ll_dn (ComplianceId);

    /*  TBn per branch: MAX(1, Temp_CustomerBranch rows) (LR 669-670, LEFT).
        The floor is applied here, never inside the final SUM.             */
    IF OBJECT_ID('tempdb..#ll_tb') IS NOT NULL DROP TABLE #ll_tb;
    ;WITH b AS (
        SELECT DISTINCT ms.BranchID FROM #ll_master ms
    )
    SELECT b.BranchID,
           CASE WHEN COUNT(tb.BranchId) < 1 THEN 1 ELSE COUNT(tb.BranchId) END AS TBn
    INTO #ll_tb
    FROM b
    LEFT JOIN Temp_CustomerBranch tb ON tb.BranchId = b.BranchID
    GROUP BY b.BranchID;
    CREATE CLUSTERED INDEX IX_ll_tb ON #ll_tb (BranchID);

    /*  CSDn per compliance: MAX(1, Compliance_ShortDescr_ByClient rows)
        (LR 673-674, LEFT). RegTrack joins
        CSD.ComplianceInstanceID = <the COMPLIANCE id>. That compares an
        instance-id column to a compliance id; it is replicated EXACTLY as
        written, because the export rows it produces are what testers count. */
    IF OBJECT_ID('tempdb..#ll_csd') IS NOT NULL DROP TABLE #ll_csd;
    ;WITH k AS (
        SELECT DISTINCT ms.ComplianceId FROM #ll_master ms
    )
    SELECT k.ComplianceId,
           CASE WHEN COUNT(csd.ComplianceInstanceID) < 1 THEN 1 ELSE COUNT(csd.ComplianceInstanceID) END AS CSDn
    INTO #ll_csd
    FROM k
    LEFT JOIN Compliance_ShortDescr_ByClient csd ON csd.ComplianceInstanceID = k.ComplianceId
    GROUP BY k.ComplianceId;
    CREATE CLUSTERED INDEX IX_ll_csd ON #ll_csd (ComplianceId);

    /*  One row per qualifying master row with its factors. INNER joins to
        #ll_r and #ll_dn drop master rows the report drops (R or Dn = 0);
        TBn and CSDn are already floored at 1 and always present.          */
    IF OBJECT_ID('tempdb..#ll_rows') IS NOT NULL DROP TABLE #ll_rows;
    SELECT ms.LicenseId,
           ms.ComplianceInstanceID,
           ms.ComplianceScheduleOnID,
           ms.ComplianceId,
           ms.UserID,
           tl.TL, r.R, dn.Dn, tb.TBn, csd.CSDn,
           tl.MaxLstId
    INTO #ll_rows
    FROM #ll_master ms
    JOIN #ll_tl  tl  ON tl.LicenseID              = ms.LicenseId
                    AND tl.ComplianceScheduleOnID = ms.ComplianceScheduleOnID
    JOIN #ll_r   r   ON r.ComplianceScheduleOnID  = ms.ComplianceScheduleOnID
    JOIN #ll_dn  dn  ON dn.ComplianceId           = ms.ComplianceId
    JOIN #ll_tb  tb  ON tb.BranchID               = ms.BranchID
    JOIN #ll_csd csd ON csd.ComplianceId          = ms.ComplianceId;
    CREATE CLUSTERED INDEX IX_ll_rows ON #ll_rows (LicenseId);

    /*  Per licence: report rows = SUM over master rows of
        TL x R x Dn x TBn x CSDn (ADR Sec. 5.3), and the representative =
        the highest-ID qualifying kept tied LST row (ADR Sec. 7.2).        */
    IF OBJECT_ID('tempdb..#ll_weight') IS NOT NULL DROP TABLE #ll_weight;
    SELECT w.LicenseId,
           SUM(CAST(w.TL AS BIGINT) * w.R * w.Dn * w.TBn * w.CSDn) AS ReportRows,
           MAX(w.MaxLstId) AS RepLstId
    INTO #ll_weight
    FROM #ll_rows w
    GROUP BY w.LicenseId;
    CREATE CLUSTERED INDEX IX_ll_weight ON #ll_weight (LicenseId);

    /*-- 4d. THE SCOPED LICENCE BASE - one row per licence ---------------------
       A licence is counted only if ReportRows >= 1 (ADR Sec. 5.3). The
       label's status comes from the representative LST row, joined back on
       LST.ID (unique), so this stays one row per licence.                  */
    IF OBJECT_ID('tempdb..#ll_base') IS NOT NULL DROP TABLE #ll_base;
    SELECT c.LicenseId, c.BranchID, c.LicenseTypeID, c.EndDate, rep.StatusID, w.ReportRows
    INTO #ll_base
    FROM #ll_cand c
    JOIN #ll_weight w   ON w.LicenseId = c.LicenseId
    JOIN #ll_latest rep ON rep.LstId   = w.RepLstId
    WHERE w.ReportRows >= 1;
    CREATE CLUSTERED INDEX IX_ll_base ON #ll_base (LicenseId);

    /*-- 5. FILL THE CALLER'S #lic -----------------------------------------------*/
    INSERT #lic (LicenseId, BranchID, LicenseTypeID, LicenseTypeName, EndDate, StatusBucket,
                 LicenceState, RenewalInProgress, LapsesRestOfMonth, LapsedThisMonth, LapsedLastMonth,
                 ExportRows)
    SELECT
        l.LicenseId,
        l.BranchID,
        l.LicenseTypeID,
        lt.Name,
        l.EndDate,
        s.Bucket,
        /*  [v4] Valid / lapsed by the licence report's own label (#ll_dash).
            CASE order unchanged from prod.                                 */
        CASE WHEN dx.DashState = 'lapsed'                             THEN 'lapsed'
             WHEN dx.DashState = 'valid'                              THEN 'valid'
             WHEN ISNULL(CAST(s.LapseEligible AS INT), 1) = 0         THEN 'ended_other'
             WHEN l.EndDate IS NULL                                   THEN 'no_end_date'
             ELSE 'other_status' END,
        CAST(CASE WHEN s.Bucket = N'in_progress' THEN 1 ELSE 0 END AS BIT),
        /*  [v4] Windows split at the as-at DATE.
            [v4.1 2026-10-07] By END DATE ONLY, whatever the status - exactly what the
            licence report's End Date column shows. LapseEligible used to be required here,
            so a licence already Renewed / Terminated / Not Applicable was left out (live:
            Minda had 2 end dates in September, the email said 1). The "no renewal filed"
            figures and the named licences still exclude those licences: the slot procs add
            LicenceState <> 'ended_other' wherever they read RenewalInProgress = 0 with
            one of these windows (01_slot_procs.sql, v4.1).                            */
        CAST(CASE WHEN l.EndDate >= @AsOfDate AND l.EndDate < @NextMonthStart THEN 1 ELSE 0 END AS BIT),
        CAST(CASE WHEN l.EndDate >= @CurrStart AND l.EndDate < @AsOfDate       THEN 1 ELSE 0 END AS BIT),
        CAST(CASE WHEN l.EndDate >= @PrevMonthStart AND l.EndDate < @CurrStart THEN 1 ELSE 0 END AS BIT),
        /*  ===== U2 SWITCH - ExportRows =====================================
            Default: licence-report ROW grain (ADR Sec. 5.3).
            For distinct-licence grain replace this ONE expression with 1.
            Nothing else changes: inclusion (ReportRows >= 1) stays.
            ================================================================ */
        CAST(l.ReportRows AS INT)
    FROM #ll_base l
    LEFT JOIN Lic_tbl_LicenseType_Master lt ON lt.ID = l.LicenseTypeID
    LEFT JOIN #ll_status s                  ON s.StatusId  = l.StatusID
    LEFT JOIN #ll_dash dx                   ON dx.StatusId = l.StatusID;

    /*-- 6. STRUCTURAL: one #lic row per scoped licence (no fan-out) -----------*/
    IF (SELECT COUNT(*) FROM #lic) <> (SELECT COUNT(*) FROM #ll_base)
        THROW 51241, N'FREE MONTHLY LICENCE RECONCILIATION FAILED - loaded licence rows do not tie to the scoped licence base (join fan-out). Refusing to publish.', 1;

    DROP TABLE #ll_base;
    DROP TABLE #ll_weight;
    DROP TABLE #ll_rows;
    DROP TABLE #ll_csd;
    DROP TABLE #ll_tb;
    DROP TABLE #ll_dn;
    DROP TABLE #ll_r;
    DROP TABLE #ll_master;
    DROP TABLE #ll_tl;
    DROP TABLE #ll_latest;
    DROP TABLE #ll_cand;
    DROP TABLE #ll_status;
    DROP TABLE #ll_dash;
END
GO

PRINT 'dbo.usp_Insights_FreeMonthly_LoadLicences (v4, licence report export parity) installed';
GO
