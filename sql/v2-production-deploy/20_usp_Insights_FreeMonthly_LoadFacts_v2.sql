/*===========================================================================
  RegTrack Insights - v2 (2026-09-29): follow RegTrack's own Detailed Report
  Object : dbo.usp_Insights_FreeMonthly_LoadFacts
  Base   : the definition DEPLOYED on UAT on 2026-09-29 (not the repo copy)
  Change : overdue = latest status Open (dictionary v2) and due before today; no transaction is no longer overdue.
  Why    : product decision 2026-09-29 - Insights must count what RegTrack's own
           Detailed Report (Kendo_DetailedReport_Pagination) counts. See
           sql/v2/README.md for the full rule list and the parity proof.
===========================================================================*/
SET NOCOUNT ON;
GO
IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_LoadFacts', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_LoadFacts;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_LoadFacts
    @UserID          INT,
    @CustomerID      INT,
    @CurrMonthStart  DATE,
    @AsOf            DATETIME,
    @AllowedBranches VARCHAR(MAX) = NULL     -- entitlement filter, see header; NULL = unfiltered
AS
BEGIN
    SET NOCOUNT ON;

    /*===================================================================
      0. INPUT CONTRACT - fail closed before reading anything
    ===================================================================*/
    IF @UserID IS NULL OR @CustomerID IS NULL OR @CurrMonthStart IS NULL OR @AsOf IS NULL
        THROW 51235, N'FREE MONTHLY - WINDOW INPUT MISSING: @UserID, @CustomerID, @CurrMonthStart and @AsOf are all required. The caller resolves the edition and passes concrete values. Refusing to compute.', 1;

    /*  Digits and commas only, no empty element, no id longer than 10 digits
        (INT overflow). Checked before tvfInsightsEntitledScopePairs parses it. */
    IF @AllowedBranches IS NOT NULL
       AND (   @AllowedBranches = ''
            OR @AllowedBranches COLLATE Latin1_General_BIN2 LIKE '%[^0-9,]%'   -- BIN2: see CLAUDE.md Sec.3 on LIKE
            OR @AllowedBranches LIKE ',%'
            OR @AllowedBranches LIKE '%,'
            OR @AllowedBranches LIKE '%,,%'
            OR @AllowedBranches LIKE '%[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%')
        THROW 51234, N'FREE MONTHLY - @AllowedBranches IS MALFORMED. Expected NULL or a comma-separated list of branch ids (digits and commas only, no blanks). Refusing to compute.', 1;

    IF DATEPART(DAY, @CurrMonthStart) <> 1
        THROW 51236, N'FREE MONTHLY - @CurrMonthStart IS NOT THE FIRST OF A MONTH. It must be the 1st of the edition month, computed from the edition in C#. Refusing to compute.', 1;

    DECLARE @CurrStart      DATETIME = CAST(@CurrMonthStart AS DATETIME);
    DECLARE @PrevMonthStart DATETIME = DATEADD(MONTH, -1, @CurrStart);
    DECLARE @NextMonthStart DATETIME = DATEADD(MONTH,  1, @CurrStart);

    IF @AsOf < @CurrStart OR @AsOf >= @NextMonthStart
        THROW 51237, N'FREE MONTHLY - @AsOf FALLS OUTSIDE THE EDITION MONTH. Most likely a clock mismatch (a UTC @AsOf against a local-time month). Pass @AsOf in the same clock as ComplianceScheduleOn.ScheduleOn. Refusing to compute.', 1;

    /*===================================================================
      1. SCOPE + DICTIONARY PRE-FLIGHT
    ===================================================================*/
    /*  The entitled branches, materialised once. Every scoped read below joins
        this, so the entitlement filter is applied in exactly one place.     */
    IF OBJECT_ID('tempdb..#lf_branch') IS NOT NULL DROP TABLE #lf_branch;
    SELECT DISTINCT ep.BranchID
    INTO #lf_branch
    FROM dbo.tvfInsightsEntitledScopePairs(@UserID, @CustomerID, @AllowedBranches) ep;
    CREATE CLUSTERED INDEX IX_lf_branch ON #lf_branch (BranchID);

    IF NOT EXISTS (SELECT 1 FROM #lf_branch)
        THROW 51230, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant (after the entitlement filter, when one is passed). Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- THROWs 51001 on a dictionary gap; returns no grid

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
    ===================================================================*/
    INSERT #inst (ComplianceInstanceID, BranchID, CategoryId, ActID, ComplianceID,
                  DepartmentID, Imprisonment, RiskType, RiskClass)
    SELECT s.ComplianceInstanceID,
           s.BranchID,
           s.CategoryId,
           s.ActID,
           s.ComplianceID,
           ci.DepartmentID,
           CAST(ISNULL(s.Imprisonment, 0) AS BIT),
           s.RiskType,
           r.Meaning
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
    JOIN #lf_branch eb         ON eb.BranchID = s.BranchID     -- entitlement filter; a no-op when @AllowedBranches IS NULL
    JOIN ComplianceInstance ci ON ci.ID = s.ComplianceInstanceID
    LEFT JOIN #lf_risk r       ON r.RawValue = s.RiskType;

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
        CAST(CASE WHEN lt.ID IS NULL THEN 1 ELSE 0 END AS BIT),
        CASE WHEN lt.ID IS NULL                                               THEN 'open'
             WHEN d.StatusId IS NULL                                          THEN 'unclassified'
             WHEN d.ClosureClass = 'completed' AND d.Timeliness = 'on_time'   THEN 'completed_on_time'
             WHEN d.ClosureClass = 'completed' AND d.Timeliness = 'delayed'   THEN 'completed_late'
             WHEN d.ClosureClass = 'completed'                                THEN 'completed_untimed'
             WHEN d.ClosureClass = 'resolved_terminal'                        THEN 'resolved_terminal'
             WHEN d.ClosureClass = 'open'                                     THEN 'open'
             ELSE 'unclassified' END,
        CAST(CASE WHEN cso.ScheduleOn < CAST(@AsOf AS DATE) AND d.OverdueEligible = 1 THEN 1 ELSE 0 END AS BIT),
        CASE WHEN cso.ScheduleOn < CAST(@AsOf AS DATE) AND d.OverdueEligible = 1
             THEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) END,
        CASE WHEN cso.ScheduleOn < CAST(@AsOf AS DATE) AND d.OverdueEligible = 1
             THEN CASE WHEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) <= 30 THEN 'd000_030'
                       WHEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) <= 60 THEN 'd031_060'
                       WHEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) <= 90 THEN 'd061_090'
                       ELSE 'd091_plus' END END
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    OUTER APPLY (SELECT TOP 1 t.StatusId, t.ID
                 FROM ComplianceTransaction t
                 WHERE t.ComplianceScheduleOnID = cso.ID
                 ORDER BY t.Dated DESC, t.ID DESC) lt
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = lt.StatusId
    LEFT JOIN #lf_owner o                  ON o.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.IsActive = 1
      AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn < @NextMonthStart
      AND (    cso.ScheduleOn >= @PrevMonthStart
           OR (cso.ScheduleOn < CAST(@AsOf AS DATE) AND d.OverdueEligible = 1) );

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
        THROW 51232, N'FREE MONTHLY RECONCILIATION FAILED - a schedule due AFTER @AsOf is marked overdue. The never-touched-is-overdue ruling applies to PAST-DUE schedules only. Refusing to publish.', 1;

    IF EXISTS (SELECT 1 FROM #sched
               WHERE (IsOverdue = 1 AND (DaysPastDue IS NULL OR AgeBand IS NULL))
                  OR (IsOverdue = 0 AND (DaysPastDue IS NOT NULL OR AgeBand IS NOT NULL)))
        THROW 51233, N'FREE MONTHLY RECONCILIATION FAILED - overdue age fields are inconsistent with IsOverdue. Refusing to publish.', 1;

    DROP TABLE #lf_owner;
    DROP TABLE #lf_risk;
    DROP TABLE #lf_branch;
END
GO
PRINT 'usp_Insights_FreeMonthly_LoadFacts (v2) installed.';
GO