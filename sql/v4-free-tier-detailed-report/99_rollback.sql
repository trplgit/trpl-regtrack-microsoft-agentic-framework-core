/*===========================================================================
  RegTrack Insights - v4 free tier: ROLLBACK
  99_rollback.sql - restores the 8 free-tier procedures EXACTLY as they ran on
  prod and UAT on 2026-10-06 (the dashboard-parity version), from _current_prod/.

  Use only if the v4 install has to be undone. Runs in reverse install order.
  Each procedure is DROPPED and re-CREATED from the byte-identical snapshot
  (the same DROP/CREATE pattern the original deploy used), then the script
  verifies every definition hash and THROWs 51186 if any does not match.

  Do NOT use sql/v2/99_rollback_v2.sql for this - it restores an older LoadFacts.
  Explicit grants (prod: EXECUTE + VIEW DEFINITION to reginsights_sql_readonly_user)
  are saved before the drops and re-applied after.
  Pure ASCII. Error codes 51186 (hash verify), 51187 (grants) - free range, CLAUDE.md Sec.5b.
===========================================================================*/
SET NOCOUNT ON;
GO

/* Save the explicit grants on these procedures BEFORE dropping them (prod has
   EXECUTE + VIEW DEFINITION to reginsights_sql_readonly_user on 7 of them; a
   DROP removes grants). They are re-applied after the re-create, below.
   #rb_perms survives the GO separators because it lives for the session. */
IF OBJECT_ID('tempdb..#rb_perms') IS NOT NULL DROP TABLE #rb_perms;
SELECT o.name AS ProcName, p.permission_name AS PermissionName, p.state_desc AS StateDesc,
       USER_NAME(p.grantee_principal_id) AS Grantee
INTO #rb_perms
FROM sys.database_permissions p
JOIN sys.objects o ON o.object_id = p.major_id
WHERE p.class = 1 AND p.minor_id = 0
  AND o.name IN ('usp_Insights_FreeMonthly_LoadFacts','usp_Insights_FreeMonthly_LoadLicences',
                 'usp_Insights_FreeMonthly_MemberDetectors','usp_Insights_FreeMonthly_Licence',
                 'usp_Insights_FreeMonthly_Act','usp_Insights_FreeMonthly_Location',
                 'usp_Insights_FreeMonthly_Users','usp_Insights_FreeMonthly_Overview');
PRINT 'Saved ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' explicit permission(s).';
GO
IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_LoadFacts', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_LoadFacts;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_LoadFacts
    @UserID          INT,
    @CustomerID      INT,
    @CurrMonthStart  DATE,
    @AsOf            DATETIME
AS
BEGIN
    SET NOCOUNT ON;

    /*===================================================================
      0. INPUT CONTRACT - fail closed before reading anything
    ===================================================================*/
    IF @UserID IS NULL OR @CustomerID IS NULL OR @CurrMonthStart IS NULL OR @AsOf IS NULL
        THROW 51235, N'FREE MONTHLY - WINDOW INPUT MISSING: @UserID, @CustomerID, @CurrMonthStart and @AsOf are all required. The caller resolves the edition and passes concrete values. Refusing to compute.', 1;

    IF DATEPART(DAY, @CurrMonthStart) <> 1
        THROW 51236, N'FREE MONTHLY - @CurrMonthStart IS NOT THE FIRST OF A MONTH. It must be the 1st of the edition month, computed from the edition in C#. Refusing to compute.', 1;

    DECLARE @CurrStart      DATETIME = CAST(@CurrMonthStart AS DATETIME);
    DECLARE @PrevMonthStart DATETIME = DATEADD(MONTH, -1, @CurrStart);
    DECLARE @NextMonthStart DATETIME = DATEADD(MONTH,  1, @CurrStart);
    DECLARE @AsOfDate       DATETIME = CAST(CAST(@AsOf AS DATE) AS DATETIME);   -- the dashboard compares whole dates

    IF @AsOf < @CurrStart OR @AsOf >= @NextMonthStart
        THROW 51237, N'FREE MONTHLY - @AsOf FALLS OUTSIDE THE EDITION MONTH. Most likely a clock mismatch (a UTC @AsOf against a local-time month). Pass @AsOf in the same clock as ComplianceScheduleOn.ScheduleOn. Refusing to compute.', 1;

    /*===================================================================
      1. SCOPE + DICTIONARY PRE-FLIGHT
    ===================================================================*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51230, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- THROWs 51001 on a dictionary gap; returns no grid

    IF NOT EXISTS (SELECT 1 FROM dbo.InsightsFreeDashboardStatusRule WHERE RuleName = 'overdue')
       OR NOT EXISTS (SELECT 1 FROM dbo.InsightsFreeDashboardStatusRule WHERE RuleName = 'timed_by_close_date')
        THROW 51234, N'DICTIONARY GAP - dbo.InsightsFreeDashboardStatusRule is missing a rule set. It holds the RegTrack dashboard status rules and is seeded by sql/34; redeploy sql/34. Refusing to compute.', 1;

    /*  Active, undeleted users of THIS tenant - the dashboard's #tempUser_M.
        An obligation counts only when one of them holds its performer role.  */
    IF OBJECT_ID('tempdb..#lf_user') IS NOT NULL DROP TABLE #lf_user;
    SELECT u.ID
    INTO #lf_user
    FROM [User] u
    WHERE u.CustomerID = @CustomerID
      AND u.IsDeleted  = 0
      AND u.IsActive   = 1;
    CREATE CLUSTERED INDEX IX_lf_user ON #lf_user (ID);

    IF OBJECT_ID('tempdb..#lf_risk') IS NOT NULL DROP TABLE #lf_risk;
    SELECT TRY_CAST(p.RawValue AS INT) AS RawValue, p.Meaning
    INTO #lf_risk
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'RiskType'
      AND TRY_CAST(p.RawValue AS INT) IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM #lf_risk WHERE Meaning = N'Critical')
        THROW 51239, N'DICTIONARY GAP - InsightsEnumPolarity has no current RiskType row meaning Critical. The monthly tier never compares RiskType to a literal; seed the BA-verified mapping and re-run. Refusing to compute.', 1;

    /*===================================================================
      2. SCOPED INSTANCES - materialised FIRST (scope-first, then reach)
         Parity filters (header): act not deleted, compliance visible, not
         ComplianceType 1, and a performer (RoleID 3) held by an active user
         of this tenant. Risk = instance override first, as the dashboard
         reads it (COALESCE(CI.Risk, C.RiskType)).
         Selected here, not via tvfInsightsScopedInstances, because that
         shared function drops soft-deleted instances and the dashboard does
         not (header). Same 2-D scope pairs, same branch/compliance filters.
    ===================================================================*/
    INSERT #inst (ComplianceInstanceID, BranchID, CategoryId, ActID, ComplianceID,
                  DepartmentID, Imprisonment, RiskType, RiskClass)
    SELECT ci.ID,
           ci.CustomerBranchID,
           a.ComplianceCategoryId,
           a.ID,
           ci.ComplianceID,
           ci.DepartmentID,
           CAST(ISNULL(c.Imprisonment, 0) AS BIT),
           COALESCE(ci.Risk, c.RiskType),
           r.Meaning
    FROM ComplianceInstance ci
    JOIN CustomerBranch cb     ON cb.ID = ci.CustomerBranchID
    JOIN Compliance c          ON c.ID  = ci.ComplianceID
    JOIN Act a                 ON a.ID  = c.ActID
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp      -- 2-D: BOTH branch AND category
         ON sp.BranchID   = ci.CustomerBranchID
        AND sp.CategoryId = a.ComplianceCategoryId
    LEFT JOIN #lf_risk r       ON r.RawValue = COALESCE(ci.Risk, c.RiskType)
    WHERE cb.CustomerID = @CustomerID
      AND cb.IsDeleted  = 0 AND cb.Status = 1
      AND c.IsDeleted   = 0
      AND a.IsDeleted   = 0
      AND (c.ComplinceVisible = 1 OR c.ComplinceVisible IS NULL)   -- schema spelling
      AND c.ComplianceType <> 1                                    -- as the dashboard: NULL is excluded too
      AND EXISTS (SELECT 1
                  FROM ComplianceAssignment ca
                  JOIN #lf_user u ON u.ID = ca.UserID
                  WHERE ca.ComplianceInstanceID = ci.ID
                    AND ca.RoleID = 3);                            -- 3 = performer (a role id, not a status)

    CREATE NONCLUSTERED INDEX IX_inst_branch ON #inst (BranchID) INCLUDE (ActID, Imprisonment, RiskClass);

    /*  Instance-level owner fallback. MIN(UserID) = deterministic pick when an
        instance has several assignees. UserID > 0 excludes placeholder rows. */
    IF OBJECT_ID('tempdb..#lf_owner') IS NOT NULL DROP TABLE #lf_owner;
    SELECT ca.ComplianceInstanceID,
           MIN(CASE WHEN ca.RoleID = 3 THEN ca.UserID END) AS PerformerID,
           MIN(CASE WHEN ca.RoleID = 4 THEN ca.UserID END) AS ReviewerID
    INTO #lf_owner
    FROM #inst i
    JOIN ComplianceAssignment ca ON ca.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE ca.UserID > 0
      AND ca.RoleID IN (3, 4)      -- 3 = performer, 4 = reviewer (role ids, not statuses)
    GROUP BY ca.ComplianceInstanceID;

    CREATE CLUSTERED INDEX IX_lf_owner ON #lf_owner (ComplianceInstanceID);

    /*===================================================================
      3. ONE READ: window schedules + overdue stock, latest status resolved once
         [PERF] explicit TOP 1 seek on IX_CT_CSO_Dated_ID - never the view.
         [PERF] no join hints anywhere (CLAUDE.md Sec.5, 877 ms -> 26,492 ms).
    ===================================================================*/
    INSERT #sched (ComplianceScheduleOnID, ComplianceInstanceID, BranchID, ScheduleOn,
                   WindowPart, PerformerID, PerformerSource, ReviewerID, ReviewerSource,
                   NeverTouched, Outcome, IsOverdue, DaysPastDue, AgeBand)
    SELECT
        cso.ID,
        i.ComplianceInstanceID,
        i.BranchID,
        cso.ScheduleOn,
        CASE WHEN cso.ScheduleOn >= @PrevMonthStart AND cso.ScheduleOn < @CurrStart THEN 'prev_month'
             WHEN cso.ScheduleOn >= @CurrStart      AND cso.ScheduleOn <= @AsOf    THEN 'curr_elapsed'
             WHEN cso.ScheduleOn >  @AsOf           AND cso.ScheduleOn < @NextMonthStart THEN 'curr_remaining'
             ELSE NULL END,
        CASE WHEN cso.Performerid IS NOT NULL AND cso.Performerid <> 0 THEN cso.Performerid
             ELSE o.PerformerID END,
        CASE WHEN cso.Performerid IS NOT NULL AND cso.Performerid <> 0 THEN 'schedule'
             WHEN o.PerformerID IS NOT NULL                          THEN 'instance'
             ELSE 'none' END,
        CASE WHEN cso.Reviewerid IS NOT NULL AND cso.Reviewerid <> 0 THEN cso.Reviewerid
             ELSE o.ReviewerID END,
        CASE WHEN cso.Reviewerid IS NOT NULL AND cso.Reviewerid <> 0 THEN 'schedule'
             WHEN o.ReviewerID IS NOT NULL                           THEN 'instance'
             ELSE 'none' END,
        CAST(0 AS BIT),              -- NeverTouched: a schedule with no transaction is not loaded (parity)
        CASE WHEN d.StatusId IS NULL                                          THEN 'unclassified'
             /*  Parity: the "Approved" ids are timed by close date, not by id. */
             WHEN d.ClosureClass = 'completed' AND tc.StatusId IS NOT NULL
                  THEN CASE WHEN lt.StatusChangedOn IS NULL               THEN 'completed_untimed'
                            WHEN lt.StatusChangedOn <= cso.ScheduleOn      THEN 'completed_on_time'
                            ELSE 'completed_late' END
             WHEN d.ClosureClass = 'completed' AND d.Timeliness = 'on_time'   THEN 'completed_on_time'
             WHEN d.ClosureClass = 'completed' AND d.Timeliness = 'delayed'   THEN 'completed_late'
             WHEN d.ClosureClass = 'completed'                                THEN 'completed_untimed'
             WHEN d.ClosureClass = 'resolved_terminal'                        THEN 'resolved_terminal'
             WHEN d.ClosureClass = 'open'                                     THEN 'open'
             ELSE 'unclassified' END,
        CAST(CASE WHEN cso.ScheduleOn < @AsOfDate AND od.StatusId IS NOT NULL THEN 1 ELSE 0 END AS BIT),
        CASE WHEN cso.ScheduleOn < @AsOfDate AND od.StatusId IS NOT NULL
             THEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) END,
        CASE WHEN cso.ScheduleOn < @AsOfDate AND od.StatusId IS NOT NULL
             THEN CASE WHEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) <= 30 THEN 'd000_030'
                       WHEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) <= 60 THEN 'd031_060'
                       WHEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) <= 90 THEN 'd061_090'
                       ELSE 'd091_plus' END END
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    /*  CROSS, not OUTER: the dashboard inner-joins the latest transaction, so a
        schedule with none is not counted anywhere (header, parity).         */
    CROSS APPLY (SELECT TOP 1 t.StatusId, t.ID, t.StatusChangedOn
                 FROM ComplianceTransaction t
                 WHERE t.ComplianceScheduleOnID = cso.ID
                 ORDER BY t.Dated DESC, t.ID DESC) lt
    LEFT JOIN dbo.vInsightsStatusCurrent d            ON d.StatusId  = lt.StatusId
    LEFT JOIN dbo.InsightsFreeDashboardStatusRule od  ON od.StatusId = lt.StatusId AND od.RuleName = 'overdue'
    LEFT JOIN dbo.InsightsFreeDashboardStatusRule tc  ON tc.StatusId = lt.StatusId AND tc.RuleName = 'timed_by_close_date'
    LEFT JOIN #lf_owner o                             ON o.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.IsActive = 1
      AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn < @NextMonthStart
      AND (    cso.ScheduleOn >= @PrevMonthStart
           OR (cso.ScheduleOn < @AsOfDate AND od.StatusId IS NOT NULL) );

    CREATE NONCLUSTERED INDEX IX_sched_window ON #sched (WindowPart, Outcome) INCLUDE (ComplianceInstanceID, BranchID);
    CREATE NONCLUSTERED INDEX IX_sched_overdue ON #sched (IsOverdue, AgeBand) INCLUDE (ComplianceInstanceID, BranchID, PerformerID);
    CREATE NONCLUSTERED INDEX IX_sched_perf ON #sched (PerformerID) INCLUDE (WindowPart, Outcome, IsOverdue);

    /*===================================================================
      4. STRUCTURAL INVARIANTS - these hold REGARDLESS of data, so they
         THROW (CLAUDE.md Sec.11). A failure means this CODE is wrong.
    ===================================================================*/
    IF EXISTS (SELECT 1 FROM #sched WHERE Outcome = 'unclassified')
        THROW 51238, N'DICTIONARY GAP - a scoped schedule has a latest status the dictionary cannot place in a closure class (status id absent from vInsightsStatusCurrent, or an unknown ClosureClass). Refusing to guess.', 1;

    IF EXISTS (SELECT 1 FROM #sched WHERE WindowPart IS NULL AND IsOverdue = 0)
        THROW 51231, N'FREE MONTHLY RECONCILIATION FAILED - a row outside the window is not overdue. Rows outside the window may exist only as overdue stock; the load predicate has drifted. Refusing to publish.', 1;

    IF EXISTS (SELECT 1 FROM #sched WHERE WindowPart = 'curr_remaining' AND IsOverdue = 1)
        THROW 51232, N'FREE MONTHLY RECONCILIATION FAILED - a schedule due AFTER @AsOf is marked overdue. Overdue requires a due date before the as-at date. Refusing to publish.', 1;

    IF EXISTS (SELECT 1 FROM #sched
               WHERE (IsOverdue = 1 AND (DaysPastDue IS NULL OR AgeBand IS NULL))
                  OR (IsOverdue = 0 AND (DaysPastDue IS NOT NULL OR AgeBand IS NOT NULL)))
        THROW 51233, N'FREE MONTHLY RECONCILIATION FAILED - overdue age fields are inconsistent with IsOverdue. Refusing to publish.', 1;

    DROP TABLE #lf_owner;
    DROP TABLE #lf_risk;
    DROP TABLE #lf_user;
END
GO
PRINT 'usp_Insights_FreeMonthly_LoadFacts restored.';
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_LoadLicences', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_LoadLicences;
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
PRINT 'usp_Insights_FreeMonthly_LoadLicences restored.';
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_MemberDetectors', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_MemberDetectors;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_MemberDetectors
    @EntityKind           VARCHAR(20),          -- location | act | person
    @EntityPlural         NVARCHAR(40),         -- "locations" | "laws" | "people" - fact labels only
    @RelativeRiskFactor   DECIMAL(4,2) = 1.50,
    @ConcentrationFactor  DECIMAL(4,2) = 2.00,
    @MemberFloor          INT          = 5,
    @MaxPerDetector       INT          = 5,
    @MaxExamples          INT          = 3
AS
BEGIN
    SET NOCOUNT ON;

    IF @EntityKind IS NULL OR @EntityPlural IS NULL OR @RelativeRiskFactor IS NULL
       OR @ConcentrationFactor IS NULL OR @MemberFloor IS NULL OR @MaxPerDetector IS NULL
       OR @MaxExamples IS NULL
        THROW 51265, N'FREE MONTHLY MEMBER DETECTORS - an input parameter is NULL. Refusing to compute.', 1;

    /*===================================================================
      0. STRUCTURAL - the caller's mapping must describe loaded data
    ===================================================================*/
    IF EXISTS (SELECT 1 FROM #smap sm
               LEFT JOIN #sched s ON s.ComplianceScheduleOnID = sm.ComplianceScheduleOnID
               WHERE s.ComplianceScheduleOnID IS NULL)
        THROW 51261, N'FREE MONTHLY MEMBER DETECTORS RECONCILIATION FAILED - #smap maps a schedule that is not in #sched. Refusing to publish.', 1;

    IF EXISTS (SELECT 1 FROM #smap sm
               LEFT JOIN #mem m ON m.MemberId = sm.MemberId
               WHERE m.MemberId IS NULL)
        THROW 51262, N'FREE MONTHLY MEMBER DETECTORS RECONCILIATION FAILED - #smap maps a schedule to a member absent from #mem. Members must come from the dimension master. Refusing to publish.', 1;

    /*===================================================================
      1. PER-MEMBER METRICS - driven from #mem so empty members survive
    ===================================================================*/
    INSERT #mm (MemberId, LmDue, LmOnTime, LmLate, LmOpen, TmDue, TmOpen, RmDue, RmLiab, RmNoOwner,
                OpenItems, OverdueItems, Overdue90Items, LiabOverdueItems, NeverTouchedOverdue, NoOwnerOpen)
    SELECT
        m.MemberId,
        ISNULL(SUM(CASE WHEN s.WindowPart = 'prev_month'                             THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.WindowPart = 'prev_month' AND s.Outcome = 'completed_on_time' THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.WindowPart = 'prev_month' AND s.Outcome = 'completed_late'    THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.WindowPart = 'prev_month' AND s.Outcome = 'open'              THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.WindowPart = 'curr_elapsed'                           THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.WindowPart = 'curr_elapsed' AND s.Outcome = 'open'    THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.WindowPart = 'curr_remaining'                         THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.WindowPart = 'curr_remaining' AND i.Imprisonment = 1  THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.WindowPart = 'curr_remaining' AND s.PerformerSource = 'none' THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.Outcome = 'open'                                      THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.IsOverdue = 1                                         THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.AgeBand = 'd091_plus'                                 THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.IsOverdue = 1 AND i.Imprisonment = 1                  THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.IsOverdue = 1 AND s.NeverTouched = 1                  THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.Outcome = 'open' AND s.PerformerSource = 'none'       THEN 1 ELSE 0 END), 0)
    FROM #mem m
    LEFT JOIN #smap  sm ON sm.MemberId = m.MemberId
    LEFT JOIN #sched s  ON s.ComplianceScheduleOnID = sm.ComplianceScheduleOnID
    LEFT JOIN #inst  i  ON i.ComplianceInstanceID = s.ComplianceInstanceID
    GROUP BY m.MemberId;

    /*  Member sums must tie to the mapped schedules - a fan-out in the
        caller's mapping would inflate every member silently.               */
    IF (SELECT ISNULL(SUM(OverdueItems), 0) FROM #mm)
       <> (SELECT COUNT(*) FROM #smap sm JOIN #sched s ON s.ComplianceScheduleOnID = sm.ComplianceScheduleOnID WHERE s.IsOverdue = 1)
        THROW 51263, N'FREE MONTHLY MEMBER DETECTORS RECONCILIATION FAILED - per-member overdue items do not tie to the mapped overdue schedules. Refusing to publish.', 1;

    /*===================================================================
      2. TENANT BASELINES - over ALL loaded schedules, mapped or not
         (an unowned item still belongs to the tenant's own average)
    ===================================================================*/
    DECLARE @tLmDue INT, @tLmOpen INT, @tLmOnTime INT, @tLmCompleted INT,
            @tOverdue INT, @tOverdue90 INT, @tLiabOverdue INT, @tRmDue INT, @tRmLiab INT;

    SELECT @tLmDue       = ISNULL(SUM(CASE WHEN s.WindowPart = 'prev_month' THEN 1 ELSE 0 END), 0),
           @tLmOpen      = ISNULL(SUM(CASE WHEN s.WindowPart = 'prev_month' AND s.Outcome = 'open' THEN 1 ELSE 0 END), 0),
           @tLmOnTime    = ISNULL(SUM(CASE WHEN s.WindowPart = 'prev_month' AND s.Outcome = 'completed_on_time' THEN 1 ELSE 0 END), 0),
           @tLmCompleted = ISNULL(SUM(CASE WHEN s.WindowPart = 'prev_month' AND s.Outcome IN ('completed_on_time','completed_late','completed_untimed') THEN 1 ELSE 0 END), 0),
           @tOverdue     = ISNULL(SUM(CASE WHEN s.IsOverdue = 1 THEN 1 ELSE 0 END), 0),
           @tOverdue90   = ISNULL(SUM(CASE WHEN s.AgeBand = 'd091_plus' THEN 1 ELSE 0 END), 0),
           @tLiabOverdue = ISNULL(SUM(CASE WHEN s.IsOverdue = 1 AND i.Imprisonment = 1 THEN 1 ELSE 0 END), 0),
           @tRmDue       = ISNULL(SUM(CASE WHEN s.WindowPart = 'curr_remaining' THEN 1 ELSE 0 END), 0),
           @tRmLiab      = ISNULL(SUM(CASE WHEN s.WindowPart = 'curr_remaining' AND i.Imprisonment = 1 THEN 1 ELSE 0 END), 0)
    FROM #sched s
    JOIN #inst  i ON i.ComplianceInstanceID = s.ComplianceInstanceID;

    DECLARE @tSlipRate  DECIMAL(9,4) = CASE WHEN @tLmDue   = 0 THEN 0 ELSE 1.0 * @tLmOpen      / @tLmDue   END;
    DECLARE @tLiabShare DECIMAL(9,4) = CASE WHEN @tOverdue = 0 THEN 0 ELSE 1.0 * @tLiabOverdue / @tOverdue END;
    DECLARE @tChronShare DECIMAL(9,4) = CASE WHEN @tOverdue = 0 THEN 0 ELSE 1.0 * @tOverdue90  / @tOverdue END;

    /*===================================================================
      3. ELIGIBILITY + FLAGS  (flag each row first, then count - never
         EXISTS inside an aggregate CASE)
    ===================================================================*/
    DECLARE @fl INT, @eligN INT;
    DECLARE @slipDeg BIT = 0, @liabDeg BIT = 0, @chronDeg BIT = 0;

    /*-- last_month_slippage: eligible = LmDue >= floor --------------------*/
    SET @fl = @MemberFloor;
    IF (SELECT COUNT(*) FROM #mm WHERE LmDue >= @fl) < 2 BEGIN SET @fl = 1; SET @slipDeg = 1; END
    UPDATE #mm SET EligSlip = 1 WHERE LmDue >= @fl;
    SET @eligN = (SELECT COUNT(*) FROM #mm WHERE EligSlip = 1);
    IF @eligN >= 2 AND @tSlipRate > 0
        UPDATE #mm SET FlagSlip = 1
         WHERE EligSlip = 1 AND LmOpen >= 1
           AND 1.0 * LmOpen / NULLIF(LmDue, 0) >= @tSlipRate * @RelativeRiskFactor;

    /*-- liability_share / chronic_backlog: eligible = OverdueItems >= floor */
    SET @fl = @MemberFloor;
    IF (SELECT COUNT(*) FROM #mm WHERE OverdueItems >= @fl) < 2 BEGIN SET @fl = 1; SET @liabDeg = 1; SET @chronDeg = 1; END
    UPDATE #mm SET EligLiab = 1, EligChron = 1 WHERE OverdueItems >= @fl;
    SET @eligN = (SELECT COUNT(*) FROM #mm WHERE EligLiab = 1);
    IF @eligN >= 2 AND @tLiabShare > 0
        UPDATE #mm SET FlagLiab = 1
         WHERE EligLiab = 1 AND LiabOverdueItems >= 1
           AND 1.0 * LiabOverdueItems / NULLIF(OverdueItems, 0) >= @tLiabShare * @RelativeRiskFactor;
    IF @eligN >= 2 AND @tChronShare > 0
        UPDATE #mm SET FlagChron = 1
         WHERE EligChron = 1 AND Overdue90Items >= 1
           AND 1.0 * Overdue90Items / NULLIF(OverdueItems, 0) >= @tChronShare * @RelativeRiskFactor;

    /*-- overdue_concentration: eligible = any overdue work; fair share = 1/N */
    UPDATE #mm SET EligConc = 1 WHERE OverdueItems >= 1;
    SET @eligN = (SELECT COUNT(*) FROM #mm WHERE EligConc = 1);
    IF @eligN >= 2 AND @tOverdue > 0
        UPDATE #mm SET FlagConc = 1
         WHERE EligConc = 1
           AND 1.0 * OverdueItems / @tOverdue >= @ConcentrationFactor / @eligN;

    /*===================================================================
      4. EMISSION POLICY
    ===================================================================*/
    INSERT #detector (Detector, Eligible, Flagged, Note)
    SELECT 'last_month_slippage',
           (SELECT COUNT(*) FROM #mm WHERE EligSlip = 1), (SELECT COUNT(*) FROM #mm WHERE FlagSlip = 1),
           CASE WHEN (SELECT COUNT(*) FROM #mm WHERE EligSlip = 1) < 2 THEN N'suppressed - fewer than 2 members with last-month work'
                WHEN @tSlipRate = 0 THEN N'nothing to compare - nothing from last month is still open'
                WHEN @slipDeg = 1   THEN N'degraded_peer_sample - materiality floor widened to 1'
                ELSE NULL END
    UNION ALL
    SELECT 'liability_share',
           (SELECT COUNT(*) FROM #mm WHERE EligLiab = 1), (SELECT COUNT(*) FROM #mm WHERE FlagLiab = 1),
           CASE WHEN (SELECT COUNT(*) FROM #mm WHERE EligLiab = 1) < 2 THEN N'suppressed - fewer than 2 members with overdue work'
                WHEN @tLiabShare = 0 THEN N'nothing to compare - no overdue item carries personal liability'
                WHEN @liabDeg = 1    THEN N'degraded_peer_sample - materiality floor widened to 1'
                ELSE NULL END
    UNION ALL
    SELECT 'chronic_backlog',
           (SELECT COUNT(*) FROM #mm WHERE EligChron = 1), (SELECT COUNT(*) FROM #mm WHERE FlagChron = 1),
           CASE WHEN (SELECT COUNT(*) FROM #mm WHERE EligChron = 1) < 2 THEN N'suppressed - fewer than 2 members with overdue work'
                WHEN @tChronShare = 0 THEN N'nothing to compare - nothing is overdue beyond 90 days'
                WHEN @chronDeg = 1    THEN N'degraded_peer_sample - materiality floor widened to 1'
                ELSE NULL END
    UNION ALL
    SELECT 'overdue_concentration',
           (SELECT COUNT(*) FROM #mm WHERE EligConc = 1), (SELECT COUNT(*) FROM #mm WHERE FlagConc = 1),
           CASE WHEN (SELECT COUNT(*) FROM #mm WHERE EligConc = 1) < 2 THEN N'suppressed - fewer than 2 members with overdue work'
                ELSE NULL END;

    UPDATE #detector
       SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END
     WHERE Detector IN ('last_month_slippage','liability_share','chronic_backlog','overdue_concentration');

    /*  Flagged >= 2 for aggregate: one member is not a pattern (sql/36).   */
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0                        THEN 'none'
                           WHEN Flagged >= 2 AND FlaggedPct > 20.0 THEN 'aggregate'
                           ELSE 'individual' END
     WHERE Detector IN ('last_month_slippage','liability_share','chronic_backlog','overdue_concentration');

    /*  Own code - never the shared 51040 (mapped to a dictionary-gap
        exception by SqlFreeDigestRepository).                               */
    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51264, N'DETECTOR CONTRACT VIOLATED - a free monthly member detector flagged more members than it declared eligible. Refusing to emit.', 1;

    DECLARE @slipMode VARCHAR(12)  = (SELECT EmitMode FROM #detector WHERE Detector = 'last_month_slippage');
    DECLARE @liabMode VARCHAR(12)  = (SELECT EmitMode FROM #detector WHERE Detector = 'liability_share');
    DECLARE @chronMode VARCHAR(12) = (SELECT EmitMode FROM #detector WHERE Detector = 'chronic_backlog');
    DECLARE @concMode VARCHAR(12)  = (SELECT EmitMode FROM #detector WHERE Detector = 'overdue_concentration');

    DECLARE @slipF INT  = (SELECT Flagged  FROM #detector WHERE Detector = 'last_month_slippage');
    DECLARE @slipE INT  = (SELECT Eligible FROM #detector WHERE Detector = 'last_month_slippage');
    DECLARE @liabF INT  = (SELECT Flagged  FROM #detector WHERE Detector = 'liability_share');
    DECLARE @liabE INT  = (SELECT Eligible FROM #detector WHERE Detector = 'liability_share');
    DECLARE @chronF INT = (SELECT Flagged  FROM #detector WHERE Detector = 'chronic_backlog');
    DECLARE @chronE INT = (SELECT Eligible FROM #detector WHERE Detector = 'chronic_backlog');
    DECLARE @concF INT  = (SELECT Flagged  FROM #detector WHERE Detector = 'overdue_concentration');
    DECLARE @concE INT  = (SELECT Eligible FROM #detector WHERE Detector = 'overdue_concentration');

    /*===================================================================
      5. CANDIDATES - individual mode only, top @MaxPerDetector by count.
         Percentages FLOORED (never overstate). Ties broken by MemberId so
         the order is deterministic.
    ===================================================================*/
    IF @slipMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, MetricPct, TenantPct, ItemCount, BaseCount, AsAtRequired, ProblemCount, PopulationCount)
        SELECT TOP (@MaxPerDetector)
               'last_month_slippage', 2, 2,
               ROW_NUMBER() OVER (ORDER BY x.LmOpen DESC, x.MemberId),
               @EntityKind, x.MemberId, m.MemberLabel,
               'last_month_still_open_pct',
               CAST(FLOOR(100.0 * x.LmOpen / NULLIF(x.LmDue, 0)) AS INT),
               CAST(FLOOR(100.0 * @tSlipRate) AS INT),
               x.LmOpen, x.LmDue, 1, @slipF, @slipE
        FROM #mm x JOIN #mem m ON m.MemberId = x.MemberId
        WHERE x.FlagSlip = 1
        ORDER BY x.LmOpen DESC, x.MemberId;

    IF @liabMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, MetricPct, TenantPct, ItemCount, BaseCount, AsAtRequired, ProblemCount, PopulationCount)
        SELECT TOP (@MaxPerDetector)
               'liability_share', 3, 1,
               ROW_NUMBER() OVER (ORDER BY x.LiabOverdueItems DESC, x.MemberId),
               @EntityKind, x.MemberId, m.MemberLabel,
               'overdue_with_liability_pct',
               CAST(FLOOR(100.0 * x.LiabOverdueItems / NULLIF(x.OverdueItems, 0)) AS INT),
               CAST(FLOOR(100.0 * @tLiabShare) AS INT),
               x.LiabOverdueItems, x.OverdueItems, 0, @liabF, @liabE
        FROM #mm x JOIN #mem m ON m.MemberId = x.MemberId
        WHERE x.FlagLiab = 1
        ORDER BY x.LiabOverdueItems DESC, x.MemberId;

    IF @chronMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, MetricPct, TenantPct, ItemCount, BaseCount, AsAtRequired, ProblemCount, PopulationCount)
        SELECT TOP (@MaxPerDetector)
               'chronic_backlog', 4, 2,
               ROW_NUMBER() OVER (ORDER BY x.Overdue90Items DESC, x.MemberId),
               @EntityKind, x.MemberId, m.MemberLabel,
               'overdue_over_90_days_pct',
               CAST(FLOOR(100.0 * x.Overdue90Items / NULLIF(x.OverdueItems, 0)) AS INT),
               CAST(FLOOR(100.0 * @tChronShare) AS INT),
               x.Overdue90Items, x.OverdueItems, 0, @chronF, @chronE
        FROM #mm x JOIN #mem m ON m.MemberId = x.MemberId
        WHERE x.FlagChron = 1
        ORDER BY x.Overdue90Items DESC, x.MemberId;

    /*  Concentration: MetricPct = the member's share of ALL overdue items;
        TenantPct is NULL - the comparison is to a fair share, not a rate.  */
    IF @concMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, MetricPct, TenantPct, ItemCount, BaseCount, AsAtRequired, ProblemCount, PopulationCount)
        SELECT TOP (@MaxPerDetector)
               'overdue_concentration', 5, 2,
               ROW_NUMBER() OVER (ORDER BY x.OverdueItems DESC, x.MemberId),
               @EntityKind, x.MemberId, m.MemberLabel,
               'share_of_all_overdue_pct',
               CAST(FLOOR(100.0 * x.OverdueItems / @tOverdue) AS INT),
               NULL,
               x.OverdueItems, @tOverdue, 0, @concF, @concE
        FROM #mm x JOIN #mem m ON m.MemberId = x.MemberId
        WHERE x.FlagConc = 1
        ORDER BY x.OverdueItems DESC, x.MemberId;

    /*===================================================================
      6. FACTS - tenant context (every dimension week needs the totals its
         findings are measured against) + aggregate-mode patterns, unnamed
    ===================================================================*/
    INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
    VALUES
     ('t_lm_due',          @tLmDue,      N'items fell due last month across your scope',                                   'context', 800, 'prev',  'volume',                 5, 1, NULL),
     ('t_lm_still_open',   @tLmOpen,     N'of last month''s items are still open across your scope',                       'context', 805, 'prev',  'operational_continuity', 3, 1, NULL),
     ('t_od_total',        @tOverdue,    N'items are overdue today across your scope, whatever their due date',            'context', 820, 'stock', 'operational_continuity', 3, 0, NULL),
     ('t_od_over_90_days', @tOverdue90,  N'of those have been overdue for more than 90 days',                              'context', 825, 'stock', 'operational_continuity', 3, 0, NULL),
     ('t_od_liability',    @tLiabOverdue,N'overdue items across your scope that carry personal criminal liability',        'context', 830, 'stock', 'personal_liability',     2, 0, NULL),
     ('t_rm_due',          @tRmDue,      N'items fall due between today and the end of the month across your scope',       'context', 840, 'curr',  'volume',                 5, 0, NULL),
     ('t_rm_liability',    @tRmLiab,     N'of those carry personal criminal liability',                                    'context', 845, 'curr',  'personal_liability',     2, 0, NULL);

    IF @tLmCompleted > 0
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('t_lm_on_time_pct', CAST(FLOOR(100.0 * @tLmOnTime / @tLmCompleted) AS INT),
                N'per cent of last month''s completed items were closed on time across your scope (rounded down)',
                'context', 810, 'prev', 'performance', 4, 1, NULL);

    IF @slipMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_last_month_slippage',    @slipF, @EntityPlural + N' still have an unusually large share of last month''s items open', 'patterns', 700, 'prev', 'operational_continuity', 2, 1, 2),
               ('pat_last_month_slippage_of', @slipE, @EntityPlural + N' with last-month work were compared',                              'patterns', 701, 'ctx',  'volume',                 5, 0, NULL);

    IF @liabMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_liability_share',    @liabF, @EntityPlural + N' have an unusually high share of overdue work carrying personal criminal liability', 'patterns', 710, 'stock', 'personal_liability', 1, 0, 3),
               ('pat_liability_share_of', @liabE, @EntityPlural + N' with overdue work were compared',                                                   'patterns', 711, 'ctx',   'volume',             5, 0, NULL);

    IF @chronMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_chronic_backlog',    @chronF, @EntityPlural + N' have an unusually high share of overdue work open for more than 90 days', 'patterns', 720, 'stock', 'operational_continuity', 2, 0, 4),
               ('pat_chronic_backlog_of', @chronE, @EntityPlural + N' with overdue work were compared',                                       'patterns', 721, 'ctx',   'volume',                 5, 0, NULL);

    IF @concMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_overdue_concentration',    @concF, @EntityPlural + N' each hold a disproportionate share of all overdue work', 'patterns', 730, 'stock', 'operational_continuity', 2, 0, 5),
               ('pat_overdue_concentration_of', @concE, @EntityPlural + N' with overdue work were compared',                         'patterns', 731, 'ctx',   'volume',                 5, 0, NULL);

    /*===================================================================
      7. EXAMPLES - aggregate mode only, top @MaxExamples by the SAME
         measure and order the detector ranks on in individual mode.
         Counts only, never a rate (see header). Members with no label are
         never examples: the WHERE is explicit, not incidental.
    ===================================================================*/
    IF @slipMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples)
               'last_month_slippage', 'pat_last_month_slippage',
               ROW_NUMBER() OVER (ORDER BY x.LmOpen DESC, x.MemberId),
               @EntityKind, x.MemberId, m.MemberLabel, x.LmOpen, x.LmDue,
               N'of the obligations it had due last month are still open'
        FROM #mm x JOIN #mem m ON m.MemberId = x.MemberId
        WHERE x.FlagSlip = 1 AND m.MemberLabel IS NOT NULL
        ORDER BY x.LmOpen DESC, x.MemberId;

    IF @liabMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples)
               'liability_share', 'pat_liability_share',
               ROW_NUMBER() OVER (ORDER BY x.LiabOverdueItems DESC, x.MemberId),
               @EntityKind, x.MemberId, m.MemberLabel, x.LiabOverdueItems, x.OverdueItems,
               N'of its overdue obligations carry personal criminal liability'
        FROM #mm x JOIN #mem m ON m.MemberId = x.MemberId
        WHERE x.FlagLiab = 1 AND m.MemberLabel IS NOT NULL
        ORDER BY x.LiabOverdueItems DESC, x.MemberId;

    IF @chronMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples)
               'chronic_backlog', 'pat_chronic_backlog',
               ROW_NUMBER() OVER (ORDER BY x.Overdue90Items DESC, x.MemberId),
               @EntityKind, x.MemberId, m.MemberLabel, x.Overdue90Items, x.OverdueItems,
               N'of its overdue obligations have been overdue for more than 90 days'
        FROM #mm x JOIN #mem m ON m.MemberId = x.MemberId
        WHERE x.FlagChron = 1 AND m.MemberLabel IS NOT NULL
        ORDER BY x.Overdue90Items DESC, x.MemberId;

    /*  Concentration: BaseCount is the scope's whole overdue total (= the
        t_od_total fact), so "holds 210 of the 2,853 overdue across your
        scope" passes the scope-wide check in C#.                            */
    IF @concMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples)
               'overdue_concentration', 'pat_overdue_concentration',
               ROW_NUMBER() OVER (ORDER BY x.OverdueItems DESC, x.MemberId),
               @EntityKind, x.MemberId, m.MemberLabel, x.OverdueItems, @tOverdue,
               N'of all the overdue obligations across your scope sit here'
        FROM #mm x JOIN #mem m ON m.MemberId = x.MemberId
        WHERE x.FlagConc = 1 AND m.MemberLabel IS NOT NULL
        ORDER BY x.OverdueItems DESC, x.MemberId;
END
GO
PRINT 'usp_Insights_FreeMonthly_MemberDetectors restored.';
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Licence', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Licence;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_Licence
    @UserID              INT,
    @CustomerID          INT,
    @CurrMonthStart      DATE,
    @AsOf                DATETIME,
    @RelativeRiskFactor  DECIMAL(4,2) = 1.50,
    @TypeFloor           INT          = 10,    -- min licences for a type to be compared (declared fallback to 1)
    @MaxPerDetector      INT          = 5,
    @MaxExamples         INT          = 3      -- examples named inside an aggregate-mode pattern (2026-09-23)
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID('tempdb..#lic') IS NOT NULL DROP TABLE #lic;
    CREATE TABLE #lic (
        LicenseId BIGINT NOT NULL PRIMARY KEY, BranchID INT NOT NULL, LicenseTypeID BIGINT NULL,
        LicenseTypeName NVARCHAR(MAX) NULL, EndDate DATETIME NULL, StatusBucket NVARCHAR(200) NULL,
        LicenceState VARCHAR(12) NOT NULL, RenewalInProgress BIT NOT NULL, LapsesRestOfMonth BIT NOT NULL,
        LapsedThisMonth BIT NOT NULL, LapsedLastMonth BIT NOT NULL);

    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) NOT NULL PRIMARY KEY, Eligible INT NOT NULL, Flagged INT NOT NULL,
        FlaggedPct DECIMAL(5,1) NULL, EmitMode VARCHAR(12) NULL, Note NVARCHAR(200) NULL);

    IF OBJECT_ID('tempdb..#cand') IS NOT NULL DROP TABLE #cand;
    CREATE TABLE #cand (
        Detector VARCHAR(40) NOT NULL, Priority TINYINT NOT NULL, SeverityTier TINYINT NOT NULL,
        RankInDetector INT NOT NULL, EntityKind VARCHAR(20) NOT NULL, EntityId BIGINT NULL,
        EntityLabel NVARCHAR(MAX) NULL, ContextKind VARCHAR(20) NULL, ContextLabel NVARCHAR(MAX) NULL,
        Metric VARCHAR(40) NOT NULL, MetricPct INT NULL, TenantPct INT NULL, ItemCount INT NULL,
        BaseCount INT NULL, EventDate DATE NULL, AsAtRequired BIT NOT NULL DEFAULT 0,
        ProblemCount INT NOT NULL, PopulationCount INT NOT NULL, DefaultSlot TINYINT NULL);

    /*  Examples for aggregate-mode patterns (grid #6). Shared shape - byte-identical in sql/36,
        38, 39, 40, 41; sql/37 fills it for the four shared detectors.                          */
    IF OBJECT_ID('tempdb..#eg') IS NOT NULL DROP TABLE #eg;
    CREATE TABLE #eg (
        Detector VARCHAR(40) NOT NULL, PatternFactKey VARCHAR(40) NOT NULL, ExampleRank INT NOT NULL,
        EntityKind VARCHAR(20) NOT NULL, EntityId BIGINT NULL, EntityLabel NVARCHAR(MAX) NOT NULL,
        ContextKind VARCHAR(20) NULL, ContextLabel NVARCHAR(MAX) NULL,
        ItemCount INT NULL, BaseCount INT NULL, UnitLabel NVARCHAR(200) NOT NULL);

    IF OBJECT_ID('tempdb..#facts') IS NOT NULL DROP TABLE #facts;
    CREATE TABLE #facts (
        FactKey VARCHAR(40) NOT NULL PRIMARY KEY, FactValue INT NOT NULL, DisplayLabel NVARCHAR(MAX) NOT NULL,
        Section VARCHAR(16) NOT NULL, DisplayOrder INT NOT NULL, WindowScope VARCHAR(6) NOT NULL,
        ImpactClass VARCHAR(24) NOT NULL, SeverityTier TINYINT NOT NULL, AsAtRequired BIT NOT NULL,
        HeadlineRank TINYINT NULL, IsHeadline BIT NOT NULL DEFAULT 0);

    /*  Validates window, scope and the licence dictionary; THROWs on failure. */
    EXEC dbo.usp_Insights_FreeMonthly_LoadLicences @UserID, @CustomerID, @CurrMonthStart, @AsOf;

    DECLARE @CurrStart DATETIME = CAST(@CurrMonthStart AS DATETIME);
    DECLARE @PrevStart DATETIME = DATEADD(MONTH, -1, @CurrStart);
    DECLARE @NextStart DATETIME = DATEADD(MONTH,  1, @CurrStart);

    /*===================================================================
      1. TOTALS + RECONCILIATION (all before any result set)
    ===================================================================*/
    DECLARE @total INT, @valid INT, @lapsed INT, @endedOther INT, @noEnd INT, @otherStatus INT,
            @lapsedUnrenewed INT, @lapsedRenewing INT,
            @expRom INT, @expRomUnrenewed INT, @expRomRenewing INT,
            @lapsedLm INT, @lapsedLmUnrenewed INT, @lapsedTm INT, @lapsedTmUnrenewed INT,
            @untyped INT, @noStatusRow INT;

    SELECT @total             = COUNT(*),
           @valid             = ISNULL(SUM(CASE WHEN LicenceState = 'valid'       THEN 1 ELSE 0 END), 0),
           @lapsed            = ISNULL(SUM(CASE WHEN LicenceState = 'lapsed'      THEN 1 ELSE 0 END), 0),
           @endedOther        = ISNULL(SUM(CASE WHEN LicenceState = 'ended_other' THEN 1 ELSE 0 END), 0),
           @noEnd             = ISNULL(SUM(CASE WHEN LicenceState = 'no_end_date' THEN 1 ELSE 0 END), 0),
           @otherStatus       = ISNULL(SUM(CASE WHEN LicenceState = 'other_status' THEN 1 ELSE 0 END), 0),   -- 2026-09-29, sql/35
           @lapsedUnrenewed   = ISNULL(SUM(CASE WHEN LicenceState = 'lapsed' AND RenewalInProgress = 0 THEN 1 ELSE 0 END), 0),
           @lapsedRenewing    = ISNULL(SUM(CASE WHEN LicenceState = 'lapsed' AND RenewalInProgress = 1 THEN 1 ELSE 0 END), 0),
           @expRom            = ISNULL(SUM(CASE WHEN LapsesRestOfMonth = 1 THEN 1 ELSE 0 END), 0),
           @expRomUnrenewed   = ISNULL(SUM(CASE WHEN LapsesRestOfMonth = 1 AND RenewalInProgress = 0 THEN 1 ELSE 0 END), 0),
           @expRomRenewing    = ISNULL(SUM(CASE WHEN LapsesRestOfMonth = 1 AND RenewalInProgress = 1 THEN 1 ELSE 0 END), 0),
           @lapsedLm          = ISNULL(SUM(CASE WHEN LapsedLastMonth = 1 THEN 1 ELSE 0 END), 0),
           @lapsedLmUnrenewed = ISNULL(SUM(CASE WHEN LapsedLastMonth = 1 AND RenewalInProgress = 0 THEN 1 ELSE 0 END), 0),
           @lapsedTm          = ISNULL(SUM(CASE WHEN LapsedThisMonth = 1 THEN 1 ELSE 0 END), 0),
           @lapsedTmUnrenewed = ISNULL(SUM(CASE WHEN LapsedThisMonth = 1 AND RenewalInProgress = 0 THEN 1 ELSE 0 END), 0),
           @untyped           = ISNULL(SUM(CASE WHEN LicenseTypeID IS NULL THEN 1 ELSE 0 END), 0),
           @noStatusRow       = ISNULL(SUM(CASE WHEN StatusBucket IS NULL THEN 1 ELSE 0 END), 0)
    FROM #lic;

    IF @total <> @valid + @lapsed + @endedOther + @noEnd + @otherStatus
        THROW 51301, N'FREE MONTHLY LICENCE RECONCILIATION FAILED - licence states do not partition the scoped licence set. Refusing to publish.', 1;

    /*  Per-location members: every licence sits on exactly one branch.      */
    IF OBJECT_ID('tempdb..#licLoc') IS NOT NULL DROP TABLE #licLoc;
    CREATE TABLE #licLoc (
        BranchID           INT           NOT NULL PRIMARY KEY,
        BranchName         NVARCHAR(MAX) NULL,
        Licences           INT           NOT NULL,
        ExpiredUnrenewed   INT           NOT NULL,
        IsFlagged          BIT           NOT NULL DEFAULT 0
    );
    INSERT #licLoc (BranchID, BranchName, Licences, ExpiredUnrenewed)
    SELECT l.BranchID, MAX(cb.Name), COUNT(*),      -- group by id only: never GROUP BY a name column
           SUM(CASE WHEN l.LicenceState = 'lapsed' AND l.RenewalInProgress = 0 THEN 1 ELSE 0 END)
    FROM #lic l
    JOIN CustomerBranch cb ON cb.ID = l.BranchID
    GROUP BY l.BranchID;

    IF (SELECT ISNULL(SUM(Licences), 0) FROM #licLoc) <> @total
        THROW 51302, N'FREE MONTHLY LICENCE RECONCILIATION FAILED - per-location licence counts do not sum to the scoped total. Refusing to publish.', 1;

    /*  Per-type members. Sec.4a: typed rows cover part of the estate, so the
        field is TypedLicences, paired with the named residual UntypedLicences. */
    IF OBJECT_ID('tempdb..#licType') IS NOT NULL DROP TABLE #licType;
    CREATE TABLE #licType (
        LicenseTypeID      BIGINT        NOT NULL PRIMARY KEY,
        LicenseTypeName    NVARCHAR(MAX) NULL,
        Licences           INT           NOT NULL,
        ExpiredUnrenewed   INT           NOT NULL,
        IsMaterial         BIT           NOT NULL DEFAULT 0,
        IsFlagged          BIT           NOT NULL DEFAULT 0
    );
    INSERT #licType (LicenseTypeID, LicenseTypeName, Licences, ExpiredUnrenewed)
    SELECT l.LicenseTypeID, MAX(l.LicenseTypeName), COUNT(*),
           SUM(CASE WHEN l.LicenceState = 'lapsed' AND l.RenewalInProgress = 0 THEN 1 ELSE 0 END)
    FROM #lic l
    WHERE l.LicenseTypeID IS NOT NULL
    GROUP BY l.LicenseTypeID;

    DECLARE @typed INT = (SELECT ISNULL(SUM(Licences), 0) FROM #licType);
    IF @typed + @untyped <> @total
        THROW 51303, N'FREE MONTHLY LICENCE RECONCILIATION FAILED - typed plus untyped licences do not tie to the scoped total. Refusing to publish.', 1;

    /*===================================================================
      2. DETECTORS - expired-unrenewed rate, peer-relative
    ===================================================================*/
    DECLARE @tRate DECIMAL(9,4) = CASE WHEN @total = 0 THEN 0 ELSE 1.0 * @lapsedUnrenewed / @total END;

    DECLARE @locE INT = (SELECT COUNT(*) FROM #licLoc);
    IF @locE >= 2 AND @tRate > 0
        UPDATE #licLoc SET IsFlagged = 1
         WHERE ExpiredUnrenewed >= 1
           AND 1.0 * ExpiredUnrenewed / Licences >= @tRate * @RelativeRiskFactor;
    DECLARE @locF INT = (SELECT COUNT(*) FROM #licLoc WHERE IsFlagged = 1);

    DECLARE @tf INT = @TypeFloor, @typeDeg BIT = 0;
    IF (SELECT COUNT(*) FROM #licType WHERE Licences >= @tf) < 2 BEGIN SET @tf = 1; SET @typeDeg = 1; END
    UPDATE #licType SET IsMaterial = 1 WHERE Licences >= @tf;
    DECLARE @typeE INT = (SELECT COUNT(*) FROM #licType WHERE IsMaterial = 1);
    IF @typeE >= 2 AND @tRate > 0
        UPDATE #licType SET IsFlagged = 1
         WHERE IsMaterial = 1 AND ExpiredUnrenewed >= 1
           AND 1.0 * ExpiredUnrenewed / Licences >= @tRate * @RelativeRiskFactor;
    DECLARE @typeF INT = (SELECT COUNT(*) FROM #licType WHERE IsFlagged = 1);

    INSERT #detector (Detector, Eligible, Flagged, Note)
    VALUES ('expired_unrenewed_location', @locE, @locF,
            CASE WHEN @locE < 2   THEN N'suppressed - fewer than 2 locations hold licences'
                 WHEN @tRate = 0  THEN N'nothing to compare - no licence is expired without a renewal' END),
           ('licence_type_lapse_rate', @typeE, @typeF,
            CASE WHEN @typeE < 2  THEN N'suppressed - fewer than 2 licence types to compare'
                 WHEN @tRate = 0  THEN N'nothing to compare - no licence is expired without a renewal'
                 WHEN @typeDeg = 1 THEN N'degraded_peer_sample - materiality floor widened to 1' END);

    UPDATE #detector
       SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END,
           EmitMode   = CASE WHEN Flagged = 0 THEN 'none'
                             WHEN Flagged >= 2 AND 100.0 * Flagged / NULLIF(Eligible, 0) > 20.0 THEN 'aggregate'
                             ELSE 'individual' END;

    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51304, N'DETECTOR CONTRACT VIOLATED - a free monthly licence detector flagged more members than it declared eligible. Refusing to emit.', 1;

    DECLARE @locMode  VARCHAR(12) = (SELECT EmitMode FROM #detector WHERE Detector = 'expired_unrenewed_location');
    DECLARE @typeMode VARCHAR(12) = (SELECT EmitMode FROM #detector WHERE Detector = 'licence_type_lapse_rate');

    /*===================================================================
      3. CANDIDATES
    ===================================================================*/
    /*  P1 - expiring before month end, no renewal filed, soonest first.     */
    INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                  ContextKind, ContextLabel, Metric, EventDate, ProblemCount, PopulationCount)
    SELECT TOP (@MaxPerDetector) 'licence_expiring_unrenewed', 1, 1,
           ROW_NUMBER() OVER (ORDER BY l.EndDate ASC, l.LicenseId ASC),
           'licence', l.LicenseId, l.LicenseTypeName, 'location', cb.Name,
           'expires_on', CAST(l.EndDate AS DATE), @expRomUnrenewed, @expRom
    FROM #lic l
    JOIN CustomerBranch cb ON cb.ID = l.BranchID
    WHERE l.LapsesRestOfMonth = 1 AND l.RenewalInProgress = 0
    ORDER BY l.EndDate ASC, l.LicenseId ASC;

    /*  P2 - lapsed last month or so far this month, still no renewal;
        longest-lapsed first. AsAtRequired: a renewal may be filed any day.  */
    INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                  ContextKind, ContextLabel, Metric, EventDate, AsAtRequired, ProblemCount, PopulationCount)
    SELECT TOP (@MaxPerDetector) 'licence_lapsed_recent_unrenewed', 2, 1,
           ROW_NUMBER() OVER (ORDER BY l.EndDate ASC, l.LicenseId ASC),
           'licence', l.LicenseId, l.LicenseTypeName, 'location', cb.Name,
           'expired_on', CAST(l.EndDate AS DATE), 1,
           @lapsedLmUnrenewed + @lapsedTmUnrenewed, @lapsedLm + @lapsedTm
    FROM #lic l
    JOIN CustomerBranch cb ON cb.ID = l.BranchID
    WHERE (l.LapsedLastMonth = 1 OR l.LapsedThisMonth = 1) AND l.RenewalInProgress = 0
    ORDER BY l.EndDate ASC, l.LicenseId ASC;

    IF @locMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, MetricPct, TenantPct, ItemCount, BaseCount, ProblemCount, PopulationCount)
        SELECT TOP (@MaxPerDetector) 'expired_unrenewed_location', 3, 1,
               ROW_NUMBER() OVER (ORDER BY ll.ExpiredUnrenewed DESC, ll.BranchID),
               'location', ll.BranchID, ll.BranchName,
               'licences_expired_unrenewed_pct',
               CAST(FLOOR(100.0 * ll.ExpiredUnrenewed / ll.Licences) AS INT),
               CAST(FLOOR(100.0 * @tRate) AS INT),
               ll.ExpiredUnrenewed, ll.Licences, @locF, @locE
        FROM #licLoc ll
        WHERE ll.IsFlagged = 1
        ORDER BY ll.ExpiredUnrenewed DESC, ll.BranchID;

    IF @typeMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, MetricPct, TenantPct, ItemCount, BaseCount, ProblemCount, PopulationCount)
        SELECT TOP (@MaxPerDetector) 'licence_type_lapse_rate', 4, 2,
               ROW_NUMBER() OVER (ORDER BY lt.ExpiredUnrenewed DESC, lt.LicenseTypeID),
               'licence_type', lt.LicenseTypeID, lt.LicenseTypeName,
               'licences_expired_unrenewed_pct',
               CAST(FLOOR(100.0 * lt.ExpiredUnrenewed / lt.Licences) AS INT),
               CAST(FLOOR(100.0 * @tRate) AS INT),
               lt.ExpiredUnrenewed, lt.Licences, @typeF, @typeE
        FROM #licType lt
        WHERE lt.IsFlagged = 1
        ORDER BY lt.ExpiredUnrenewed DESC, lt.LicenseTypeID;

    /*===================================================================
      4. FACTS
    ===================================================================*/
    INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
    VALUES
     ('lic_total',                        @total,             N'licences tracked in your scope',                                                'licences', 100, 'ctx',   'volume',             5, 0, NULL),
     ('lic_valid',                        @valid,             N'of those are valid today',                                                      'licences', 110, 'stock', 'volume',             5, 0, NULL),
     ('lic_expiring_rest_of_month',       @expRom,            N'licences expire between today and the end of the month',                        'licences', 200, 'curr',  'licence_continuity', 2, 0, 5),
     ('lic_expiring_unrenewed',           @expRomUnrenewed,   N'of those have no renewal filed',                                                'licences', 210, 'curr',  'licence_continuity', 1, 0, 1),
     ('lic_expiring_renewal_filed',       @expRomRenewing,    N'of those already have a renewal in progress',                                   'licences', 220, 'curr',  'volume',             4, 0, NULL),
     ('lic_lapsed_last_month',            @lapsedLm,          N'licences expired during last month',                                            'licences', 300, 'prev',  'licence_continuity', 2, 1, NULL),
     ('lic_lapsed_last_month_unrenewed',  @lapsedLmUnrenewed, N'of those still have no renewal in progress',                                    'licences', 310, 'prev',  'licence_continuity', 1, 1, 3),
     ('lic_lapsed_this_month',            @lapsedTm,          N'licences have expired so far this month',                                       'licences', 320, 'curr',  'licence_continuity', 2, 0, NULL),
     ('lic_lapsed_this_month_unrenewed',  @lapsedTmUnrenewed, N'of those have no renewal in progress',                                          'licences', 330, 'curr',  'licence_continuity', 1, 0, 2),
     ('lic_expired_total',                @lapsed,            N'licences are expired today',                                                    'licences', 400, 'stock', 'licence_continuity', 2, 0, NULL),
     ('lic_expired_unrenewed',            @lapsedUnrenewed,   N'of those have no renewal in progress',                                          'licences', 410, 'stock', 'licence_continuity', 1, 0, 4),
     ('lic_expired_renewing',             @lapsedRenewing,    N'of those have a renewal in progress',                                           'licences', 420, 'stock', 'volume',             4, 0, NULL),
     ('lic_ended_other',                  @endedOther,        N'licences ended another way (terminated, rejected, renewed or not applicable)',   'licences', 500, 'stock', 'volume',             5, 0, NULL),
     ('lic_types_in_scope',               (SELECT COUNT(*) FROM #licType), N'licence types are held across your scope',                        'licences', 510, 'ctx',   'volume',             5, 0, NULL),
     ('loc_with_licences',                @locE,              N'locations hold at least one licence',                                           'licences', 520, 'ctx',   'volume',             5, 0, NULL),
     ('loc_with_expired_unrenewed',       (SELECT COUNT(*) FROM #licLoc WHERE ExpiredUnrenewed >= 1),
                                                              N'locations hold at least one expired licence with no renewal in progress',       'licences', 530, 'stock', 'licence_continuity', 2, 0, NULL);

    IF @locMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_expired_unrenewed_location',    @locF, N'locations have an unusually high share of licences expired with no renewal - a pattern', 'patterns', 700, 'stock', 'licence_continuity', 1, 0, 4),
               ('pat_expired_unrenewed_location_of', @locE, N'locations holding licences were compared',                                             'patterns', 701, 'ctx',   'volume',             5, 0, NULL);

    IF @typeMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_licence_type_lapse_rate',    @typeF, N'licence types lapse without renewal unusually often - a pattern', 'patterns', 710, 'stock', 'licence_continuity', 2, 0, 5),
               ('pat_licence_type_lapse_rate_of', @typeE, N'licence types were compared',                                    'patterns', 711, 'ctx',   'volume',             5, 0, NULL);

    /*  EXAMPLES - the sites / types holding the most expired-unrenewed licences. Same order
        as individual mode, counts only. An unnamed type (NULL master name) is never one.    */
    IF @locMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples) 'expired_unrenewed_location', 'pat_expired_unrenewed_location',
               ROW_NUMBER() OVER (ORDER BY ll.ExpiredUnrenewed DESC, ll.BranchID),
               'location', ll.BranchID, ll.BranchName, ll.ExpiredUnrenewed, ll.Licences,
               N'of the licences held at this location are expired with no renewal in progress'
        FROM #licLoc ll
        WHERE ll.IsFlagged = 1 AND ll.BranchName IS NOT NULL
        ORDER BY ll.ExpiredUnrenewed DESC, ll.BranchID;

    IF @typeMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples) 'licence_type_lapse_rate', 'pat_licence_type_lapse_rate',
               ROW_NUMBER() OVER (ORDER BY lt.ExpiredUnrenewed DESC, lt.LicenseTypeID),
               'licence_type', lt.LicenseTypeID, lt.LicenseTypeName, lt.ExpiredUnrenewed, lt.Licences,
               N'of the licences of this type are expired with no renewal in progress'
        FROM #licType lt
        WHERE lt.IsFlagged = 1 AND lt.LicenseTypeName IS NOT NULL
        ORDER BY lt.ExpiredUnrenewed DESC, lt.LicenseTypeID;

    /*===================================================================
      5. DEFAULT SLOTS + HEADLINE (identical block in every dimension slot)
    ===================================================================*/
    ;WITH s1 AS (SELECT TOP 1 * FROM #cand WHERE RankInDetector = 1 ORDER BY Priority, Detector)
    UPDATE s1 SET DefaultSlot = 1;

    ;WITH s2 AS (
        SELECT TOP 1 c.* FROM #cand c
        WHERE c.RankInDetector = 1 AND c.DefaultSlot IS NULL
          AND NOT EXISTS (SELECT 1 FROM #cand f
                          WHERE f.DefaultSlot = 1 AND f.EntityKind = c.EntityKind AND f.EntityId = c.EntityId)
        ORDER BY c.Priority, c.Detector)
    UPDATE s2 SET DefaultSlot = 2;

    DECLARE @bestCand INT = (SELECT MIN(Priority) FROM #cand WHERE DefaultSlot = 1);
    DECLARE @bestFact INT = (SELECT MIN(HeadlineRank) FROM #facts WHERE HeadlineRank IS NOT NULL AND FactValue > 0);
    DECLARE @headlineSource VARCHAR(10) =
        CASE WHEN @bestCand IS NOT NULL AND (@bestFact IS NULL OR @bestCand <= @bestFact) THEN 'candidate' ELSE 'fact' END;

    IF @headlineSource = 'fact'
    BEGIN
        ;WITH pick AS (SELECT TOP 1 FactKey FROM #facts
                       WHERE HeadlineRank IS NOT NULL AND FactValue > 0
                       ORDER BY HeadlineRank, DisplayOrder)
        UPDATE f SET IsHeadline = 1 FROM #facts f JOIN pick p ON p.FactKey = f.FactKey;

        IF NOT EXISTS (SELECT 1 FROM #facts WHERE IsHeadline = 1)
            UPDATE #facts SET IsHeadline = 1 WHERE FactKey = 'lic_total';
    END

    /*  EXAMPLES CONTRACT (2026-09-23 design, Sec.2.7). An example exists only in aggregate
        mode, beside the pattern fact it illustrates. Codes from this file's own block.       */
    IF EXISTS (SELECT 1 FROM #eg e JOIN #cand c ON c.Detector = e.Detector)
        THROW 51301, N'EXAMPLES CONTRACT VIOLATED - a free monthly licence detector emitted both named candidates and examples. Examples exist only in aggregate mode. Refusing to emit.', 1;
    IF EXISTS (SELECT 1 FROM #eg e LEFT JOIN #facts f ON f.FactKey = e.PatternFactKey WHERE f.FactKey IS NULL)
        THROW 51305, N'EXAMPLES CONTRACT VIOLATED - a free monthly licence example refers to a pattern fact that was not emitted. Refusing to emit.', 1;

    /*===================================================================
      6. EMIT
    ===================================================================*/
    SELECT 'control_totals'                           AS ResultSet,
           @CustomerID                                AS CustomerID,
           @UserID                                    AS UserID,
           'licence'                                  AS Slot,
           CAST(@PrevStart AS DATE)                   AS PrevMonthStart,
           CAST(DATEADD(DAY, -1, @CurrStart) AS DATE) AS PrevMonthEnd,
           CAST(@CurrStart AS DATE)                   AS CurrMonthStart,
           CAST(DATEADD(DAY, -1, @NextStart) AS DATE) AS CurrMonthEnd,
           @AsOf                                      AS AsOf,
           @total                                     AS ScopedLicences,
           @typed                                     AS TypedLicences,     /* + UntypedLicences = ScopedLicences (Sec.4a) */
           @untyped                                   AS UntypedLicences,
           @locE                                      AS LocationsWithLicences,
           CAST(1 AS BIT)                             AS Reconciled,
           @headlineSource                            AS HeadlineSource;

    SELECT 'facts' AS ResultSet, FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope,
           ImpactClass, SeverityTier, AsAtRequired, IsHeadline
    FROM #facts ORDER BY DisplayOrder;

    SELECT 'detector_policy' AS ResultSet, Detector, Eligible, Flagged, FlaggedPct, EmitMode, Note
    FROM #detector;

    SELECT 'candidates' AS ResultSet, DefaultSlot, Detector, Priority, SeverityTier, RankInDetector,
           EntityKind, EntityId, EntityLabel, ContextKind, ContextLabel, Metric, MetricPct, TenantPct,
           ItemCount, BaseCount, EventDate, AsAtRequired, ProblemCount, PopulationCount,
           ProblemCount - 1 AS ResidualCount
    FROM #cand
    ORDER BY CASE WHEN DefaultSlot IS NULL THEN 9 ELSE DefaultSlot END, Priority, Detector, RankInDetector;

    SELECT 'data_quality' AS ResultSet, Code, ItemCount, Detail
    FROM (VALUES
        ('no_licences_in_scope', CASE WHEN @total = 0 THEN 1 ELSE 0 END,
         N'1 = no licences are tracked in this recipient''s scope. The email says so plainly; it does not switch topic.'),
        ('licences_without_end_date', @noEnd,
         N'Licences with no EndDate cannot be assessed for lapse and are excluded from every expiry fact.'),
        ('licences_without_status_row', @noStatusRow,
         N'Licences with no status transaction at all. Still lapse-eligible if past EndDate - absence is not exclusion (BA ruling, sql/21).'),
        ('untyped_licences', @untyped,
         N'Licences with no type. Counted in every total; not compared in licence_type_lapse_rate.'),
        ('licence_scope_branch_only', CASE WHEN @total > 0 THEN 1 ELSE 0 END,
         N'Licences are scoped by authorised branch only (no category link without Lic_tbl_LicenseComplianceInstanceMapping).')
    ) AS dq(Code, ItemCount, Detail);

    /*  Grid #6 - APPENDED LAST, never before data_quality (a reader not yet updated would
        map example rows onto its data_quality shape). Empty is normal.                     */
    SELECT 'examples' AS ResultSet, Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel,
           ContextKind, ContextLabel, ItemCount, BaseCount, UnitLabel
    FROM #eg
    ORDER BY PatternFactKey, ExampleRank;
END
GO
PRINT 'usp_Insights_FreeMonthly_Licence restored.';
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Act', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Act;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_Act
    @UserID              INT,
    @CustomerID          INT,
    @CurrMonthStart      DATE,
    @AsOf                DATETIME,
    @RelativeRiskFactor  DECIMAL(4,2) = 1.50,
    @ConcentrationFactor DECIMAL(4,2) = 2.00,
    @MemberFloor         INT          = 5,
    @MaxPerDetector      INT          = 5,
    @MaxExamples         INT          = 3      -- examples named inside an aggregate-mode pattern (2026-09-23)
AS
BEGIN
    SET NOCOUNT ON;

    /*===================================================================
      0. SHARED TABLES - verbatim shapes (sql/34, sql/37)
    ===================================================================*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    CREATE TABLE #inst (
        ComplianceInstanceID BIGINT NOT NULL PRIMARY KEY, BranchID INT NOT NULL, CategoryId INT NULL,
        ActID INT NULL, ComplianceID BIGINT NOT NULL, DepartmentID BIGINT NULL, Imprisonment BIT NOT NULL,
        RiskType INT NULL, RiskClass NVARCHAR(200) NULL);

    IF OBJECT_ID('tempdb..#sched') IS NOT NULL DROP TABLE #sched;
    CREATE TABLE #sched (
        ComplianceScheduleOnID BIGINT NOT NULL PRIMARY KEY, ComplianceInstanceID BIGINT NOT NULL,
        BranchID INT NOT NULL, ScheduleOn DATETIME NOT NULL, WindowPart VARCHAR(14) NULL,
        PerformerID BIGINT NULL, PerformerSource VARCHAR(10) NOT NULL, ReviewerID BIGINT NULL,
        ReviewerSource VARCHAR(10) NOT NULL, NeverTouched BIT NOT NULL, Outcome VARCHAR(20) NOT NULL,
        IsOverdue BIT NOT NULL, DaysPastDue INT NULL, AgeBand VARCHAR(10) NULL);

    IF OBJECT_ID('tempdb..#mem') IS NOT NULL DROP TABLE #mem;
    CREATE TABLE #mem (MemberId BIGINT NOT NULL PRIMARY KEY, MemberLabel NVARCHAR(MAX) NULL);

    IF OBJECT_ID('tempdb..#smap') IS NOT NULL DROP TABLE #smap;
    CREATE TABLE #smap (ComplianceScheduleOnID BIGINT NOT NULL PRIMARY KEY, MemberId BIGINT NOT NULL);

    IF OBJECT_ID('tempdb..#mm') IS NOT NULL DROP TABLE #mm;
    CREATE TABLE #mm (
        MemberId BIGINT NOT NULL PRIMARY KEY,
        LmDue INT NOT NULL, LmOnTime INT NOT NULL, LmLate INT NOT NULL, LmOpen INT NOT NULL,
        TmDue INT NOT NULL, TmOpen INT NOT NULL,
        RmDue INT NOT NULL, RmLiab INT NOT NULL, RmNoOwner INT NOT NULL,
        OpenItems INT NOT NULL, OverdueItems INT NOT NULL, Overdue90Items INT NOT NULL,
        LiabOverdueItems INT NOT NULL, NeverTouchedOverdue INT NOT NULL, NoOwnerOpen INT NOT NULL,
        EligSlip BIT NOT NULL DEFAULT 0, FlagSlip BIT NOT NULL DEFAULT 0,
        EligLiab BIT NOT NULL DEFAULT 0, FlagLiab BIT NOT NULL DEFAULT 0,
        EligChron BIT NOT NULL DEFAULT 0, FlagChron BIT NOT NULL DEFAULT 0,
        EligConc BIT NOT NULL DEFAULT 0, FlagConc BIT NOT NULL DEFAULT 0);

    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) NOT NULL PRIMARY KEY, Eligible INT NOT NULL, Flagged INT NOT NULL,
        FlaggedPct DECIMAL(5,1) NULL, EmitMode VARCHAR(12) NULL, Note NVARCHAR(200) NULL);

    IF OBJECT_ID('tempdb..#cand') IS NOT NULL DROP TABLE #cand;
    CREATE TABLE #cand (
        Detector VARCHAR(40) NOT NULL, Priority TINYINT NOT NULL, SeverityTier TINYINT NOT NULL,
        RankInDetector INT NOT NULL, EntityKind VARCHAR(20) NOT NULL, EntityId BIGINT NULL,
        EntityLabel NVARCHAR(MAX) NULL, ContextKind VARCHAR(20) NULL, ContextLabel NVARCHAR(MAX) NULL,
        Metric VARCHAR(40) NOT NULL, MetricPct INT NULL, TenantPct INT NULL, ItemCount INT NULL,
        BaseCount INT NULL, EventDate DATE NULL, AsAtRequired BIT NOT NULL DEFAULT 0,
        ProblemCount INT NOT NULL, PopulationCount INT NOT NULL, DefaultSlot TINYINT NULL);

    /*  Examples for aggregate-mode patterns (grid #6). Shared shape - byte-identical in sql/36,
        38, 39, 40, 41; sql/37 fills it for the four shared detectors.                          */
    IF OBJECT_ID('tempdb..#eg') IS NOT NULL DROP TABLE #eg;
    CREATE TABLE #eg (
        Detector VARCHAR(40) NOT NULL, PatternFactKey VARCHAR(40) NOT NULL, ExampleRank INT NOT NULL,
        EntityKind VARCHAR(20) NOT NULL, EntityId BIGINT NULL, EntityLabel NVARCHAR(MAX) NOT NULL,
        ContextKind VARCHAR(20) NULL, ContextLabel NVARCHAR(MAX) NULL,
        ItemCount INT NULL, BaseCount INT NULL, UnitLabel NVARCHAR(200) NOT NULL);

    IF OBJECT_ID('tempdb..#facts') IS NOT NULL DROP TABLE #facts;
    CREATE TABLE #facts (
        FactKey VARCHAR(40) NOT NULL PRIMARY KEY, FactValue INT NOT NULL, DisplayLabel NVARCHAR(MAX) NOT NULL,
        Section VARCHAR(16) NOT NULL, DisplayOrder INT NOT NULL, WindowScope VARCHAR(6) NOT NULL,
        ImpactClass VARCHAR(24) NOT NULL, SeverityTier TINYINT NOT NULL, AsAtRequired BIT NOT NULL,
        HeadlineRank TINYINT NULL, IsHeadline BIT NOT NULL DEFAULT 0);

    EXEC dbo.usp_Insights_FreeMonthly_LoadFacts @UserID, @CustomerID, @CurrMonthStart, @AsOf;

    DECLARE @CurrStart DATETIME = CAST(@CurrMonthStart AS DATETIME);
    DECLARE @PrevStart DATETIME = DATEADD(MONTH, -1, @CurrStart);
    DECLARE @NextStart DATETIME = DATEADD(MONTH,  1, @CurrStart);

    /*===================================================================
      1. MEMBERS = laws applicable in scope
    ===================================================================*/
    INSERT #mem (MemberId, MemberLabel)
    SELECT a.ActID, act.Name
    FROM (SELECT DISTINCT ActID FROM #inst WHERE ActID IS NOT NULL) a
    JOIN Act act ON act.ID = a.ActID;

    INSERT #smap (ComplianceScheduleOnID, MemberId)
    SELECT s.ComplianceScheduleOnID, i.ActID
    FROM #sched s
    JOIN #inst  i ON i.ComplianceInstanceID = s.ComplianceInstanceID
    WHERE i.ActID IS NOT NULL;

    /*  [PERF] sql/37 joins and groups #smap by MemberId three times; the PK is
        on the schedule id. Index the column it is actually read by.         */
    CREATE NONCLUSTERED INDEX IX_smap_member ON #smap (MemberId);

    /*  Structural: tvfInsightsScopedInstances inner-joins Act, so every
        schedule must map to a law. An unmapped one means the scope source
        changed underneath this proc.                                       */
    IF (SELECT COUNT(*) FROM #smap) <> (SELECT COUNT(*) FROM #sched)
        THROW 51291, N'FREE MONTHLY ACT RECONCILIATION FAILED - a loaded schedule does not map to a law in scope. Refusing to publish.', 1;

    /*  Sec.4a: rows sum exactly - every obligation has exactly one law.     */
    IF (SELECT COUNT(*) FROM #inst i JOIN #mem m ON m.MemberId = i.ActID) <> (SELECT COUNT(*) FROM #inst)
        THROW 51292, N'FREE MONTHLY ACT RECONCILIATION FAILED - per-law obligations do not sum to the scoped total. Refusing to publish.', 1;

    /*===================================================================
      2. SHARED DETECTORS (sql/37)
    ===================================================================*/
    EXEC dbo.usp_Insights_FreeMonthly_MemberDetectors
         @EntityKind = 'act', @EntityPlural = N'laws',
         @RelativeRiskFactor = @RelativeRiskFactor, @ConcentrationFactor = @ConcentrationFactor,
         @MemberFloor = @MemberFloor, @MaxPerDetector = @MaxPerDetector, @MaxExamples = @MaxExamples;

    /*===================================================================
      3. multi_location_pattern
    ===================================================================*/
    IF OBJECT_ID('tempdb..#actLoc') IS NOT NULL DROP TABLE #actLoc;
    CREATE TABLE #actLoc (
        ActID          BIGINT NOT NULL PRIMARY KEY,
        Locations      INT    NOT NULL,
        LocsOverdue    INT    NOT NULL,
        IsEligible     BIT    NOT NULL DEFAULT 0,
        IsFlagged      BIT    NOT NULL DEFAULT 0
    );

    INSERT #actLoc (ActID, Locations, LocsOverdue)
    SELECT a.ActID, a.Locations, ISNULL(o.LocsOverdue, 0)
    FROM (SELECT ActID, COUNT(DISTINCT BranchID) AS Locations
          FROM #inst WHERE ActID IS NOT NULL GROUP BY ActID) a
    LEFT JOIN (SELECT i.ActID, COUNT(DISTINCT s.BranchID) AS LocsOverdue
               FROM #sched s
               JOIN #inst  i ON i.ComplianceInstanceID = s.ComplianceInstanceID
               WHERE s.IsOverdue = 1
               GROUP BY i.ActID) o ON o.ActID = a.ActID;

    UPDATE #actLoc SET IsEligible = 1 WHERE Locations >= 2;

    /*  Baseline = the AVERAGE LAW's share of its locations with overdue work
        (unweighted mean of per-law shares). Same basis as each law's own
        MetricPct, so "X% of its locations, against Y% for the average law" is
        a like-for-like sentence. A pair-weighted mean would let big laws set
        the bar and could not be explained in the email.                     */
    DECLARE @mlE INT = (SELECT COUNT(*) FROM #actLoc WHERE IsEligible = 1);
    DECLARE @mlBase DECIMAL(9,4) = ISNULL((SELECT AVG(1.0 * LocsOverdue / Locations)
                                           FROM #actLoc WHERE IsEligible = 1), 0);

    IF @mlE >= 2 AND @mlBase > 0
        UPDATE #actLoc
           SET IsFlagged = 1
         WHERE IsEligible = 1
           AND LocsOverdue >= 2
           AND 1.0 * LocsOverdue / Locations >= @mlBase * @RelativeRiskFactor;

    DECLARE @mlF INT = (SELECT COUNT(*) FROM #actLoc WHERE IsFlagged = 1);

    INSERT #detector (Detector, Eligible, Flagged, Note)
    VALUES ('multi_location_pattern', @mlE, @mlF,
            CASE WHEN @mlE < 2    THEN N'suppressed - fewer than 2 laws apply at more than one location'
                 WHEN @mlBase = 0 THEN N'nothing to compare - no law is overdue at any location'
                 ELSE NULL END);

    UPDATE #detector
       SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END,
           EmitMode   = CASE WHEN Flagged = 0 THEN 'none'
                             WHEN Flagged >= 2 AND 100.0 * Flagged / NULLIF(Eligible, 0) > 20.0 THEN 'aggregate'
                             ELSE 'individual' END
     WHERE Detector = 'multi_location_pattern';

    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51294, N'DETECTOR CONTRACT VIOLATED - a free monthly act detector flagged more laws than it declared eligible. Refusing to emit.', 1;

    DECLARE @mlMode VARCHAR(12) = (SELECT EmitMode FROM #detector WHERE Detector = 'multi_location_pattern');

    IF @mlMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, MetricPct, TenantPct, ItemCount, BaseCount, ProblemCount, PopulationCount)
        SELECT TOP (@MaxPerDetector) 'multi_location_pattern', 3, 2,
               ROW_NUMBER() OVER (ORDER BY al.LocsOverdue DESC, al.ActID),
               'act', al.ActID, m.MemberLabel,
               'locations_overdue_on_this_law',
               CAST(FLOOR(100.0 * al.LocsOverdue / al.Locations) AS INT),
               CAST(FLOOR(100.0 * @mlBase) AS INT),
               al.LocsOverdue, al.Locations, @mlF, @mlE
        FROM #actLoc al JOIN #mem m ON m.MemberId = al.ActID
        WHERE al.IsFlagged = 1
        ORDER BY al.LocsOverdue DESC, al.ActID;

    /*===================================================================
      4. FACTS
    ===================================================================*/
    DECLARE @lawsIn      INT = (SELECT COUNT(*) FROM #mem);
    DECLARE @lawsOverdue INT = (SELECT COUNT(*) FROM #mm WHERE OverdueItems >= 1);
    DECLARE @tOverdue    INT = (SELECT COUNT(*) FROM #sched WHERE IsOverdue = 1);
    DECLARE @top3        INT = (SELECT ISNULL(SUM(t.OverdueItems), 0)
                                FROM (SELECT TOP 3 x.OverdueItems FROM #mm x ORDER BY x.OverdueItems DESC) t);

    INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
    VALUES
     ('law_in_scope',                @lawsIn,     N'laws apply to the obligations in your scope',                                     'laws', 100, 'ctx',   'volume',                 5, 0, NULL),
     ('law_with_liability_oblig',    (SELECT COUNT(DISTINCT ActID) FROM #inst WHERE Imprisonment = 1),
                                                  N'of those include obligations that carry personal criminal liability',             'laws', 110, 'ctx',   'personal_liability',     4, 0, NULL),
     ('law_with_overdue',            @lawsOverdue,N'laws have at least one overdue item',                                             'laws', 120, 'stock', 'operational_continuity', 4, 0, NULL),
     ('law_with_liability_overdue',  (SELECT COUNT(*) FROM #mm WHERE LiabOverdueItems >= 1),
                                                  N'laws have overdue items that carry personal criminal liability',                  'laws', 130, 'stock', 'personal_liability',     1, 0, NULL),
     ('law_with_over_90_days',       (SELECT COUNT(*) FROM #mm WHERE Overdue90Items >= 1),
                                                  N'laws have items overdue for more than 90 days',                                   'laws', 140, 'stock', 'operational_continuity', 2, 0, NULL),
     ('law_with_last_month_open',    (SELECT COUNT(*) FROM #mm WHERE LmOpen >= 1),
                                                  N'laws still have items open from last month',                                     'laws', 150, 'prev',  'operational_continuity', 2, 1, NULL),
     ('law_overdue_at_2plus_locs',   (SELECT COUNT(*) FROM #actLoc WHERE LocsOverdue >= 2),
                                                  N'laws are overdue at two or more locations at once',                               'laws', 160, 'stock', 'operational_continuity', 2, 0, NULL);

    IF @lawsOverdue >= 4 AND @tOverdue > 0
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('law_top3_overdue_share_pct', CAST(FLOOR(100.0 * @top3 / @tOverdue) AS INT),
                N'per cent of all overdue items fall under the 3 laws holding the most (rounded down)',
                'laws', 170, 'stock', 'operational_continuity', 3, 0, 8);

    IF @mlMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_multi_location_pattern',    @mlF, N'laws are overdue across an unusually large share of the locations they apply to - a systemic pattern', 'patterns', 740, 'stock', 'operational_continuity', 2, 0, 3),
               ('pat_multi_location_pattern_of', @mlE, N'laws that apply at two or more locations were compared',                                             'patterns', 741, 'ctx',   'volume',                 5, 0, NULL);

    /*  EXAMPLES - the Acts overdue at the most locations. NOTE the unit: ItemCount and
        BaseCount count LOCATIONS, never obligations, and the UnitLabel says so.             */
    IF @mlMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples) 'multi_location_pattern', 'pat_multi_location_pattern',
               ROW_NUMBER() OVER (ORDER BY al.LocsOverdue DESC, al.ActID),
               'act', al.ActID, m.MemberLabel, al.LocsOverdue, al.Locations,
               N'of the locations this Act applies to have it overdue (locations, not obligations)'
        FROM #actLoc al JOIN #mem m ON m.MemberId = al.ActID
        WHERE al.IsFlagged = 1 AND m.MemberLabel IS NOT NULL
        ORDER BY al.LocsOverdue DESC, al.ActID;

    /*===================================================================
      5. DEFAULT SLOTS + HEADLINE (identical block in every dimension slot)
    ===================================================================*/
    ;WITH s1 AS (SELECT TOP 1 * FROM #cand WHERE RankInDetector = 1 ORDER BY Priority, Detector)
    UPDATE s1 SET DefaultSlot = 1;

    ;WITH s2 AS (
        SELECT TOP 1 c.* FROM #cand c
        WHERE c.RankInDetector = 1 AND c.DefaultSlot IS NULL
          AND NOT EXISTS (SELECT 1 FROM #cand f
                          WHERE f.DefaultSlot = 1 AND f.EntityKind = c.EntityKind AND f.EntityId = c.EntityId)
        ORDER BY c.Priority, c.Detector)
    UPDATE s2 SET DefaultSlot = 2;

    DECLARE @bestCand INT = (SELECT MIN(Priority) FROM #cand WHERE DefaultSlot = 1);
    DECLARE @bestFact INT = (SELECT MIN(HeadlineRank) FROM #facts WHERE HeadlineRank IS NOT NULL AND FactValue > 0);
    DECLARE @headlineSource VARCHAR(10) =
        CASE WHEN @bestCand IS NOT NULL AND (@bestFact IS NULL OR @bestCand <= @bestFact) THEN 'candidate' ELSE 'fact' END;

    IF @headlineSource = 'fact'
    BEGIN
        ;WITH pick AS (SELECT TOP 1 FactKey FROM #facts
                       WHERE HeadlineRank IS NOT NULL AND FactValue > 0
                       ORDER BY HeadlineRank, DisplayOrder)
        UPDATE f SET IsHeadline = 1 FROM #facts f JOIN pick p ON p.FactKey = f.FactKey;

        IF NOT EXISTS (SELECT 1 FROM #facts WHERE IsHeadline = 1)
            UPDATE #facts SET IsHeadline = 1 WHERE FactKey = 't_rm_due';
    END

    /*  EXAMPLES CONTRACT (2026-09-23 design, Sec.2.7). An example exists only in aggregate
        mode, beside the pattern fact it illustrates. Codes from this file's own block.       */
    IF EXISTS (SELECT 1 FROM #eg e JOIN #cand c ON c.Detector = e.Detector)
        THROW 51293, N'EXAMPLES CONTRACT VIOLATED - a free monthly act detector emitted both named candidates and examples. Examples exist only in aggregate mode. Refusing to emit.', 1;
    IF EXISTS (SELECT 1 FROM #eg e LEFT JOIN #facts f ON f.FactKey = e.PatternFactKey WHERE f.FactKey IS NULL)
        THROW 51295, N'EXAMPLES CONTRACT VIOLATED - a free monthly act example refers to a pattern fact that was not emitted. Refusing to emit.', 1;

    /*===================================================================
      6. EMIT
    ===================================================================*/
    SELECT 'control_totals'                           AS ResultSet,
           @CustomerID                                AS CustomerID,
           @UserID                                    AS UserID,
           'act'                                      AS Slot,
           CAST(@PrevStart AS DATE)                   AS PrevMonthStart,
           CAST(DATEADD(DAY, -1, @CurrStart) AS DATE) AS PrevMonthEnd,
           CAST(@CurrStart AS DATE)                   AS CurrMonthStart,
           CAST(DATEADD(DAY, -1, @NextStart) AS DATE) AS CurrMonthEnd,
           @AsOf                                      AS AsOf,
           (SELECT COUNT(*) FROM #inst)               AS ScopedInstances,
           (SELECT COUNT(*) FROM #inst)               AS SumOfRows,   /* each obligation has exactly one law - checked by 51292 */
           (SELECT COUNT(*) FROM #sched)              AS SchedulesLoaded,
           @lawsIn                                    AS MembersInScope,
           CAST(1 AS BIT)                             AS Reconciled,
           @headlineSource                            AS HeadlineSource;

    SELECT 'facts' AS ResultSet, FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope,
           ImpactClass, SeverityTier, AsAtRequired, IsHeadline
    FROM #facts ORDER BY DisplayOrder;

    SELECT 'detector_policy' AS ResultSet, Detector, Eligible, Flagged, FlaggedPct, EmitMode, Note
    FROM #detector;

    SELECT 'candidates' AS ResultSet, DefaultSlot, Detector, Priority, SeverityTier, RankInDetector,
           EntityKind, EntityId, EntityLabel, ContextKind, ContextLabel, Metric, MetricPct, TenantPct,
           ItemCount, BaseCount, EventDate, AsAtRequired, ProblemCount, PopulationCount,
           ProblemCount - 1 AS ResidualCount
    FROM #cand
    ORDER BY CASE WHEN DefaultSlot IS NULL THEN 9 ELSE DefaultSlot END, Priority, Detector, RankInDetector;

    SELECT 'data_quality' AS ResultSet, Code, ItemCount, Detail
    FROM (VALUES
        ('laws_single_location', (SELECT COUNT(*) FROM #actLoc WHERE Locations < 2),
         N'Laws that apply at only one location. They cannot show a multi-location pattern and are not compared on it.'),
        ('open_items_no_owner_anywhere', (SELECT COUNT(*) FROM #sched WHERE Outcome = 'open' AND PerformerSource = 'none'),
         N'Open items with no performer anywhere.'),
        ('prev_month_not_settled', (SELECT ISNULL(SUM(LmDue), 0) FROM #mm),
         N'Last-month figures are as at the run date; late closures can still arrive.')
    ) AS dq(Code, ItemCount, Detail);

    /*  Grid #6 - APPENDED LAST, never before data_quality (a reader not yet updated would
        map example rows onto its data_quality shape). Empty is normal.                     */
    SELECT 'examples' AS ResultSet, Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel,
           ContextKind, ContextLabel, ItemCount, BaseCount, UnitLabel
    FROM #eg
    ORDER BY PatternFactKey, ExampleRank;
END
GO
PRINT 'usp_Insights_FreeMonthly_Act restored.';
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Location', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Location;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_Location
    @UserID              INT,
    @CustomerID          INT,
    @CurrMonthStart      DATE,
    @AsOf                DATETIME,
    @RelativeRiskFactor  DECIMAL(4,2) = 1.50,
    @ConcentrationFactor DECIMAL(4,2) = 2.00,
    @MemberFloor         INT          = 5,
    @SpofFloor           INT          = 5,     -- min open items for a site to be a single-point-of-failure candidate
    @MaxPerDetector      INT          = 5,
    @MaxExamples         INT          = 3      -- examples named inside an aggregate-mode pattern (2026-09-23)
AS
BEGIN
    SET NOCOUNT ON;

    /*===================================================================
      0. SHARED TABLES - verbatim shapes (sql/34, sql/37)
    ===================================================================*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    CREATE TABLE #inst (
        ComplianceInstanceID BIGINT NOT NULL PRIMARY KEY, BranchID INT NOT NULL, CategoryId INT NULL,
        ActID INT NULL, ComplianceID BIGINT NOT NULL, DepartmentID BIGINT NULL, Imprisonment BIT NOT NULL,
        RiskType INT NULL, RiskClass NVARCHAR(200) NULL);

    IF OBJECT_ID('tempdb..#sched') IS NOT NULL DROP TABLE #sched;
    CREATE TABLE #sched (
        ComplianceScheduleOnID BIGINT NOT NULL PRIMARY KEY, ComplianceInstanceID BIGINT NOT NULL,
        BranchID INT NOT NULL, ScheduleOn DATETIME NOT NULL, WindowPart VARCHAR(14) NULL,
        PerformerID BIGINT NULL, PerformerSource VARCHAR(10) NOT NULL, ReviewerID BIGINT NULL,
        ReviewerSource VARCHAR(10) NOT NULL, NeverTouched BIT NOT NULL, Outcome VARCHAR(20) NOT NULL,
        IsOverdue BIT NOT NULL, DaysPastDue INT NULL, AgeBand VARCHAR(10) NULL);

    IF OBJECT_ID('tempdb..#mem') IS NOT NULL DROP TABLE #mem;
    CREATE TABLE #mem (MemberId BIGINT NOT NULL PRIMARY KEY, MemberLabel NVARCHAR(MAX) NULL);

    IF OBJECT_ID('tempdb..#smap') IS NOT NULL DROP TABLE #smap;
    CREATE TABLE #smap (ComplianceScheduleOnID BIGINT NOT NULL PRIMARY KEY, MemberId BIGINT NOT NULL);

    IF OBJECT_ID('tempdb..#mm') IS NOT NULL DROP TABLE #mm;
    CREATE TABLE #mm (
        MemberId BIGINT NOT NULL PRIMARY KEY,
        LmDue INT NOT NULL, LmOnTime INT NOT NULL, LmLate INT NOT NULL, LmOpen INT NOT NULL,
        TmDue INT NOT NULL, TmOpen INT NOT NULL,
        RmDue INT NOT NULL, RmLiab INT NOT NULL, RmNoOwner INT NOT NULL,
        OpenItems INT NOT NULL, OverdueItems INT NOT NULL, Overdue90Items INT NOT NULL,
        LiabOverdueItems INT NOT NULL, NeverTouchedOverdue INT NOT NULL, NoOwnerOpen INT NOT NULL,
        EligSlip BIT NOT NULL DEFAULT 0, FlagSlip BIT NOT NULL DEFAULT 0,
        EligLiab BIT NOT NULL DEFAULT 0, FlagLiab BIT NOT NULL DEFAULT 0,
        EligChron BIT NOT NULL DEFAULT 0, FlagChron BIT NOT NULL DEFAULT 0,
        EligConc BIT NOT NULL DEFAULT 0, FlagConc BIT NOT NULL DEFAULT 0);

    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) NOT NULL PRIMARY KEY, Eligible INT NOT NULL, Flagged INT NOT NULL,
        FlaggedPct DECIMAL(5,1) NULL, EmitMode VARCHAR(12) NULL, Note NVARCHAR(200) NULL);

    IF OBJECT_ID('tempdb..#cand') IS NOT NULL DROP TABLE #cand;
    CREATE TABLE #cand (
        Detector VARCHAR(40) NOT NULL, Priority TINYINT NOT NULL, SeverityTier TINYINT NOT NULL,
        RankInDetector INT NOT NULL, EntityKind VARCHAR(20) NOT NULL, EntityId BIGINT NULL,
        EntityLabel NVARCHAR(MAX) NULL, ContextKind VARCHAR(20) NULL, ContextLabel NVARCHAR(MAX) NULL,
        Metric VARCHAR(40) NOT NULL, MetricPct INT NULL, TenantPct INT NULL, ItemCount INT NULL,
        BaseCount INT NULL, EventDate DATE NULL, AsAtRequired BIT NOT NULL DEFAULT 0,
        ProblemCount INT NOT NULL, PopulationCount INT NOT NULL, DefaultSlot TINYINT NULL);

    /*  Examples for aggregate-mode patterns (grid #6). Shared shape - byte-identical in sql/36,
        38, 39, 40, 41; sql/37 fills it for the four shared detectors.                          */
    IF OBJECT_ID('tempdb..#eg') IS NOT NULL DROP TABLE #eg;
    CREATE TABLE #eg (
        Detector VARCHAR(40) NOT NULL, PatternFactKey VARCHAR(40) NOT NULL, ExampleRank INT NOT NULL,
        EntityKind VARCHAR(20) NOT NULL, EntityId BIGINT NULL, EntityLabel NVARCHAR(MAX) NOT NULL,
        ContextKind VARCHAR(20) NULL, ContextLabel NVARCHAR(MAX) NULL,
        ItemCount INT NULL, BaseCount INT NULL, UnitLabel NVARCHAR(200) NOT NULL);

    IF OBJECT_ID('tempdb..#facts') IS NOT NULL DROP TABLE #facts;
    CREATE TABLE #facts (
        FactKey VARCHAR(40) NOT NULL PRIMARY KEY, FactValue INT NOT NULL, DisplayLabel NVARCHAR(MAX) NOT NULL,
        Section VARCHAR(16) NOT NULL, DisplayOrder INT NOT NULL, WindowScope VARCHAR(6) NOT NULL,
        ImpactClass VARCHAR(24) NOT NULL, SeverityTier TINYINT NOT NULL, AsAtRequired BIT NOT NULL,
        HeadlineRank TINYINT NULL, IsHeadline BIT NOT NULL DEFAULT 0);

    EXEC dbo.usp_Insights_FreeMonthly_LoadFacts @UserID, @CustomerID, @CurrMonthStart, @AsOf;

    DECLARE @CurrStart DATETIME = CAST(@CurrMonthStart AS DATETIME);
    DECLARE @PrevStart DATETIME = DATEADD(MONTH, -1, @CurrStart);
    DECLARE @NextStart DATETIME = DATEADD(MONTH,  1, @CurrStart);

    /*===================================================================
      1. MEMBERS = the locations the RegTrack dashboard lists (2026-09-29):
         a branch counts only when it holds at least one counted obligation
         (SP_GetEntitySummary, MGMT path - see sql/34's parity filters). An
         authorised branch with nothing counted is NOT a member, so members
         equal the dashboard's location list. Consequence: ghost_location
         (below) finds nothing and stays silent - its code is kept.
    ===================================================================*/
    INSERT #mem (MemberId, MemberLabel)
    SELECT b.BranchID, cb.Name
    FROM (SELECT DISTINCT i.BranchID FROM #inst i) b
    JOIN CustomerBranch cb ON cb.ID = b.BranchID;

    INSERT #smap (ComplianceScheduleOnID, MemberId)
    SELECT s.ComplianceScheduleOnID, s.BranchID
    FROM #sched s;

    /*  [PERF] sql/37 joins and groups #smap by MemberId three times; the PK is
        on the schedule id. Index the column it is actually read by.         */
    CREATE NONCLUSTERED INDEX IX_smap_member ON #smap (MemberId);

    /*  Structural: every scoped obligation's branch is an authorised branch.
        If not, #inst and the branch list disagree about scope.             */
    IF EXISTS (SELECT 1 FROM #inst i LEFT JOIN #mem m ON m.MemberId = i.BranchID WHERE m.MemberId IS NULL)
        THROW 51281, N'FREE MONTHLY LOCATION RECONCILIATION FAILED - a scoped obligation sits on a branch outside the authorised branch list. Scope sources disagree. Refusing to publish.', 1;

    /*===================================================================
      2. SHARED DETECTORS (sql/37)
    ===================================================================*/
    EXEC dbo.usp_Insights_FreeMonthly_MemberDetectors
         @EntityKind = 'location', @EntityPlural = N'locations',
         @RelativeRiskFactor = @RelativeRiskFactor, @ConcentrationFactor = @ConcentrationFactor,
         @MemberFloor = @MemberFloor, @MaxPerDetector = @MaxPerDetector, @MaxExamples = @MaxExamples;

    /*===================================================================
      3. LOCATION-ONLY DETECTORS
    ===================================================================*/
    IF OBJECT_ID('tempdb..#site') IS NOT NULL DROP TABLE #site;
    CREATE TABLE #site (
        BranchID        BIGINT NOT NULL PRIMARY KEY,
        Obligations     INT    NOT NULL,
        OpenItems       INT    NOT NULL,
        OpenPerformers  INT    NOT NULL,
        OpenUnowned     INT    NOT NULL,
        IsSpofEligible  BIT    NOT NULL DEFAULT 0,
        IsSpof          BIT    NOT NULL DEFAULT 0,
        IsGhost         BIT    NOT NULL DEFAULT 0
    );

    INSERT #site (BranchID, Obligations, OpenItems, OpenPerformers, OpenUnowned)
    SELECT m.MemberId,
           ISNULL(o.Obligations, 0),
           ISNULL(w.OpenItems, 0),
           ISNULL(w.OpenPerformers, 0),
           ISNULL(w.OpenUnowned, 0)
    FROM #mem m
    LEFT JOIN (SELECT BranchID, COUNT(*) AS Obligations FROM #inst GROUP BY BranchID) o
           ON o.BranchID = m.MemberId
    LEFT JOIN (SELECT s.BranchID, COUNT(*) AS OpenItems,
                      COUNT(DISTINCT s.PerformerID) AS OpenPerformers,  -- NULLs not counted
                      SUM(CASE WHEN s.PerformerID IS NULL THEN 1 ELSE 0 END) AS OpenUnowned
               FROM #sched s WHERE s.Outcome = 'open'
               GROUP BY s.BranchID) w
           ON w.BranchID = m.MemberId;

    /*  SumOfRows must equal ScopedInstances (Sec.4a: rows sum exactly). Checked
        BEFORE any result set is emitted - a THROW after the first grid is
        invisible to a caller that reads only grid 1 (CLAUDE.md Sec.3).      */
    IF (SELECT ISNULL(SUM(Obligations), 0) FROM #site) <> (SELECT COUNT(*) FROM #inst)
        THROW 51282, N'FREE MONTHLY LOCATION RECONCILIATION FAILED - per-location obligations do not sum to the scoped total. Refusing to publish.', 1;

    UPDATE #site SET IsSpofEligible = 1 WHERE OpenItems >= @SpofFloor;
    /*  [GUARD] COUNT(DISTINCT) skips NULL owners: a site with 39 unowned items
        and 1 owned one would read as "one performer". Its real problem is "no
        owner" (a separate fact), so SPOF requires EVERY open item to be owned. */
    UPDATE #site SET IsSpof  = 1 WHERE IsSpofEligible = 1 AND OpenPerformers = 1 AND OpenUnowned = 0;
    UPDATE #site SET IsGhost = 1 WHERE Obligations = 0;

    DECLARE @spofE INT  = (SELECT COUNT(*) FROM #site WHERE IsSpofEligible = 1);
    DECLARE @spofF INT  = (SELECT COUNT(*) FROM #site WHERE IsSpof = 1);
    DECLARE @ghostE INT = (SELECT COUNT(*) FROM #site);
    DECLARE @ghostF INT = (SELECT COUNT(*) FROM #site WHERE IsGhost = 1);

    INSERT #detector (Detector, Eligible, Flagged, Note)
    VALUES ('single_point_of_failure', @spofE, @spofF,
            CASE WHEN @spofE = 0 THEN N'nothing to assess - no location holds enough open work' END),
           ('ghost_location', @ghostE, @ghostF, NULL);

    UPDATE #detector
       SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END,
           EmitMode   = CASE WHEN Flagged = 0 THEN 'none'
                             WHEN Flagged >= 2 AND 100.0 * Flagged / NULLIF(Eligible, 0) > 20.0 THEN 'aggregate'
                             ELSE 'individual' END
     WHERE Detector IN ('single_point_of_failure', 'ghost_location');

    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51284, N'DETECTOR CONTRACT VIOLATED - a free monthly location detector flagged more locations than it declared eligible. Refusing to emit.', 1;

    DECLARE @spofMode  VARCHAR(12) = (SELECT EmitMode FROM #detector WHERE Detector = 'single_point_of_failure');
    DECLARE @ghostMode VARCHAR(12) = (SELECT EmitMode FROM #detector WHERE Detector = 'ghost_location');

    IF @spofMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, ItemCount, ProblemCount, PopulationCount)
        SELECT TOP (@MaxPerDetector) 'single_point_of_failure', 6, 2,
               ROW_NUMBER() OVER (ORDER BY st.OpenItems DESC, st.BranchID),
               'location', st.BranchID, m.MemberLabel,
               'open_items_with_one_performer', st.OpenItems, @spofF, @spofE
        FROM #site st JOIN #mem m ON m.MemberId = st.BranchID
        WHERE st.IsSpof = 1
        ORDER BY st.OpenItems DESC, st.BranchID;

    IF @ghostMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, ProblemCount, PopulationCount)
        SELECT TOP (@MaxPerDetector) 'ghost_location', 7, 3,
               ROW_NUMBER() OVER (ORDER BY m.MemberLabel, st.BranchID),
               'location', st.BranchID, m.MemberLabel,
               'no_obligations_configured', @ghostF, @ghostE
        FROM #site st JOIN #mem m ON m.MemberId = st.BranchID
        WHERE st.IsGhost = 1
        ORDER BY m.MemberLabel, st.BranchID;

    /*===================================================================
      4. FACTS
    ===================================================================*/
    DECLARE @locIn       INT = (SELECT COUNT(*) FROM #site);
    DECLARE @locWithObl  INT = (SELECT COUNT(*) FROM #site WHERE Obligations > 0);
    DECLARE @locOverdue  INT = (SELECT COUNT(*) FROM #mm WHERE OverdueItems >= 1);
    DECLARE @tOverdue    INT = (SELECT COUNT(*) FROM #sched WHERE IsOverdue = 1);
    DECLARE @top3        INT = (SELECT ISNULL(SUM(t.OverdueItems), 0)
                                FROM (SELECT TOP 3 x.OverdueItems FROM #mm x ORDER BY x.OverdueItems DESC) t);

    INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
    VALUES
     ('loc_in_scope',                @locIn,      N'locations in your scope',                                                         'locations', 100, 'ctx',   'volume',                 5, 0, NULL),
     ('loc_with_obligations',        @locWithObl, N'of those have obligations configured',                                            'locations', 110, 'ctx',   'volume',                 5, 0, NULL),
     ('loc_without_obligations',     @ghostF,     N'locations in your scope have no obligations configured at all',                   'locations', 120, 'ctx',   'operational_continuity', 3, 0, NULL),
     ('loc_with_overdue',            @locOverdue, N'locations have at least one overdue item',                                        'locations', 130, 'stock', 'operational_continuity', 4, 0, NULL),
     ('loc_with_liability_overdue',  (SELECT COUNT(*) FROM #mm WHERE LiabOverdueItems >= 1),
                                                  N'locations have overdue items that carry personal criminal liability',             'locations', 140, 'stock', 'personal_liability',     1, 0, NULL),
     ('loc_with_over_90_days',       (SELECT COUNT(*) FROM #mm WHERE Overdue90Items >= 1),
                                                  N'locations have items overdue for more than 90 days',                              'locations', 150, 'stock', 'operational_continuity', 2, 0, NULL),
     ('loc_with_last_month_open',    (SELECT COUNT(*) FROM #mm WHERE LmOpen >= 1),
                                                  N'locations still have items open from last month',                                'locations', 160, 'prev',  'operational_continuity', 2, 1, NULL),
     ('loc_single_performer',        @spofF,      N'locations where every open item sits with one person',                            'locations', 170, 'stock', 'operational_continuity', 2, 0, NULL);

    IF @locOverdue >= 4 AND @tOverdue > 0
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('loc_top3_overdue_share_pct', CAST(FLOOR(100.0 * @top3 / @tOverdue) AS INT),
                N'per cent of all overdue items sit at the 3 locations holding the most (rounded down)',
                'locations', 180, 'stock', 'operational_continuity', 3, 0, 8);

    /*  [2026-09-23] Each pattern now carries its _of partner - the population it was measured
        against. SPOF's population is the sites holding enough open work (@SpofFloor), NOT every
        site in scope: without the partner a model reaching for a denominator took loc_in_scope
        and produced "N of 24" - both figures real, the fraction nobody's.                    */
    IF @spofMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_single_point_of_failure',    @spofF, N'locations depend on a single person for all their open work - a pattern across your scope', 'patterns', 740, 'stock', 'operational_continuity', 2, 0, 6),
               ('pat_single_point_of_failure_of', @spofE, N'locations holding enough open work to be assessed were compared',                          'patterns', 741, 'ctx',   'volume',                 5, 0, NULL);

    IF @ghostMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_ghost_location',    @ghostF, N'locations in your scope have no obligations configured - a structural pattern, likely a wider location master', 'patterns', 750, 'ctx', 'operational_continuity', 3, 0, 7),
               ('pat_ghost_location_of', @ghostE, N'locations in your scope were checked for configured obligations',                                          'patterns', 751, 'ctx', 'volume',                 5, 0, NULL);

    /*  EXAMPLES - single_point_of_failure only. ghost_location NEVER: its members hold zero
        obligations, so its order is alphabetical, and "including A, B and C" would present an
        alphabetical accident as significance. The count stands on its own.                    */
    IF @spofMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples) 'single_point_of_failure', 'pat_single_point_of_failure',
               ROW_NUMBER() OVER (ORDER BY st.OpenItems DESC, st.BranchID),
               'location', st.BranchID, m.MemberLabel, st.OpenItems, NULL,
               N'open obligations at this location are all assigned to one person'
        FROM #site st JOIN #mem m ON m.MemberId = st.BranchID
        WHERE st.IsSpof = 1 AND m.MemberLabel IS NOT NULL
        ORDER BY st.OpenItems DESC, st.BranchID;

    /*===================================================================
      5. DEFAULT SLOTS + HEADLINE (identical block in every dimension slot)
    ===================================================================*/
    ;WITH s1 AS (SELECT TOP 1 * FROM #cand WHERE RankInDetector = 1 ORDER BY Priority, Detector)
    UPDATE s1 SET DefaultSlot = 1;

    ;WITH s2 AS (
        SELECT TOP 1 c.* FROM #cand c
        WHERE c.RankInDetector = 1 AND c.DefaultSlot IS NULL
          AND NOT EXISTS (SELECT 1 FROM #cand f
                          WHERE f.DefaultSlot = 1 AND f.EntityKind = c.EntityKind AND f.EntityId = c.EntityId)
        ORDER BY c.Priority, c.Detector)
    UPDATE s2 SET DefaultSlot = 2;

    DECLARE @bestCand INT = (SELECT MIN(Priority) FROM #cand WHERE DefaultSlot = 1);
    DECLARE @bestFact INT = (SELECT MIN(HeadlineRank) FROM #facts WHERE HeadlineRank IS NOT NULL AND FactValue > 0);
    DECLARE @headlineSource VARCHAR(10) =
        CASE WHEN @bestCand IS NOT NULL AND (@bestFact IS NULL OR @bestCand <= @bestFact) THEN 'candidate' ELSE 'fact' END;

    IF @headlineSource = 'fact'
    BEGIN
        ;WITH pick AS (SELECT TOP 1 FactKey FROM #facts
                       WHERE HeadlineRank IS NOT NULL AND FactValue > 0
                       ORDER BY HeadlineRank, DisplayOrder)
        UPDATE f SET IsHeadline = 1 FROM #facts f JOIN pick p ON p.FactKey = f.FactKey;

        IF NOT EXISTS (SELECT 1 FROM #facts WHERE IsHeadline = 1)
            UPDATE #facts SET IsHeadline = 1 WHERE FactKey = 't_rm_due';
    END

    /*  EXAMPLES CONTRACT (2026-09-23 design, Sec.2.7). An example exists only in aggregate
        mode, beside the pattern fact it illustrates. Codes from this file's own block.       */
    IF EXISTS (SELECT 1 FROM #eg e JOIN #cand c ON c.Detector = e.Detector)
        THROW 51283, N'EXAMPLES CONTRACT VIOLATED - a free monthly location detector emitted both named candidates and examples. Examples exist only in aggregate mode. Refusing to emit.', 1;
    IF EXISTS (SELECT 1 FROM #eg e LEFT JOIN #facts f ON f.FactKey = e.PatternFactKey WHERE f.FactKey IS NULL)
        THROW 51285, N'EXAMPLES CONTRACT VIOLATED - a free monthly location example refers to a pattern fact that was not emitted. Refusing to emit.', 1;

    /*===================================================================
      6. EMIT
    ===================================================================*/
    SELECT 'control_totals'                           AS ResultSet,
           @CustomerID                                AS CustomerID,
           @UserID                                    AS UserID,
           'location'                                 AS Slot,
           CAST(@PrevStart AS DATE)                   AS PrevMonthStart,
           CAST(DATEADD(DAY, -1, @CurrStart) AS DATE) AS PrevMonthEnd,
           CAST(@CurrStart AS DATE)                   AS CurrMonthStart,
           CAST(DATEADD(DAY, -1, @NextStart) AS DATE) AS CurrMonthEnd,
           @AsOf                                      AS AsOf,
           (SELECT COUNT(*) FROM #inst)               AS ScopedInstances,
           (SELECT COUNT(*) FROM #sched)              AS SchedulesLoaded,
           @locIn                                     AS MembersInScope,
           @locWithObl                                AS MembersWithObligations,
           (SELECT ISNULL(SUM(Obligations), 0) FROM #site) AS SumOfRows,   /* = ScopedInstances: every obligation sits on exactly one branch */
           CAST(1 AS BIT)                             AS Reconciled,
           @headlineSource                            AS HeadlineSource;

    SELECT 'facts' AS ResultSet, FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope,
           ImpactClass, SeverityTier, AsAtRequired, IsHeadline
    FROM #facts ORDER BY DisplayOrder;

    SELECT 'detector_policy' AS ResultSet, Detector, Eligible, Flagged, FlaggedPct, EmitMode, Note
    FROM #detector;

    SELECT 'candidates' AS ResultSet, DefaultSlot, Detector, Priority, SeverityTier, RankInDetector,
           EntityKind, EntityId, EntityLabel, ContextKind, ContextLabel, Metric, MetricPct, TenantPct,
           ItemCount, BaseCount, EventDate, AsAtRequired, ProblemCount, PopulationCount,
           ProblemCount - 1 AS ResidualCount
    FROM #cand
    ORDER BY CASE WHEN DefaultSlot IS NULL THEN 9 ELSE DefaultSlot END, Priority, Detector, RankInDetector;

    SELECT 'data_quality' AS ResultSet, Code, ItemCount, Detail
    FROM (VALUES
        ('locations_without_obligations', @ghostF,
         N'Authorised locations with no obligation configured. Included as members so the coverage gap is visible, never dropped.'),
        ('open_items_no_owner_anywhere', (SELECT COUNT(*) FROM #sched WHERE Outcome = 'open' AND PerformerSource = 'none'),
         N'Open items with no performer anywhere. Not counted as single-point-of-failure; reported separately.'),
        ('prev_month_not_settled', (SELECT ISNULL(SUM(LmDue), 0) FROM #mm),
         N'Last-month figures are as at the run date; late closures can still arrive.')
    ) AS dq(Code, ItemCount, Detail);

    /*  Grid #6 - APPENDED LAST, never before data_quality (a reader not yet updated would
        map example rows onto its data_quality shape). Empty is normal.                     */
    SELECT 'examples' AS ResultSet, Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel,
           ContextKind, ContextLabel, ItemCount, BaseCount, UnitLabel
    FROM #eg
    ORDER BY PatternFactKey, ExampleRank;
END
GO
PRINT 'usp_Insights_FreeMonthly_Location restored.';
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Users', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Users;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_Users
    @UserID              INT,
    @CustomerID          INT,
    @CurrMonthStart      DATE,
    @AsOf                DATETIME,
    @AllowPersonNames    BIT          = 1,
    @RelativeRiskFactor  DECIMAL(4,2) = 1.50,
    @ConcentrationFactor DECIMAL(4,2) = 2.00,
    @MemberFloor         INT          = 5,
    @MaxPerDetector      INT          = 5,
    @MaxExamples         INT          = 3      -- examples named inside an aggregate-mode pattern (2026-09-23)
AS
BEGIN
    SET NOCOUNT ON;

    /*===================================================================
      0. SHARED TABLES - verbatim shapes (sql/34, sql/37)
    ===================================================================*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    CREATE TABLE #inst (
        ComplianceInstanceID BIGINT NOT NULL PRIMARY KEY, BranchID INT NOT NULL, CategoryId INT NULL,
        ActID INT NULL, ComplianceID BIGINT NOT NULL, DepartmentID BIGINT NULL, Imprisonment BIT NOT NULL,
        RiskType INT NULL, RiskClass NVARCHAR(200) NULL);

    IF OBJECT_ID('tempdb..#sched') IS NOT NULL DROP TABLE #sched;
    CREATE TABLE #sched (
        ComplianceScheduleOnID BIGINT NOT NULL PRIMARY KEY, ComplianceInstanceID BIGINT NOT NULL,
        BranchID INT NOT NULL, ScheduleOn DATETIME NOT NULL, WindowPart VARCHAR(14) NULL,
        PerformerID BIGINT NULL, PerformerSource VARCHAR(10) NOT NULL, ReviewerID BIGINT NULL,
        ReviewerSource VARCHAR(10) NOT NULL, NeverTouched BIT NOT NULL, Outcome VARCHAR(20) NOT NULL,
        IsOverdue BIT NOT NULL, DaysPastDue INT NULL, AgeBand VARCHAR(10) NULL);

    IF OBJECT_ID('tempdb..#mem') IS NOT NULL DROP TABLE #mem;
    CREATE TABLE #mem (MemberId BIGINT NOT NULL PRIMARY KEY, MemberLabel NVARCHAR(MAX) NULL);

    IF OBJECT_ID('tempdb..#smap') IS NOT NULL DROP TABLE #smap;
    CREATE TABLE #smap (ComplianceScheduleOnID BIGINT NOT NULL PRIMARY KEY, MemberId BIGINT NOT NULL);

    IF OBJECT_ID('tempdb..#mm') IS NOT NULL DROP TABLE #mm;
    CREATE TABLE #mm (
        MemberId BIGINT NOT NULL PRIMARY KEY,
        LmDue INT NOT NULL, LmOnTime INT NOT NULL, LmLate INT NOT NULL, LmOpen INT NOT NULL,
        TmDue INT NOT NULL, TmOpen INT NOT NULL,
        RmDue INT NOT NULL, RmLiab INT NOT NULL, RmNoOwner INT NOT NULL,
        OpenItems INT NOT NULL, OverdueItems INT NOT NULL, Overdue90Items INT NOT NULL,
        LiabOverdueItems INT NOT NULL, NeverTouchedOverdue INT NOT NULL, NoOwnerOpen INT NOT NULL,
        EligSlip BIT NOT NULL DEFAULT 0, FlagSlip BIT NOT NULL DEFAULT 0,
        EligLiab BIT NOT NULL DEFAULT 0, FlagLiab BIT NOT NULL DEFAULT 0,
        EligChron BIT NOT NULL DEFAULT 0, FlagChron BIT NOT NULL DEFAULT 0,
        EligConc BIT NOT NULL DEFAULT 0, FlagConc BIT NOT NULL DEFAULT 0);

    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) NOT NULL PRIMARY KEY, Eligible INT NOT NULL, Flagged INT NOT NULL,
        FlaggedPct DECIMAL(5,1) NULL, EmitMode VARCHAR(12) NULL, Note NVARCHAR(200) NULL);

    IF OBJECT_ID('tempdb..#cand') IS NOT NULL DROP TABLE #cand;
    CREATE TABLE #cand (
        Detector VARCHAR(40) NOT NULL, Priority TINYINT NOT NULL, SeverityTier TINYINT NOT NULL,
        RankInDetector INT NOT NULL, EntityKind VARCHAR(20) NOT NULL, EntityId BIGINT NULL,
        EntityLabel NVARCHAR(MAX) NULL, ContextKind VARCHAR(20) NULL, ContextLabel NVARCHAR(MAX) NULL,
        Metric VARCHAR(40) NOT NULL, MetricPct INT NULL, TenantPct INT NULL, ItemCount INT NULL,
        BaseCount INT NULL, EventDate DATE NULL, AsAtRequired BIT NOT NULL DEFAULT 0,
        ProblemCount INT NOT NULL, PopulationCount INT NOT NULL, DefaultSlot TINYINT NULL);

    /*  Examples for aggregate-mode patterns (grid #6). Shared shape - byte-identical in sql/36,
        38, 39, 40, 41; sql/37 fills it for the four shared detectors.                          */
    IF OBJECT_ID('tempdb..#eg') IS NOT NULL DROP TABLE #eg;
    CREATE TABLE #eg (
        Detector VARCHAR(40) NOT NULL, PatternFactKey VARCHAR(40) NOT NULL, ExampleRank INT NOT NULL,
        EntityKind VARCHAR(20) NOT NULL, EntityId BIGINT NULL, EntityLabel NVARCHAR(MAX) NOT NULL,
        ContextKind VARCHAR(20) NULL, ContextLabel NVARCHAR(MAX) NULL,
        ItemCount INT NULL, BaseCount INT NULL, UnitLabel NVARCHAR(200) NOT NULL);

    IF OBJECT_ID('tempdb..#facts') IS NOT NULL DROP TABLE #facts;
    CREATE TABLE #facts (
        FactKey VARCHAR(40) NOT NULL PRIMARY KEY, FactValue INT NOT NULL, DisplayLabel NVARCHAR(MAX) NOT NULL,
        Section VARCHAR(16) NOT NULL, DisplayOrder INT NOT NULL, WindowScope VARCHAR(6) NOT NULL,
        ImpactClass VARCHAR(24) NOT NULL, SeverityTier TINYINT NOT NULL, AsAtRequired BIT NOT NULL,
        HeadlineRank TINYINT NULL, IsHeadline BIT NOT NULL DEFAULT 0);

    EXEC dbo.usp_Insights_FreeMonthly_LoadFacts @UserID, @CustomerID, @CurrMonthStart, @AsOf;

    DECLARE @CurrStart DATETIME = CAST(@CurrMonthStart AS DATETIME);
    DECLARE @PrevStart DATETIME = DATEADD(MONTH, -1, @CurrStart);
    DECLARE @NextStart DATETIME = DATEADD(MONTH,  1, @CurrStart);

    /*===================================================================
      1. PEOPLE - the member list
    ===================================================================*/
    IF OBJECT_ID('tempdb..#people') IS NOT NULL DROP TABLE #people;
    CREATE TABLE #people (
        UserID       BIGINT        NOT NULL PRIMARY KEY,
        FullName     NVARCHAR(MAX) NULL,
        InUserTable  BIT           NOT NULL DEFAULT 0,
        IsActive     BIT           NULL,
        IsDeleted    BIT           NULL,
        IsInactive   BIT           NOT NULL DEFAULT 0   -- deactivated, deleted, or unknown
    );

    INSERT #people (UserID)
    SELECT x.UserID
    FROM (
        SELECT s.PerformerID AS UserID FROM #sched s WHERE s.PerformerID IS NOT NULL
        UNION
        SELECT CAST(ca.UserID AS BIGINT)
        FROM ComplianceAssignment ca
        JOIN #inst i ON i.ComplianceInstanceID = ca.ComplianceInstanceID
        WHERE ca.RoleID = 3 AND ca.UserID > 0          -- 3 = performer (role id, not a status)
    ) x;

    UPDATE p
       SET FullName    = LTRIM(RTRIM(CONCAT(u.FirstName, N' ', u.LastName))),
           InUserTable = 1,
           IsActive    = u.IsActive,
           IsDeleted   = u.IsDeleted
    FROM #people p
    JOIN [User] u ON u.ID = p.UserID;

    /*  [TRAP] User.IsActive is NOT inverted (1 = active) - unlike
        ProductMapping.IsActive. Deleted, deactivated and unknown all count.  */
    UPDATE #people
       SET IsInactive = 1
     WHERE InUserTable = 0 OR IsActive = 0 OR IsDeleted = 1;

    INSERT #mem (MemberId, MemberLabel)
    SELECT p.UserID, CASE WHEN @AllowPersonNames = 1 THEN p.FullName END
    FROM #people p;

    INSERT #smap (ComplianceScheduleOnID, MemberId)
    SELECT s.ComplianceScheduleOnID, s.PerformerID
    FROM #sched s
    WHERE s.PerformerID IS NOT NULL;

    /*  [PERF] sql/37 joins and groups #smap by MemberId three times; the PK is
        on the schedule id. Index the column it is actually read by.         */
    CREATE NONCLUSTERED INDEX IX_smap_member ON #smap (MemberId);

    /*===================================================================
      2. SHARED DETECTORS (sql/37)
    ===================================================================*/
    EXEC dbo.usp_Insights_FreeMonthly_MemberDetectors
         @EntityKind = 'person', @EntityPlural = N'people',
         @RelativeRiskFactor = @RelativeRiskFactor, @ConcentrationFactor = @ConcentrationFactor,
         @MemberFloor = @MemberFloor, @MaxPerDetector = @MaxPerDetector, @MaxExamples = @MaxExamples;

    /*  Owned + unowned = all open work holds by construction (#smap maps exactly
        the schedules with a performer), so it is REPORTED in control_totals,
        not asserted - an assertion that cannot fail tests nothing. The real
        tie (member sums vs mapped schedules) is sql/37's 51263.             */
    DECLARE @openAll   INT = (SELECT COUNT(*) FROM #sched WHERE Outcome = 'open');
    DECLARE @openOwned INT = (SELECT ISNULL(SUM(OpenItems), 0) FROM #mm);
    DECLARE @openNone  INT = (SELECT COUNT(*) FROM #sched WHERE Outcome = 'open' AND PerformerID IS NULL);

    /*===================================================================
      3. USERS-ONLY DETECTORS
    ===================================================================*/
    /*-- deactivated_owner: people holding open work who cannot act on it ------*/
    DECLARE @holders     INT = (SELECT COUNT(*) FROM #mm WHERE OpenItems >= 1);
    DECLARE @deactHold   INT = (SELECT COUNT(*) FROM #mm m JOIN #people p ON p.UserID = m.MemberId
                                WHERE m.OpenItems >= 1 AND p.IsInactive = 1);
    DECLARE @deactItems  INT = (SELECT ISNULL(SUM(m.OpenItems), 0) FROM #mm m JOIN #people p ON p.UserID = m.MemberId
                                WHERE p.IsInactive = 1);

    /*-- self_review: performer = reviewer on an open item ------------------------*/
    IF OBJECT_ID('tempdb..#selfRev') IS NOT NULL DROP TABLE #selfRev;
    SELECT s.PerformerID AS UserID, COUNT(*) AS Items
    INTO #selfRev
    FROM #sched s
    WHERE s.Outcome = 'open'
      AND s.PerformerID IS NOT NULL
      AND s.PerformerID = s.ReviewerID
    GROUP BY s.PerformerID;

    DECLARE @selfRevPeople INT = (SELECT COUNT(*) FROM #selfRev);
    DECLARE @selfRevItems  INT = (SELECT ISNULL(SUM(Items), 0) FROM #selfRev);

    INSERT #detector (Detector, Eligible, Flagged, Note)
    VALUES ('deactivated_owner', @holders, @deactHold,
            CASE WHEN @holders = 0 THEN N'nothing to assess - no one holds open work' END),
           ('self_review', @holders, @selfRevPeople,
            CASE WHEN @holders = 0 THEN N'nothing to assess - no one holds open work' END);

    UPDATE #detector
       SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END,
           EmitMode   = CASE WHEN Flagged = 0 THEN 'none'
                             WHEN Flagged >= 2 AND 100.0 * Flagged / NULLIF(Eligible, 0) > 20.0 THEN 'aggregate'
                             ELSE 'individual' END
     WHERE Detector IN ('deactivated_owner', 'self_review');

    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51274, N'DETECTOR CONTRACT VIOLATED - a free monthly users detector flagged more people than it declared eligible. Refusing to emit.', 1;

    DECLARE @deactMode VARCHAR(12) = (SELECT EmitMode FROM #detector WHERE Detector = 'deactivated_owner');
    DECLARE @selfMode  VARCHAR(12) = (SELECT EmitMode FROM #detector WHERE Detector = 'self_review');

    IF @deactMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, ItemCount, ProblemCount, PopulationCount)
        SELECT TOP (@MaxPerDetector) 'deactivated_owner', 1, 1,
               ROW_NUMBER() OVER (ORDER BY m.OpenItems DESC, m.MemberId),
               'person', m.MemberId, CASE WHEN @AllowPersonNames = 1 THEN p.FullName END,
               'open_items_held_by_inactive_user', m.OpenItems, @deactHold, @holders
        FROM #mm m
        JOIN #people p ON p.UserID = m.MemberId
        WHERE m.OpenItems >= 1 AND p.IsInactive = 1
        ORDER BY m.OpenItems DESC, m.MemberId;

    IF @selfMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, ItemCount, BaseCount, ProblemCount, PopulationCount)
        SELECT TOP (@MaxPerDetector) 'self_review', 6, 2,
               ROW_NUMBER() OVER (ORDER BY r.Items DESC, r.UserID),
               'person', r.UserID, CASE WHEN @AllowPersonNames = 1 THEN p.FullName END,
               'open_items_self_reviewed', r.Items, m.OpenItems, @selfRevPeople, @holders
        FROM #selfRev r
        JOIN #people p ON p.UserID = r.UserID
        JOIN #mm m     ON m.MemberId = r.UserID
        ORDER BY r.Items DESC, r.UserID;

    /*===================================================================
      4. FACTS
    ===================================================================*/
    DECLARE @withOverdue INT = (SELECT COUNT(*) FROM #mm WHERE OverdueItems >= 1);
    DECLARE @tOverdue    INT = (SELECT COUNT(*) FROM #sched WHERE IsOverdue = 1);
    DECLARE @top3        INT = (SELECT ISNULL(SUM(t.OverdueItems), 0)
                                FROM (SELECT TOP 3 x.OverdueItems FROM #mm x ORDER BY x.OverdueItems DESC) t);

    INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
    VALUES
     ('u_people_with_open_work',       @holders,        N'people hold open work in your scope',                                          'people', 100, 'stock', 'volume',                 5, 0, NULL),
     ('u_people_with_overdue',         @withOverdue,    N'of those hold at least one overdue item',                                      'people', 110, 'stock', 'volume',                 4, 0, NULL),
     ('u_open_items',                  @openAll,        N'open items in your scope (overdue or not yet due)',                            'people', 120, 'stock', 'volume',                 5, 0, NULL),
     ('u_open_no_owner',               @openNone,       N'open items with no one assigned to do them',                                   'people', 130, 'stock', 'operational_continuity', 2, 0, 7),
     ('u_open_no_reviewer',            (SELECT COUNT(*) FROM #sched WHERE Outcome = 'open' AND ReviewerSource = 'none'),
                                                        N'open items with no reviewer assigned',                                         'people', 140, 'stock', 'operational_continuity', 3, 0, NULL),
     ('u_open_self_reviewed',          @selfRevItems,   N'open items where the same person is both performer and reviewer',              'people', 150, 'stock', 'operational_continuity', 2, 0, NULL),
     ('u_people_self_reviewing',       @selfRevPeople,  N'people are both performer and reviewer on at least one open item',             'people', 155, 'stock', 'operational_continuity', 2, 0, NULL),
     ('u_inactive_people_with_work',   @deactHold,      N'people holding open work are no longer active users',                          'people', 160, 'stock', 'operational_continuity', 1, 0, NULL),
     ('u_open_items_inactive_owner',   @deactItems,     N'open items are assigned to people who are no longer active users',             'people', 170, 'stock', 'operational_continuity', 1, 0, 1);

    /*  Top-3 share - unnamed concentration. Vacuous when 3 people are all
        the people with overdue work, so only with 4 or more.               */
    IF @withOverdue >= 4 AND @tOverdue > 0
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('u_top3_overdue_share_pct', CAST(FLOOR(100.0 * @top3 / @tOverdue) AS INT),
                N'per cent of all overdue items sit with the 3 people holding the most (rounded down)',
                'people', 180, 'stock', 'operational_continuity', 3, 0, 8);

    /*  [2026-09-23] Each pattern now carries its _of partner - the population it was measured
        against. Without it "N of your M people" was unwritable, and worse, a model reaching for
        a denominator took u_people_with_open_work and produced a fraction nobody computed.     */
    IF @deactMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_deactivated_owner',    @deactHold, N'people holding open work are no longer active users - a pattern across your scope', 'patterns', 690, 'stock', 'operational_continuity', 1, 0, 1),
               ('pat_deactivated_owner_of', @holders,   N'people holding open work were compared',                                            'patterns', 691, 'ctx',   'volume',                 5, 0, NULL);

    IF @selfMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_self_review',    @selfRevPeople, N'people review their own work on open items - a pattern across your scope', 'patterns', 740, 'stock', 'operational_continuity', 2, 0, 6),
               ('pat_self_review_of', @holders,       N'people holding open work were compared',                                   'patterns', 741, 'ctx',   'volume',                 5, 0, NULL);

    /*  EXAMPLES for the users-only patterns - same order as individual mode, counts only, and
        only when names are allowed: with @AllowPersonNames = 0 every label is NULL and the
        WHERE keeps every person out. Explicit, not incidental.                                */
    IF @deactMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples) 'deactivated_owner', 'pat_deactivated_owner',
               ROW_NUMBER() OVER (ORDER BY m.OpenItems DESC, m.MemberId),
               'person', m.MemberId, p.FullName, m.OpenItems, NULL,
               N'open obligations are held by this person, who is no longer an active user'
        FROM #mm m
        JOIN #people p ON p.UserID = m.MemberId
        WHERE m.OpenItems >= 1 AND p.IsInactive = 1
          AND @AllowPersonNames = 1 AND p.FullName IS NOT NULL
        ORDER BY m.OpenItems DESC, m.MemberId;

    IF @selfMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples) 'self_review', 'pat_self_review',
               ROW_NUMBER() OVER (ORDER BY r.Items DESC, r.UserID),
               'person', r.UserID, p.FullName, r.Items, m.OpenItems,
               N'of the open obligations this person holds are also reviewed by the same person'
        FROM #selfRev r
        JOIN #people p ON p.UserID = r.UserID
        JOIN #mm m     ON m.MemberId = r.UserID
        WHERE @AllowPersonNames = 1 AND p.FullName IS NOT NULL
        ORDER BY r.Items DESC, r.UserID;

    /*===================================================================
      5. DEFAULT SLOTS + HEADLINE (identical block in every dimension slot)
    ===================================================================*/
    ;WITH s1 AS (SELECT TOP 1 * FROM #cand WHERE RankInDetector = 1 ORDER BY Priority, Detector)
    UPDATE s1 SET DefaultSlot = 1;

    ;WITH s2 AS (
        SELECT TOP 1 c.* FROM #cand c
        WHERE c.RankInDetector = 1 AND c.DefaultSlot IS NULL
          AND NOT EXISTS (SELECT 1 FROM #cand f
                          WHERE f.DefaultSlot = 1 AND f.EntityKind = c.EntityKind AND f.EntityId = c.EntityId)
        ORDER BY c.Priority, c.Detector)
    UPDATE s2 SET DefaultSlot = 2;

    /*  The lead is the named finding when its priority is at least as high
        as the best non-zero fact; otherwise the fact leads. One scale.      */
    DECLARE @bestCand INT = (SELECT MIN(Priority) FROM #cand WHERE DefaultSlot = 1);
    DECLARE @bestFact INT = (SELECT MIN(HeadlineRank) FROM #facts WHERE HeadlineRank IS NOT NULL AND FactValue > 0);
    DECLARE @headlineSource VARCHAR(10) =
        CASE WHEN @bestCand IS NOT NULL AND (@bestFact IS NULL OR @bestCand <= @bestFact) THEN 'candidate' ELSE 'fact' END;

    IF @headlineSource = 'fact'
    BEGIN
        ;WITH pick AS (SELECT TOP 1 FactKey FROM #facts
                       WHERE HeadlineRank IS NOT NULL AND FactValue > 0
                       ORDER BY HeadlineRank, DisplayOrder)
        UPDATE f SET IsHeadline = 1 FROM #facts f JOIN pick p ON p.FactKey = f.FactKey;

        IF NOT EXISTS (SELECT 1 FROM #facts WHERE IsHeadline = 1)
            UPDATE #facts SET IsHeadline = 1 WHERE FactKey = 't_rm_due';
    END

    /*  EXAMPLES CONTRACT (2026-09-23 design, Sec.2.7). An example exists only in aggregate
        mode, beside the pattern fact it illustrates. Codes from this file's own block.       */
    IF EXISTS (SELECT 1 FROM #eg e JOIN #cand c ON c.Detector = e.Detector)
        THROW 51271, N'EXAMPLES CONTRACT VIOLATED - a free monthly users detector emitted both named candidates and examples. Examples exist only in aggregate mode. Refusing to emit.', 1;
    IF EXISTS (SELECT 1 FROM #eg e LEFT JOIN #facts f ON f.FactKey = e.PatternFactKey WHERE f.FactKey IS NULL)
        THROW 51272, N'EXAMPLES CONTRACT VIOLATED - a free monthly users example refers to a pattern fact that was not emitted. Refusing to emit.', 1;

    /*===================================================================
      6. EMIT
    ===================================================================*/
    SELECT 'control_totals'                           AS ResultSet,
           @CustomerID                                AS CustomerID,
           @UserID                                    AS UserID,
           'users'                                    AS Slot,
           CAST(@PrevStart AS DATE)                   AS PrevMonthStart,
           CAST(DATEADD(DAY, -1, @CurrStart) AS DATE) AS PrevMonthEnd,
           CAST(@CurrStart AS DATE)                   AS CurrMonthStart,
           CAST(DATEADD(DAY, -1, @NextStart) AS DATE) AS CurrMonthEnd,
           @AsOf                                      AS AsOf,
           (SELECT COUNT(*) FROM #inst)               AS ScopedInstances,
           (SELECT COUNT(*) FROM #sched)              AS SchedulesLoaded,
           (SELECT COUNT(*) FROM #mem)                AS MembersInScope,
           @holders                                   AS MembersWithOpenWork,
           @openAll                                   AS OpenItems,
           @openOwned                                 AS OpenItemsOwned,      /* + OpenItemsUnowned = OpenItems */
           @openNone                                  AS OpenItemsUnowned,
           CAST(1 AS BIT)                             AS Reconciled,
           @headlineSource                            AS HeadlineSource,
           @AllowPersonNames                          AS PersonNamesAllowed;

    SELECT 'facts' AS ResultSet, FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope,
           ImpactClass, SeverityTier, AsAtRequired, IsHeadline
    FROM #facts ORDER BY DisplayOrder;

    SELECT 'detector_policy' AS ResultSet, Detector, Eligible, Flagged, FlaggedPct, EmitMode, Note
    FROM #detector;

    SELECT 'candidates' AS ResultSet, DefaultSlot, Detector, Priority, SeverityTier, RankInDetector,
           EntityKind, EntityId, EntityLabel, ContextKind, ContextLabel, Metric, MetricPct, TenantPct,
           ItemCount, BaseCount, EventDate, AsAtRequired, ProblemCount, PopulationCount,
           ProblemCount - 1 AS ResidualCount
    FROM #cand
    ORDER BY CASE WHEN DefaultSlot IS NULL THEN 9 ELSE DefaultSlot END, Priority, Detector, RankInDetector;

    SELECT 'data_quality' AS ResultSet, Code, ItemCount, Detail
    FROM (VALUES
        ('performer_not_in_user_table', (SELECT COUNT(*) FROM #people WHERE InUserTable = 0),
         N'Performer ids on scoped work with no row in [User]. Counted as "not an active user" in deactivated_owner.'),
        ('open_items_no_owner_anywhere', @openNone,
         N'Open items with no performer on the schedule AND none on the obligation (schedule-first read, 99.8% populated).'),
        ('person_names_withheld', CASE WHEN @AllowPersonNames = 1 THEN 0 ELSE 1 END,
         N'1 = @AllowPersonNames is 0: every person candidate carries a NULL EntityLabel and must be described, never named.'),
        ('prev_month_not_settled', (SELECT ISNULL(SUM(LmDue), 0) FROM #mm),
         N'Last-month figures are as at the run date; late closures can still arrive.')
    ) AS dq(Code, ItemCount, Detail);

    /*  Grid #6 - APPENDED LAST, never before data_quality (a reader not yet updated would
        map example rows onto its data_quality shape). Empty is normal.                     */
    SELECT 'examples' AS ResultSet, Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel,
           ContextKind, ContextLabel, ItemCount, BaseCount, UnitLabel
    FROM #eg
    ORDER BY PatternFactKey, ExampleRank;
END
GO
PRINT 'usp_Insights_FreeMonthly_Users restored.';
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Overview', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Overview;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_Overview
    @UserID              INT,
    @CustomerID          INT,
    @CurrMonthStart      DATE,
    @AsOf                DATETIME,
    @RelativeRiskFactor  DECIMAL(4,2) = 1.50,   -- flagged at >= this multiple of the tenant's own rate
    @CategoryFloor       INT          = 10,     -- min obligations for a category to be ranked
    @MaxExamples         INT          = 3       -- examples named inside an aggregate-mode pattern (2026-09-23)
AS
BEGIN
    SET NOCOUNT ON;

    /*===================================================================
      0. LOAD - the shared estate definition (sql/34, sql/35)
    ===================================================================*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    CREATE TABLE #inst (
        ComplianceInstanceID BIGINT        NOT NULL PRIMARY KEY,
        BranchID             INT           NOT NULL,
        CategoryId           INT           NULL,
        ActID                INT           NULL,
        ComplianceID         BIGINT        NOT NULL,
        DepartmentID         BIGINT        NULL,
        Imprisonment         BIT           NOT NULL,
        RiskType             INT           NULL,
        RiskClass            NVARCHAR(200)  NULL
    );

    IF OBJECT_ID('tempdb..#sched') IS NOT NULL DROP TABLE #sched;
    CREATE TABLE #sched (
        ComplianceScheduleOnID BIGINT       NOT NULL PRIMARY KEY,
        ComplianceInstanceID   BIGINT       NOT NULL,
        BranchID               INT          NOT NULL,
        ScheduleOn             DATETIME     NOT NULL,
        WindowPart             VARCHAR(14)  NULL,
        PerformerID            BIGINT       NULL,
        PerformerSource        VARCHAR(10)  NOT NULL,
        ReviewerID             BIGINT       NULL,
        ReviewerSource         VARCHAR(10)  NOT NULL,
        NeverTouched           BIT          NOT NULL,
        Outcome                VARCHAR(20)  NOT NULL,
        IsOverdue              BIT          NOT NULL,
        DaysPastDue            INT          NULL,
        AgeBand                VARCHAR(10)  NULL
    );

    IF OBJECT_ID('tempdb..#lic') IS NOT NULL DROP TABLE #lic;
    CREATE TABLE #lic (
        LicenseId          BIGINT        NOT NULL PRIMARY KEY,
        BranchID           INT           NOT NULL,
        LicenseTypeID      BIGINT        NULL,
        LicenseTypeName    NVARCHAR(MAX) NULL,
        EndDate            DATETIME      NULL,
        StatusBucket       NVARCHAR(200)  NULL,
        LicenceState       VARCHAR(12)   NOT NULL,
        RenewalInProgress  BIT           NOT NULL,
        LapsesRestOfMonth  BIT           NOT NULL,
        LapsedThisMonth    BIT           NOT NULL,
        LapsedLastMonth    BIT           NOT NULL
    );

    /*  Both loaders validate the window, the scope and the dictionary, and
        THROW on any failure. Neither returns a result set.                 */
    EXEC dbo.usp_Insights_FreeMonthly_LoadFacts    @UserID, @CustomerID, @CurrMonthStart, @AsOf;
    EXEC dbo.usp_Insights_FreeMonthly_LoadLicences @UserID, @CustomerID, @CurrMonthStart, @AsOf;

    DECLARE @CurrStart DATETIME = CAST(@CurrMonthStart AS DATETIME);
    DECLARE @PrevStart DATETIME = DATEADD(MONTH, -1, @CurrStart);
    DECLARE @NextStart DATETIME = DATEADD(MONTH,  1, @CurrStart);

    /*===================================================================
      1. WINDOW DECOMPOSITION - built from the LIST of window parts, not
         from the facts, so an empty part still yields a zero row.
    ===================================================================*/
    IF OBJECT_ID('tempdb..#wp') IS NOT NULL DROP TABLE #wp;
    CREATE TABLE #wp (
        WindowPart     VARCHAR(14) NOT NULL PRIMARY KEY,
        Due            INT NOT NULL,
        OnTime         INT NOT NULL,
        Late           INT NOT NULL,
        Untimed        INT NOT NULL,
        Terminal       INT NOT NULL,
        OpenCount      INT NOT NULL,
        Liability      INT NOT NULL,
        Critical       INT NOT NULL,
        NoOwner        INT NOT NULL,
        OpenLiability  INT NOT NULL,
        OpenCritical   INT NOT NULL,
        OpenUntouched  INT NOT NULL
    );

    INSERT #wp (WindowPart, Due, OnTime, Late, Untimed, Terminal, OpenCount, Liability,
                Critical, NoOwner, OpenLiability, OpenCritical, OpenUntouched)
    SELECT
        p.WindowPart,
        COUNT(s.ComplianceScheduleOnID),
        ISNULL(SUM(CASE WHEN s.Outcome = 'completed_on_time' THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.Outcome = 'completed_late'    THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.Outcome = 'completed_untimed' THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.Outcome = 'resolved_terminal' THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.Outcome = 'open'              THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN i.Imprisonment = 1              THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN i.RiskClass = N'Critical'       THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.PerformerSource = 'none'      THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.Outcome = 'open' AND i.Imprisonment = 1        THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.Outcome = 'open' AND i.RiskClass = N'Critical' THEN 1 ELSE 0 END), 0),
        ISNULL(SUM(CASE WHEN s.Outcome = 'open' AND s.NeverTouched = 1        THEN 1 ELSE 0 END), 0)
    FROM (VALUES ('prev_month'), ('curr_elapsed'), ('curr_remaining')) AS p(WindowPart)
    LEFT JOIN #sched s ON s.WindowPart = p.WindowPart
    LEFT JOIN #inst  i ON i.ComplianceInstanceID = s.ComplianceInstanceID
    GROUP BY p.WindowPart;

    IF EXISTS (SELECT 1 FROM #wp WHERE Due <> OnTime + Late + Untimed + Terminal + OpenCount)
        THROW 51251, N'FREE MONTHLY OVERVIEW RECONCILIATION FAILED - a window part does not decompose exactly into on-time + late + untimed + closed-without-completion + open. Refusing to publish.', 1;

    DECLARE @lmDue INT, @lmOnTime INT, @lmLate INT, @lmUntimed INT, @lmTerminal INT, @lmOpen INT,
            @lmOpenLiab INT, @lmOpenCrit INT, @lmOpenUntouched INT,
            @tmDue INT, @tmCompleted INT, @tmTerminal INT, @tmOpen INT, @tmOpenLiab INT,
            @rmDue INT, @rmLiab INT, @rmCrit INT, @rmNoOwner INT, @rmClosedEarly INT;

    SELECT @lmDue = Due, @lmOnTime = OnTime, @lmLate = Late, @lmUntimed = Untimed,
           @lmTerminal = Terminal, @lmOpen = OpenCount, @lmOpenLiab = OpenLiability,
           @lmOpenCrit = OpenCritical, @lmOpenUntouched = OpenUntouched
    FROM #wp WHERE WindowPart = 'prev_month';

    SELECT @tmDue = Due, @tmCompleted = OnTime + Late + Untimed, @tmTerminal = Terminal,
           @tmOpen = OpenCount, @tmOpenLiab = OpenLiability
    FROM #wp WHERE WindowPart = 'curr_elapsed';

    SELECT @rmDue = Due, @rmLiab = Liability, @rmCrit = Critical, @rmNoOwner = NoOwner,
           @rmClosedEarly = OnTime + Late + Untimed
    FROM #wp WHERE WindowPart = 'curr_remaining';

    DECLARE @lmCompleted INT = @lmOnTime + @lmLate + @lmUntimed;
    DECLARE @lmOnTimePct INT = CASE WHEN @lmCompleted = 0 THEN NULL
                                    ELSE CAST(FLOOR(100.0 * @lmOnTime / @lmCompleted) AS INT) END;

    /*===================================================================
      2. OVERDUE STOCK AS AT @AsOf (any due date) - age-banded
    ===================================================================*/
    DECLARE @odTotal INT, @od0030 INT, @od3160 INT, @od6190 INT, @od91p INT,
            @od91pLiab INT, @odLiab INT, @odUntouched INT, @odNoOwner INT;

    SELECT @odTotal     = COUNT(*),
           @od0030      = ISNULL(SUM(CASE WHEN s.AgeBand = 'd000_030' THEN 1 ELSE 0 END), 0),
           @od3160      = ISNULL(SUM(CASE WHEN s.AgeBand = 'd031_060' THEN 1 ELSE 0 END), 0),
           @od6190      = ISNULL(SUM(CASE WHEN s.AgeBand = 'd061_090' THEN 1 ELSE 0 END), 0),
           @od91p       = ISNULL(SUM(CASE WHEN s.AgeBand = 'd091_plus' THEN 1 ELSE 0 END), 0),
           @od91pLiab   = ISNULL(SUM(CASE WHEN s.AgeBand = 'd091_plus' AND i.Imprisonment = 1 THEN 1 ELSE 0 END), 0),
           @odLiab      = ISNULL(SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END), 0),
           @odUntouched = ISNULL(SUM(CASE WHEN s.NeverTouched = 1 THEN 1 ELSE 0 END), 0),
           @odNoOwner   = ISNULL(SUM(CASE WHEN s.PerformerSource = 'none' THEN 1 ELSE 0 END), 0)
    FROM #sched s
    JOIN #inst  i ON i.ComplianceInstanceID = s.ComplianceInstanceID
    WHERE s.IsOverdue = 1;

    IF @odTotal <> @od0030 + @od3160 + @od6190 + @od91p
        THROW 51252, N'FREE MONTHLY OVERVIEW RECONCILIATION FAILED - overdue age bands do not sum to the overdue stock. Refusing to publish.', 1;

    /*===================================================================
      3. LICENCES
    ===================================================================*/
    DECLARE @licTotal INT, @licValid INT, @licLapsed INT, @licEndedOther INT, @licNoEnd INT, @licOtherStatus INT,
            @licLapsingRom INT, @licLapsingUnrenewed INT, @licLapsedLastMonth INT,
            @licLapsedThisMonth INT, @licLapsedUnrenewed INT;

    SELECT @licTotal            = COUNT(*),
           @licValid            = ISNULL(SUM(CASE WHEN LicenceState = 'valid'       THEN 1 ELSE 0 END), 0),
           @licLapsed           = ISNULL(SUM(CASE WHEN LicenceState = 'lapsed'      THEN 1 ELSE 0 END), 0),
           @licEndedOther       = ISNULL(SUM(CASE WHEN LicenceState = 'ended_other' THEN 1 ELSE 0 END), 0),
           @licNoEnd            = ISNULL(SUM(CASE WHEN LicenceState = 'no_end_date' THEN 1 ELSE 0 END), 0),
           @licOtherStatus      = ISNULL(SUM(CASE WHEN LicenceState = 'other_status' THEN 1 ELSE 0 END), 0),   -- 2026-09-29, sql/35
           @licLapsingRom       = ISNULL(SUM(CASE WHEN LapsesRestOfMonth = 1 THEN 1 ELSE 0 END), 0),
           @licLapsingUnrenewed = ISNULL(SUM(CASE WHEN LapsesRestOfMonth = 1 AND RenewalInProgress = 0 THEN 1 ELSE 0 END), 0),
           @licLapsedLastMonth  = ISNULL(SUM(CASE WHEN LapsedLastMonth = 1 THEN 1 ELSE 0 END), 0),
           @licLapsedThisMonth  = ISNULL(SUM(CASE WHEN LapsedThisMonth = 1 THEN 1 ELSE 0 END), 0),
           @licLapsedUnrenewed  = ISNULL(SUM(CASE WHEN LicenceState = 'lapsed' AND RenewalInProgress = 0 THEN 1 ELSE 0 END), 0)
    FROM #lic;

    IF @licTotal <> @licValid + @licLapsed + @licEndedOther + @licNoEnd + @licOtherStatus
        THROW 51253, N'FREE MONTHLY OVERVIEW RECONCILIATION FAILED - licence states do not partition the scoped licence set. Refusing to publish.', 1;

    /*===================================================================
      4. ESTATE CONTEXT
    ===================================================================*/
    DECLARE @obligations      INT = (SELECT COUNT(*) FROM #inst);
    /*  [2026-09-29 RegTrack parity] The dashboard lists a location only when it
        holds at least one counted obligation (SP_GetEntitySummary), so the
        location count comes from #inst, not from the raw scope pairs. Both
        facts keep their keys for the C# contract; they are now equal.       */
    DECLARE @locationsInScope INT = (SELECT COUNT(DISTINCT BranchID) FROM #inst);
    DECLARE @locationsWithObl INT = @locationsInScope;

    /*===================================================================
      5. DETECTORS - instance level: an obligation is "overdue" when it has
         at least one overdue schedule (sql/05's convention).
    ===================================================================*/
    IF OBJECT_ID('tempdb..#instOverdue') IS NOT NULL DROP TABLE #instOverdue;
    SELECT DISTINCT s.ComplianceInstanceID
    INTO #instOverdue
    FROM #sched s
    WHERE s.IsOverdue = 1;
    CREATE CLUSTERED INDEX IX_instOverdue ON #instOverdue (ComplianceInstanceID);

    /*-- 5a. liability_overdue_location ----------------------------------------*/
    IF OBJECT_ID('tempdb..#liabLoc') IS NOT NULL DROP TABLE #liabLoc;
    CREATE TABLE #liabLoc (
        BranchID          INT           NOT NULL PRIMARY KEY,
        BranchName        NVARCHAR(MAX) NULL,   -- MAX: never truncate a real name (sql/05 hit this live on tenant 29)
        LiabInst          INT           NOT NULL,
        LiabOverdueInst   INT           NOT NULL,
        LiabOverdueRate   DECIMAL(9,4)  NOT NULL,
        LiabOverdueItems  INT           NOT NULL DEFAULT 0,  -- overdue SCHEDULES, same unit as od_liability
        IsFlagged         BIT           NOT NULL DEFAULT 0
    );

    /*  Flag each row first, THEN aggregate - never EXISTS inside a CASE inside
        an aggregate (Msg 130 at CREATE time, CLAUDE.md Sec.3).             */
    ;WITH liab AS (
        SELECT i.BranchID,
               CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END AS IsOverdueFlag
        FROM #inst i
        LEFT JOIN #instOverdue o ON o.ComplianceInstanceID = i.ComplianceInstanceID
        WHERE i.Imprisonment = 1
    )
    INSERT #liabLoc (BranchID, BranchName, LiabInst, LiabOverdueInst, LiabOverdueRate)
    SELECT l.BranchID, MAX(cb.Name), COUNT(*), SUM(l.IsOverdueFlag),
           CAST(1.0 * SUM(l.IsOverdueFlag) / COUNT(*) AS DECIMAL(9,4))
    FROM liab l
    JOIN CustomerBranch cb ON cb.ID = l.BranchID
    GROUP BY l.BranchID;

    /*  [UNIT] Flagging is instance-level (an obligation is overdue if any of
        its occurrences is - sql/05's convention, lag-safe). The NAMED value
        is occurrence-level, the same unit as the od_liability fact, so the
        email may truthfully say "{{NAME}} holds 6 of them". Mixing the two
        units in one sentence would be a false statement.                    */
    UPDATE ll
       SET LiabOverdueItems = x.Items
    FROM #liabLoc ll
    JOIN (SELECT s.BranchID, COUNT(*) AS Items
          FROM #sched s
          JOIN #inst  i ON i.ComplianceInstanceID = s.ComplianceInstanceID
          WHERE s.IsOverdue = 1 AND i.Imprisonment = 1
          GROUP BY s.BranchID) x ON x.BranchID = ll.BranchID;

    DECLARE @liabInstTotal    INT = (SELECT ISNULL(SUM(LiabInst), 0)        FROM #liabLoc);
    DECLARE @liabOverdueTotal INT = (SELECT ISNULL(SUM(LiabOverdueInst), 0) FROM #liabLoc);
    DECLARE @tenantLiabRate   DECIMAL(9,4) = CASE WHEN @liabInstTotal = 0 THEN 0
                                                  ELSE 1.0 * @liabOverdueTotal / @liabInstTotal END;
    DECLARE @liabMembers      INT = (SELECT COUNT(*) FROM #liabLoc);

    IF @liabMembers >= 2 AND @tenantLiabRate > 0
        UPDATE #liabLoc
           SET IsFlagged = 1
         WHERE LiabOverdueInst >= 1
           AND LiabOverdueRate >= @tenantLiabRate * @RelativeRiskFactor;

    /*-- 5b. category_overdue_skew --------------------------------------------*/
    IF OBJECT_ID('tempdb..#cat') IS NOT NULL DROP TABLE #cat;
    CREATE TABLE #cat (
        CategoryId        INT           NOT NULL PRIMARY KEY,
        CategoryName      NVARCHAR(100) NULL,
        Inst              INT           NOT NULL,
        OverdueInst       INT           NOT NULL,
        OverdueRate       DECIMAL(9,4)  NOT NULL,
        IsMaterial        BIT           NOT NULL DEFAULT 0,
        IsFlagged         BIT           NOT NULL DEFAULT 0
    );

    ;WITH c AS (
        SELECT i.CategoryId,
               CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END AS IsOverdueFlag
        FROM #inst i
        LEFT JOIN #instOverdue o ON o.ComplianceInstanceID = i.ComplianceInstanceID
        WHERE i.CategoryId IS NOT NULL
    )
    INSERT #cat (CategoryId, CategoryName, Inst, OverdueInst, OverdueRate)
    SELECT c.CategoryId, MAX(cc.Name), COUNT(*), SUM(c.IsOverdueFlag),
           CAST(1.0 * SUM(c.IsOverdueFlag) / COUNT(*) AS DECIMAL(9,4))
    FROM c
    LEFT JOIN ComplianceCategory cc ON cc.ID = c.CategoryId
    GROUP BY c.CategoryId;

    DECLARE @tenantOverdueInst INT = (SELECT COUNT(*) FROM #instOverdue);
    DECLARE @tenantOverdueRate DECIMAL(9,4) = CASE WHEN @obligations = 0 THEN 0
                                                   ELSE 1.0 * @tenantOverdueInst / @obligations END;

    /*  Materiality floor with a declared fallback (CLAUDE.md Sec.4: an empty
        peer sample widens, never infers from nothing).                      */
    DECLARE @catFloor INT = @CategoryFloor;
    DECLARE @catDegraded BIT = 0;
    IF (SELECT COUNT(*) FROM #cat WHERE Inst >= @catFloor) < 2
    BEGIN
        SET @catFloor = 1;
        SET @catDegraded = 1;
    END

    UPDATE #cat SET IsMaterial = 1 WHERE Inst >= @catFloor;
    DECLARE @catMembers INT = (SELECT COUNT(*) FROM #cat WHERE IsMaterial = 1);

    IF @catMembers >= 2 AND @tenantOverdueRate > 0
        UPDATE #cat
           SET IsFlagged = 1
         WHERE IsMaterial = 1
           AND OverdueInst >= 1
           AND OverdueRate >= @tenantOverdueRate * @RelativeRiskFactor;

    /*-- 5c. emission policy ------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector    VARCHAR(40)   NOT NULL PRIMARY KEY,
        Eligible    INT           NOT NULL,
        Flagged     INT           NOT NULL,
        FlaggedPct  DECIMAL(5,1)  NULL,
        EmitMode    VARCHAR(12)   NULL,     -- individual | aggregate | none
        Note        NVARCHAR(200) NULL
    );

    INSERT #detector (Detector, Eligible, Flagged, Note)
    SELECT 'liability_overdue_location',
           @liabMembers,
           (SELECT COUNT(*) FROM #liabLoc WHERE IsFlagged = 1),
           CASE WHEN @liabMembers < 2   THEN N'suppressed - fewer than 2 locations hold liability-bearing obligations'
                WHEN @tenantLiabRate = 0 THEN N'nothing to compare - no liability-bearing obligation is overdue'
                ELSE NULL END
    UNION ALL
    SELECT 'category_overdue_skew',
           @catMembers,
           (SELECT COUNT(*) FROM #cat WHERE IsFlagged = 1),
           CASE WHEN @catMembers < 2       THEN N'suppressed - fewer than 2 material categories'
                WHEN @tenantOverdueRate = 0 THEN N'nothing to compare - nothing is overdue'
                WHEN @catDegraded = 1       THEN N'degraded_peer_sample - materiality floor widened to 1'
                ELSE NULL END;

    UPDATE #detector
       SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;

    /*  [DEVIATION FROM Sec.4, DELIBERATE] Aggregate mode also requires
        Flagged >= 2. The 20% rule exists to stop a PATTERN flooding the report
        with per-member findings. On a small tenant one flag among 2-4 members
        is 25-50% - by the letter an "aggregate", which would print "1 locations
        are running above average" and hide the one real, nameable finding.
        One member is not a pattern. The 2-name cap still bounds the output.  */
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0                          THEN 'none'
                           WHEN Flagged >= 2 AND FlaggedPct > 20.0   THEN 'aggregate'
                           ELSE 'individual' END;

    /*  Own code - NOT the shared 51040, which SqlFreeDigestRepository maps to a
        dictionary-gap exception.                                            */
    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51254, N'DETECTOR CONTRACT VIOLATED - a free monthly overview detector flagged more members than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

    DECLARE @liabMode VARCHAR(12) = (SELECT EmitMode FROM #detector WHERE Detector = 'liability_overdue_location');
    DECLARE @catMode  VARCHAR(12) = (SELECT EmitMode FROM #detector WHERE Detector = 'category_overdue_skew');
    DECLARE @liabFlagged INT = (SELECT Flagged FROM #detector WHERE Detector = 'liability_overdue_location');
    DECLARE @catFlagged  INT = (SELECT Flagged FROM #detector WHERE Detector = 'category_overdue_skew');

    /*===================================================================
      6. FACTS - the complete closed set the narrative may cite
    ===================================================================*/
    IF OBJECT_ID('tempdb..#facts') IS NOT NULL DROP TABLE #facts;
    CREATE TABLE #facts (
        FactKey          VARCHAR(40)   NOT NULL PRIMARY KEY,
        FactValue        INT           NOT NULL,
        DisplayLabel     NVARCHAR(MAX) NOT NULL,
        Section          VARCHAR(16)   NOT NULL,   -- last_month | this_month | overdue_now | rest_of_month | licences | estate
        DisplayOrder     INT           NOT NULL,
        WindowScope      VARCHAR(6)    NOT NULL,   -- prev | curr | stock | ctx   (ratios allowed ONLY on prev)
        ImpactClass      VARCHAR(24)   NOT NULL,
        SeverityTier     TINYINT       NOT NULL,   -- 1 most severe .. 5 context
        AsAtRequired     BIT           NOT NULL,   -- value can still move; renderer states the as-at date
        HeadlineRank     TINYINT       NULL,       -- lower leads; NULL = never leads (a slice of a larger fact)
        IsHeadline       BIT           NOT NULL DEFAULT 0
    );

    INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
    VALUES
    /* -- last month: due-date cohort, outcome as at @AsOf -------------------- */
     ('lm_due',                      @lmDue,           N'obligations fell due last month',                                                      'last_month',   100, 'prev',  'volume',                 4, 1, NULL),
     ('lm_on_time',                  @lmOnTime,        N'of those were closed on time',                                                         'last_month',   110, 'prev',  'performance',            3, 1, NULL),
     ('lm_late',                     @lmLate,          N'of those were closed, but after the due date',                                        'last_month',   120, 'prev',  'performance',            3, 1, NULL),
     ('lm_closed_without_completion',@lmTerminal,      N'of those were closed by the reviewer without completion (marked not applicable or not complied)', 'last_month', 130, 'prev', 'performance', 3, 1, NULL),
     ('lm_still_open',               @lmOpen,          N'of those are still open',                                                              'last_month',   140, 'prev',  'operational_continuity', 2, 1, 7),
     ('lm_open_liability',           @lmOpenLiab,      N'still-open items from last month that carry personal criminal liability for the responsible officer', 'last_month', 150, 'prev', 'personal_liability', 1, 1, 1),
     ('lm_open_critical',            @lmOpenCrit,      N'still-open items from last month rated critical',                                      'last_month',   160, 'prev',  'operational_continuity', 2, 1, NULL),
     ('lm_open_never_touched',       @lmOpenUntouched, N'still-open items from last month with no action recorded at all',                      'last_month',   170, 'prev',  'operational_continuity', 2, 1, 5),
    /* -- this month so far (1st to today): stock, no ratio ------------------- */
     ('tm_due_so_far',               @tmDue,           N'obligations fell due between the 1st of this month and today',                         'this_month',   200, 'curr',  'volume',                 4, 0, NULL),
     ('tm_closed_so_far',            @tmCompleted,     N'of those are already closed',                                                          'this_month',   210, 'curr',  'volume',                 4, 0, NULL),
     ('tm_closed_without_completion',@tmTerminal,      N'of those were closed by the reviewer without completion',                              'this_month',   215, 'curr',  'volume',                 4, 0, NULL),
     ('tm_open_past_due',            @tmOpen,          N'of those are already past their due date and still open',                              'this_month',   220, 'curr',  'operational_continuity', 2, 0, 8),
     ('tm_open_liability',           @tmOpenLiab,      N'of those past-due items carry personal criminal liability',                            'this_month',   230, 'curr',  'personal_liability',     1, 0, 2),
    /* -- overdue right now, any due date: stock ------------------------------ */
     ('od_total',                    @odTotal,         N'obligations are overdue today, whatever their due date',                               'overdue_now',  300, 'stock', 'operational_continuity', 2, 0, NULL),
     ('od_0_30_days',                @od0030,          N'of those have been overdue for 30 days or less',                                       'overdue_now',  310, 'stock', 'operational_continuity', 3, 0, NULL),
     ('od_31_60_days',               @od3160,          N'of those have been overdue for 31 to 60 days',                                         'overdue_now',  320, 'stock', 'operational_continuity', 3, 0, NULL),
     ('od_61_90_days',               @od6190,          N'of those have been overdue for 61 to 90 days',                                         'overdue_now',  330, 'stock', 'operational_continuity', 2, 0, NULL),
     ('od_over_90_days',             @od91p,           N'of those have been overdue for more than 90 days',                                     'overdue_now',  340, 'stock', 'operational_continuity', 2, 0, 11),
     ('od_over_90_liability',        @od91pLiab,       N'overdue for more than 90 days AND carry personal criminal liability',                  'overdue_now',  350, 'stock', 'personal_liability',     1, 0, NULL),
     ('od_liability',                @odLiab,          N'overdue items that carry personal criminal liability',                                 'overdue_now',  355, 'stock', 'personal_liability',     1, 0, 9),
     ('od_never_touched',            @odUntouched,     N'overdue items where no action has ever been recorded',                                 'overdue_now',  360, 'stock', 'operational_continuity', 2, 0, 12),
     ('od_no_owner',                 @odNoOwner,       N'overdue items with no one assigned to do them',                                        'overdue_now',  370, 'stock', 'operational_continuity', 2, 0, 13),
    /* -- rest of this month (today to month end): forward ------------------- */
     ('rm_due',                      @rmDue,           N'obligations fall due between today and the end of the month',                          'rest_of_month',400, 'curr',  'volume',                 4, 0, NULL),
     ('rm_liability',                @rmLiab,          N'of those carry personal criminal liability for the responsible officer',               'rest_of_month',410, 'curr',  'personal_liability',     1, 0, 4),
     ('rm_critical',                 @rmCrit,          N'of those are rated critical',                                                          'rest_of_month',420, 'curr',  'operational_continuity', 2, 0, NULL),
     ('rm_no_owner',                 @rmNoOwner,       N'of those have no one assigned to do them',                                             'rest_of_month',430, 'curr',  'operational_continuity', 2, 0, 6),
     ('rm_already_closed',           @rmClosedEarly,   N'of those are already closed ahead of their due date',                                  'rest_of_month',440, 'curr',  'performance',            3, 0, NULL),
    /* -- licences ------------------------------------------------------------- */
     ('lic_total',                   @licTotal,        N'licences tracked in your scope',                                                       'licences',     500, 'ctx',   'volume',                 5, 0, NULL),
     ('lic_expiring_rest_of_month',  @licLapsingRom,   N'licences expire between today and the end of the month',                               'licences',     510, 'curr',  'licence_continuity',     2, 0, NULL),
     ('lic_expiring_unrenewed',      @licLapsingUnrenewed, N'of those have no renewal filed',                                                  'licences',     520, 'curr',  'licence_continuity',     1, 0, 3),
     ('lic_lapsed_last_month',       @licLapsedLastMonth,  N'licences expired during last month',                                              'licences',     530, 'prev',  'licence_continuity',     2, 1, NULL),
     ('lic_lapsed_this_month',       @licLapsedThisMonth,  N'licences have expired so far this month',                                         'licences',     535, 'curr',  'licence_continuity',     2, 0, NULL),
     ('lic_expired_unrenewed',       @licLapsedUnrenewed,  N'licences are expired today with no renewal in progress',                          'licences',     540, 'stock', 'licence_continuity',     1, 0, 10),
    /* -- estate context ------------------------------------------------------- */
     ('obligations_in_scope',        @obligations,     N'obligations in your scope',                                                            'estate',       900, 'ctx',   'volume',                 5, 0, NULL),
     ('locations_in_scope',          @locationsInScope,N'locations in your scope',                                                              'estate',       910, 'ctx',   'volume',                 5, 0, NULL),
     ('locations_with_obligations',  @locationsWithObl,N'of those locations have obligations configured',                                      'estate',       920, 'ctx',   'volume',                 5, 0, NULL);

    /*  The rate - PREVIOUS MONTH ONLY, and only when there is a denominator.
        "0% on time" and "nothing completed" are different findings.         */
    IF @lmOnTimePct IS NOT NULL
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('lm_on_time_pct', @lmOnTimePct,
                N'per cent of last month''s completed items were closed on time (rounded down)',
                'last_month', 105, 'prev', 'performance', 3, 1, 14);

    /*  Aggregate-mode detectors become unnamed facts - a pattern, not a name. */
    IF @liabMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pattern_liability_locations', @liabFlagged,
                N'locations are running well above your own average overdue rate on obligations that carry personal liability',
                'overdue_now', 380, 'stock', 'personal_liability', 1, 0, NULL),
               ('pattern_liability_locations_of', @liabMembers,
                N'locations hold obligations that carry personal liability (the population the line above is measured against)',
                'overdue_now', 381, 'ctx', 'volume', 5, 0, NULL);

    IF @catMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pattern_categories', @catFlagged,
                N'compliance categories are running well above your own average overdue rate',
                'overdue_now', 390, 'stock', 'operational_continuity', 3, 0, NULL),
               ('pattern_categories_of', @catMembers,
                N'compliance categories were compared',
                'overdue_now', 391, 'ctx', 'volume', 5, 0, NULL);

    /*  THE HEADLINE - decided here, not by the LLM. First non-zero fact in
        HeadlineRank order.

        [DESIGN] THIS PERIOD LEADS; ALL-TIME STOCK SUPPORTS.
        Stock facts (od_*, lic_expired_unrenewed) count everything overdue or
        expired, however old - on one tenant 1,228 licences sat expired with no
        visible action (sql/01). A number like that barely moves month to
        month, so a stock-led headline reads the same every month and the
        reader learns to skip it - the "numbers thrown at the customer" failure
        this product exists to fix. So the lead is what is NEW in this edition's
        window, and the stock figure follows it as scale ("...12 such items are
        overdue in total"). Stock still leads when nothing period-scoped is
        non-zero.

           1 lm_open_liability       last month's liability-bearing items, still open
           2 tm_open_liability       this month's liability-bearing items, already past due
           3 lic_expiring_unrenewed  expires before month end, no renewal filed
           4 rm_liability            liability-bearing work due before month end
           5 lm_open_never_touched   last month's items never started
           6 rm_no_owner             due before month end, nobody assigned
           7 lm_still_open           last month's items still open
           8 tm_open_past_due        slipped already this month
           9 od_liability            STOCK - overdue with criminal liability, any age
          10 lic_expired_unrenewed   STOCK - expired, no renewal in progress
          11 od_over_90_days         STOCK - aged backlog
          12 od_never_touched        STOCK - overdue, never started
          13 od_no_owner             STOCK - overdue, nobody assigned
          14 lm_on_time_pct          nothing above is non-zero: lead with outcome
        Within a band, present failure outranks future risk. Falls back to
        rm_due so an email with nothing wrong still leads with something true. */
    ;WITH pick AS (
        SELECT TOP 1 FactKey
        FROM #facts
        WHERE HeadlineRank IS NOT NULL AND FactValue > 0
        ORDER BY HeadlineRank ASC
    )
    UPDATE f SET IsHeadline = 1
    FROM #facts f
    JOIN pick p ON p.FactKey = f.FactKey;

    IF NOT EXISTS (SELECT 1 FROM #facts WHERE IsHeadline = 1)
        UPDATE #facts SET IsHeadline = 1 WHERE FactKey = 'rm_due';

    /*===================================================================
      7. NAMED FINDINGS - max 2, deterministic
    ===================================================================*/
    /*  Shared candidate shape - identical in every monthly slot proc, so C#
        reads one contract. Up to 5 per detector (Sec.4: top 5 by
        materiality); DefaultSlot marks the 2 the email names by default.
        Re-picking the 2 is a C# change, never a redeploy.                   */
    IF OBJECT_ID('tempdb..#cand') IS NOT NULL DROP TABLE #cand;
    CREATE TABLE #cand (
        Detector        VARCHAR(40)   NOT NULL,
        Priority        TINYINT       NOT NULL,   -- lower leads
        SeverityTier    TINYINT       NOT NULL,
        RankInDetector  INT           NOT NULL,   -- 1 = most material member of this detector
        EntityKind      VARCHAR(20)   NOT NULL,
        EntityId        BIGINT        NULL,
        EntityLabel     NVARCHAR(MAX) NULL,       -- NULL = not nameable: describe, never name
        ContextKind     VARCHAR(20)   NULL,
        ContextLabel    NVARCHAR(MAX) NULL,
        Metric          VARCHAR(40)   NOT NULL,
        MetricPct       INT           NULL,       -- floored
        TenantPct       INT           NULL,       -- floored; the tenant's own figure
        ItemCount       INT           NULL,       -- ITEMS - same unit as the item facts
        BaseCount       INT           NULL,
        EventDate       DATE          NULL,
        AsAtRequired    BIT           NOT NULL DEFAULT 0,
        ProblemCount    INT           NOT NULL,   -- members with the same problem
        PopulationCount INT           NOT NULL,   -- members measured
        DefaultSlot     TINYINT       NULL        -- 1 / 2 = named by default; NULL = available, not shown
    );

    /*  Examples for aggregate-mode patterns (grid #6). Shared shape - byte-identical in sql/36,
        38, 39, 40, 41; sql/37 fills it for the four shared detectors.                          */
    IF OBJECT_ID('tempdb..#eg') IS NOT NULL DROP TABLE #eg;
    CREATE TABLE #eg (
        Detector VARCHAR(40) NOT NULL, PatternFactKey VARCHAR(40) NOT NULL, ExampleRank INT NOT NULL,
        EntityKind VARCHAR(20) NOT NULL, EntityId BIGINT NULL, EntityLabel NVARCHAR(MAX) NOT NULL,
        ContextKind VARCHAR(20) NULL, ContextLabel NVARCHAR(MAX) NULL,
        ItemCount INT NULL, BaseCount INT NULL, UnitLabel NVARCHAR(200) NOT NULL);

    /*  EXAMPLES for the two overview patterns (2026-09-23 design). Same order as individual
        mode, counts only. category figures count OBLIGATIONS (each once), never due dates -
        the UnitLabel says so. An unnamed category is never an example.                     */
    IF @liabMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples) 'liability_overdue_location', 'pattern_liability_locations',
               ROW_NUMBER() OVER (ORDER BY ll.LiabOverdueItems DESC, ll.LiabOverdueRate DESC, ll.BranchID ASC),
               'location', ll.BranchID, ll.BranchName, ll.LiabOverdueItems, NULL,
               N'overdue obligations at this location carry personal criminal liability'
        FROM #liabLoc ll
        WHERE ll.IsFlagged = 1 AND ll.BranchName IS NOT NULL
        ORDER BY ll.LiabOverdueItems DESC, ll.LiabOverdueRate DESC, ll.BranchID ASC;

    IF @catMode = 'aggregate'
        INSERT #eg (Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel, ItemCount, BaseCount, UnitLabel)
        SELECT TOP (@MaxExamples) 'category_overdue_skew', 'pattern_categories',
               ROW_NUMBER() OVER (ORDER BY c.OverdueInst DESC, c.OverdueRate DESC, c.CategoryId ASC),
               'category', c.CategoryId, c.CategoryName, c.OverdueInst, c.Inst,
               N'of the obligations in this category are overdue (obligations, each counted once)'
        FROM #cat c
        WHERE c.IsFlagged = 1 AND c.CategoryName IS NOT NULL
        ORDER BY c.OverdueInst DESC, c.OverdueRate DESC, c.CategoryId ASC;

    /*  P1 - licences expiring before month end with no renewal filed, soonest
        first. A DATED EVENT, not a pattern: the 2-name cap bounds it, the
        emission policy does not apply. Unnamed types are left to the count. */
    IF @licLapsingUnrenewed > 0
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      ContextKind, ContextLabel, Metric, EventDate, ProblemCount, PopulationCount)
        SELECT TOP 5 'licence_expiring_unrenewed', 1, 1,
               ROW_NUMBER() OVER (ORDER BY l.EndDate ASC, l.LicenseId ASC),
               'licence', l.LicenseId, l.LicenseTypeName,
               'location', cb.Name, 'expires_on', CAST(l.EndDate AS DATE),
               @licLapsingUnrenewed, @licLapsingRom
        FROM #lic l
        JOIN CustomerBranch cb ON cb.ID = l.BranchID
        WHERE l.LapsesRestOfMonth = 1 AND l.RenewalInProgress = 0
          AND l.LicenseTypeName IS NOT NULL
        ORDER BY l.EndDate ASC, l.LicenseId ASC;

    /*  P2 - flagged locations by overdue liability-bearing ITEMS (same unit
        as od_liability, so "{{NAME}} holds 6 of them" is true). Ordered by
        that count, so "the most among ProblemCount" is computed.           */
    IF @liabMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, ItemCount, ProblemCount, PopulationCount)
        SELECT TOP 5 'liability_overdue_location', 2, 1,
               ROW_NUMBER() OVER (ORDER BY ll.LiabOverdueItems DESC, ll.LiabOverdueRate DESC, ll.BranchID ASC),
               'location', ll.BranchID, ll.BranchName,
               'liability_overdue_items', ll.LiabOverdueItems,
               @liabFlagged, @liabMembers
        FROM #liabLoc ll
        WHERE ll.IsFlagged = 1
        ORDER BY ll.LiabOverdueItems DESC, ll.LiabOverdueRate DESC, ll.BranchID ASC;

    /*  P3 - categories most over-represented in overdue work. MetricPct /
        TenantPct are OBLIGATION-level overdue rates (floored) - a different
        unit from the item facts; never relate them in one sentence.        */
    IF @catMode = 'individual'
        INSERT #cand (Detector, Priority, SeverityTier, RankInDetector, EntityKind, EntityId, EntityLabel,
                      Metric, MetricPct, TenantPct, ProblemCount, PopulationCount)
        SELECT TOP 5 'category_overdue_skew', 3, 3,
               ROW_NUMBER() OVER (ORDER BY c.OverdueInst DESC, c.OverdueRate DESC, c.CategoryId ASC),
               'category', c.CategoryId, c.CategoryName,
               'obligation_overdue_rate_pct',
               CAST(FLOOR(100.0 * c.OverdueRate) AS INT),
               CAST(FLOOR(100.0 * @tenantOverdueRate) AS INT),
               @catFlagged, @catMembers
        FROM #cat c
        WHERE c.IsFlagged = 1
        ORDER BY c.OverdueInst DESC, c.OverdueRate DESC, c.CategoryId ASC;

    /*  DEFAULT SLOTS - the rank-1 row of the best detector, then the rank-1
        row of the next detector that is not the same entity. Identical block
        in every slot proc.                                                  */
    ;WITH s1 AS (
        SELECT TOP 1 * FROM #cand
        WHERE RankInDetector = 1
        ORDER BY Priority, Detector
    )
    UPDATE s1 SET DefaultSlot = 1;

    ;WITH s2 AS (
        SELECT TOP 1 c.* FROM #cand c
        WHERE c.RankInDetector = 1 AND c.DefaultSlot IS NULL
          AND NOT EXISTS (SELECT 1 FROM #cand f
                          WHERE f.DefaultSlot = 1
                            AND f.EntityKind = c.EntityKind
                            AND f.EntityId = c.EntityId)
        ORDER BY c.Priority, c.Detector
    )
    UPDATE s2 SET DefaultSlot = 2;

    /*  EXAMPLES CONTRACT (2026-09-23 design, Sec.2.7). An example exists only in aggregate
        mode, beside the pattern fact it illustrates. This file's x1-x4 codes are taken, so
        the next free ones in its block are used - the header records the deviation.      */
    IF EXISTS (SELECT 1 FROM #eg e JOIN #cand c ON c.Detector = e.Detector)
        THROW 51255, N'EXAMPLES CONTRACT VIOLATED - a free monthly overview detector emitted both named candidates and examples. Examples exist only in aggregate mode. Refusing to emit.', 1;
    IF EXISTS (SELECT 1 FROM #eg e LEFT JOIN #facts f ON f.FactKey = e.PatternFactKey WHERE f.FactKey IS NULL)
        THROW 51256, N'EXAMPLES CONTRACT VIOLATED - a free monthly overview example refers to a pattern fact that was not emitted. Refusing to emit.', 1;

    /*===================================================================
      8. EMIT - six result sets, in contract order (examples last, 2026-09-23)
    ===================================================================*/
    SELECT
        'control_totals'                          AS ResultSet,
        @CustomerID                               AS CustomerID,
        @UserID                                   AS UserID,
        'overview'                                AS Slot,
        CAST(@PrevStart AS DATE)                  AS PrevMonthStart,
        CAST(DATEADD(DAY, -1, @CurrStart) AS DATE) AS PrevMonthEnd,
        CAST(@CurrStart AS DATE)                  AS CurrMonthStart,
        CAST(DATEADD(DAY, -1, @NextStart) AS DATE) AS CurrMonthEnd,
        @AsOf                                     AS AsOf,
        @obligations                              AS ScopedInstances,
        (SELECT COUNT(*) FROM #sched)             AS SchedulesLoaded,
        (SELECT SUM(Due) FROM #wp)                AS WindowSchedules,
        @odTotal                                  AS OverdueStock,
        @licTotal                                 AS ScopedLicences,
        @locationsInScope                         AS LocationsInScope,
        @locationsWithObl                         AS LocationsWithObligations,
        CAST(1 AS BIT)                            AS Reconciled,
        @catDegraded                              AS DegradedPeerSample,
        'fact'                                    AS HeadlineSource;   /* overview always leads with a fact; candidates support */

    SELECT 'facts' AS ResultSet,
           FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope,
           ImpactClass, SeverityTier, AsAtRequired, IsHeadline
    FROM #facts
    ORDER BY DisplayOrder;

    SELECT 'detector_policy' AS ResultSet, Detector, Eligible, Flagged, FlaggedPct, EmitMode, Note
    FROM #detector;

    SELECT 'candidates' AS ResultSet, DefaultSlot, Detector, Priority, SeverityTier, RankInDetector,
           EntityKind, EntityId, EntityLabel, ContextKind, ContextLabel, Metric, MetricPct, TenantPct,
           ItemCount, BaseCount, EventDate, AsAtRequired, ProblemCount, PopulationCount,
           ProblemCount - 1 AS ResidualCount
    FROM #cand
    ORDER BY CASE WHEN DefaultSlot IS NULL THEN 9 ELSE DefaultSlot END, Priority, Detector, RankInDetector;

    /*  data_quality - declared, never silent (non-negotiable #2). A zero
        count is emitted too, so "checked and clean" is distinguishable
        from "not checked".                                                  */
    SELECT 'data_quality' AS ResultSet, Code, ItemCount, Detail
    FROM (VALUES
        ('prev_month_not_settled', @lmDue,
         N'Last month is reported as at the run date. Late closures can still arrive, so these counts can move; every previous-month figure must be stated "as at" the run date.'),
        ('completed_without_timeliness', (SELECT ISNULL(SUM(Untimed), 0) FROM #wp),
         N'Completed items whose status carries no on-time/late classification. Expected 0. Counted in the completed total, never in on-time.'),
        ('unrated_risk_type', (SELECT COUNT(*) FROM #inst WHERE RiskType IS NOT NULL AND RiskClass IS NULL),
         N'Obligations whose RiskType value is absent from the dictionary. Not counted as critical (the conservative direction).'),
        ('schedules_no_owner_anywhere', (SELECT COUNT(*) FROM #sched WHERE PerformerSource = 'none'),
         N'Schedules with no performer on the schedule AND none on the obligation. Ownership is read schedule-first (99.8% populated), then instance-level.'),
        ('licences_without_end_date', @licNoEnd,
         N'Licences with no EndDate cannot be assessed for lapse and are excluded from every licence fact.'),
        ('licence_scope_branch_only', CASE WHEN @licTotal > 0 THEN 1 ELSE 0 END,
         N'Licences are scoped by authorised branch only (no category link without Lic_tbl_LicenseComplianceInstanceMapping) - a narrower guarantee than the 2-D obligation scope.'),
        ('category_peer_sample_degraded', CAST(@catDegraded AS INT),
         N'Fewer than 2 categories met the materiality floor; the floor was widened to 1 and the comparison is declared degraded.'),
        ('unnamed_category', (SELECT COUNT(*) FROM #cat WHERE CategoryName IS NULL),
         N'Categories with no name in ComplianceCategory. Never named in the email; counted only.')
    ) AS dq(Code, ItemCount, Detail);

    /*  Grid #6 - APPENDED LAST, never before data_quality (a reader not yet updated would
        map example rows onto its data_quality shape). Empty is normal.                     */
    SELECT 'examples' AS ResultSet, Detector, PatternFactKey, ExampleRank, EntityKind, EntityId, EntityLabel,
           ContextKind, ContextLabel, ItemCount, BaseCount, UnitLabel
    FROM #eg
    ORDER BY PatternFactKey, ExampleRank;
END
GO
PRINT 'usp_Insights_FreeMonthly_Overview restored.';
GO

/* Re-apply the saved grants (GRANT / DENY exactly as they were). */
DECLARE @sql NVARCHAR(MAX) =
    (SELECT STRING_AGG(
        CASE WHEN StateDesc = 'GRANT_WITH_GRANT_OPTION'
             THEN 'GRANT ' + PermissionName + ' ON dbo.' + QUOTENAME(ProcName) + ' TO ' + QUOTENAME(Grantee) + ' WITH GRANT OPTION;'
             ELSE CASE WHEN StateDesc = 'DENY' THEN 'DENY ' ELSE 'GRANT ' END
                  + PermissionName + ' ON dbo.' + QUOTENAME(ProcName) + ' TO ' + QUOTENAME(Grantee) + ';'
        END, ' ')
     FROM #rb_perms);
IF @sql IS NOT NULL EXEC sys.sp_executesql @sql;

DECLARE @missing INT =
    (SELECT COUNT(*) FROM #rb_perms r
     WHERE NOT EXISTS (SELECT 1 FROM sys.database_permissions p
                       JOIN sys.objects o ON o.object_id = p.major_id
                       WHERE o.name = r.ProcName AND p.permission_name = r.PermissionName
                         AND p.state_desc = r.StateDesc AND USER_NAME(p.grantee_principal_id) = r.Grantee));
IF @missing > 0
    THROW 51187, N'ROLLBACK PERMISSIONS FAILED - one or more saved grants could not be re-applied. Re-grant EXECUTE / VIEW DEFINITION manually and send this to the author.', 1;
PRINT 'Permissions re-applied: ' + CAST((SELECT COUNT(*) FROM #rb_perms) AS VARCHAR(10)) + '.';
DROP TABLE #rb_perms;
GO
/* Verify: every definition must hash to the 2026-10-06 snapshot. */
DECLARE @expected TABLE (Name SYSNAME PRIMARY KEY, Sha256 VARCHAR(66) NOT NULL);
INSERT @expected (Name, Sha256) VALUES
 ('usp_Insights_FreeMonthly_Act', '0x719815AD557406A2F70F25AEE71563402D778B59318EF0FE72F947277CC1AE02'),
 ('usp_Insights_FreeMonthly_Licence', '0x1E29CDC67C98FBD9C70D938FA20CE893173A2F58406F125532275BD4F97737A1'),
 ('usp_Insights_FreeMonthly_LoadFacts', '0xA19C2CE92FAD444A1794D146FB213E34F34A3547355074763223BD09BA62EFEE'),
 ('usp_Insights_FreeMonthly_LoadLicences', '0x48050163481848CCE6F2E8B0D60B628A397C18DD4C0A7ED578B93854F84BB363'),
 ('usp_Insights_FreeMonthly_Location', '0x42CFD4F46836C36E69F81E2981FBB49F75D2FE886220C3C36D180125E893AF43'),
 ('usp_Insights_FreeMonthly_MemberDetectors', '0xEC9CEEFDEAA5E74295F881C0C08E4FF279411B925A655506CE7729A2FD0234C9'),
 ('usp_Insights_FreeMonthly_Overview', '0x5B9B777CDE6FAA1175DCD293BEFABB81169F4DABD898368E760BCB6DFA864AB2'),
 ('usp_Insights_FreeMonthly_Users', '0x5B703FA59A8F90D3330489DF2DDBF9A00121F8D1810DE9A3D289EBE7F626A830');
DECLARE @bad NVARCHAR(1000) =
    (SELECT STRING_AGG(e.Name, ', ')
     FROM @expected e
     LEFT JOIN sys.sql_modules m ON m.object_id = OBJECT_ID('dbo.' + e.Name)
     WHERE m.object_id IS NULL
        OR CONVERT(VARCHAR(66), HASHBYTES('SHA2_256', CAST(m.definition AS VARBINARY(MAX))), 1) <> e.Sha256);
IF @bad IS NOT NULL
BEGIN
    DECLARE @msg NVARCHAR(2048) = N'ROLLBACK VERIFY FAILED - these procedures do not match the 2026-10-06 snapshot: ' + @bad + N'. Send this to the author.';
    THROW 51186, @msg, 1;
END
PRINT 'ROLLBACK OK - all 8 free-tier procedures restored and verified.';
GO
