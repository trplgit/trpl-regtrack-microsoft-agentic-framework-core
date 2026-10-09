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
