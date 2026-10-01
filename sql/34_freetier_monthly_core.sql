/*===========================================================================
  RegTrack Insights - Free tier, MONTHLY edition
  Error block 51230-51239.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.
  (Window-input contract failures are placed in x5-x7: the convention has no
  dedicated class for a malformed caller input, and x5-x9 is the nearest -
  "the system was handed something it cannot interpret". Declared here so an
  operator is not surprised.)

  Codes in use: 51230 scope | 51231-51233 structural invariants |
  51234 dashboard-overdue set not seeded | 51235-51237 window input |
  51238-51239 dictionary.
  (51234 REASSIGNED 2026-09-29: it was "malformed @AllowedBranches". That
   parameter no longer exists, so the old condition cannot recur.)

  SHARED FACT LOADER for every monthly free-tier slot proc
  (sql/36 Overview, sql/38 Users, sql/39 Location, sql/40 Act; sql/37 = the
  shared member detectors those three call).
  Licence facts come from a sibling loader: sql/35 (own block 51240-51249).

  Spec reference : docs/superpowers/specs/2026-09-18-monthly-free-tier-digest-design.md
  STATUS         : PROPOSED. Written against the schema and conventions in this
                   repo; NOT yet executed against any database. Handed to Vinay
                   for review and UAT install. Validation queries: see
                   sql/42_freetier_monthly_validation.sql. Until those pass on
                   >= 5 differing tenant profiles (CLAUDE.md Sec.10/Sec.11), treat
                   every statement below as unverified.

  -- WHY A HELPER PROCEDURE AND NOT AN INLINE TVF ---------------------------
  The measured fix for windowed queries in this codebase (sql/23, 183,502 ms
  -> 157 ms) is: materialise the scoped instances into an INDEXED temp table
  FIRST, then reach into ComplianceScheduleOn / ComplianceTransaction from it.
  An inline TVF cannot materialise, so it cannot do scope-first. A helper
  procedure can - and it gives every slot proc ONE estate definition, so the
  Overview and the Users email can never disagree about what "in scope",
  "overdue" or "completed" means (CLAUDE.md Sec.3 "same estate definition").

  -- CONTRACT: THE CALLER CREATES THE TABLES, THIS PROC FILLS THEM -----------
  This procedure RETURNS NO RESULT SET (CLAUDE.md Sec.10 - a helper that
  emits a grid shifts every caller's result-set contract by one). Success is
  silence; failure is a THROW. Before calling, the caller MUST create exactly:

    CREATE TABLE #inst (
        ComplianceInstanceID BIGINT        NOT NULL PRIMARY KEY,
        BranchID             INT           NOT NULL,
        CategoryId           INT           NULL,
        ActID                INT           NULL,
        ComplianceID         BIGINT        NOT NULL,
        DepartmentID         BIGINT        NULL,
        Imprisonment         BIT           NOT NULL,
        RiskType             INT           NULL,
        RiskClass            NVARCHAR(200)  NULL   -- dictionary meaning, NULL = unrated
    );
    CREATE TABLE #sched (
        ComplianceScheduleOnID BIGINT       NOT NULL PRIMARY KEY,
        ComplianceInstanceID   BIGINT       NOT NULL,
        BranchID               INT          NOT NULL,
        ScheduleOn             DATETIME     NOT NULL,
        WindowPart             VARCHAR(14)  NULL,   -- see WINDOW below; NULL = older overdue stock
        PerformerID            BIGINT       NULL,
        PerformerSource        VARCHAR(10)  NOT NULL, -- schedule | instance | none
        ReviewerID             BIGINT       NULL,
        ReviewerSource         VARCHAR(10)  NOT NULL, -- schedule | instance | none
        NeverTouched           BIT          NOT NULL,
        Outcome                VARCHAR(20)  NOT NULL, -- see OUTCOME below
        IsOverdue              BIT          NOT NULL,
        DaysPastDue            INT          NULL,     -- NULL unless IsOverdue = 1
        AgeBand                VARCHAR(10)  NULL      -- NULL unless IsOverdue = 1
    );

  A missing table or column fails loudly at the INSERT ("Invalid object name" /
  "Invalid column name") - never silently.

  -- WINDOW -----------------------------------------------------------------
  The caller passes @CurrMonthStart (a DATE, the 1st of the edition's month,
  computed in C# from the edition - never from GETDATE() here) and @AsOf (the
  run instant, in the SAME clock as ScheduleOn). The previous month is derived
  by arithmetic, so "previous month != month before current" cannot happen.

      prev_month      [PrevMonthStart, CurrMonthStart)   closed month
      curr_elapsed    [CurrMonthStart, AsOf]             this month so far
      curr_remaining  (AsOf, NextMonthStart)             rest of this month

  [TRAP - a clock mismatch is caught, not absorbed] Sunday 1 February 00:30
  IST is Saturday 31 January 19:00 UTC. If the caller passes a UTC @AsOf with
  an IST-derived @CurrMonthStart, @AsOf falls BEFORE the month and 51237
  fires. That is deliberate: the alternative is a February edition silently
  computed as January.

  -- WHAT #sched HOLDS - ONE READ, ONE INSTANT -------------------------------
  Every row is either IN THE WINDOW (any outcome) or OVERDUE STOCK as at @AsOf
  (any due date, however old). Both come from ONE statement with ONE latest-
  status resolution, so the window facts and the overdue stock are captured
  in the same instant (non-negotiable #3). Two separate reads could disagree
  about a schedule closed between them.

  -- REGTRACK 2.0 PARITY (2026-09-29) -----------------------------------------
  Product-owner decision: every free-digest number must equal what the RegTrack
  2.0 management dashboard shows the same user for the same tenant, so testing
  can reconcile the two screen for screen. Replicated from the UAT source of
  SP_GetManagementDashboardGraphCounts_Statutory (MGMT path) and
  SP_GetEntitySummary (MGMT path), read 2026-09-29:

    an obligation counts only when
      - its Act is not deleted                       (A.IsDeleted = 0)
      - its Compliance is visible                    (ComplinceVisible = 1 or NULL)
      - its Compliance is not ComplianceType 1       (C.ComplianceType <> 1)
      - it has a PERFORMER assignment (RoleID 3) held by an active, undeleted
        user of THIS tenant                          (EXISTS ComplianceAssignment)
    a schedule counts only when it has at least one transaction
    OVERDUE = latest status in the dashboard's overdue set AND the due DATE is
      before the as-at DATE. Due today is not overdue. Pending review, rejected
      and in progress are not overdue. The set lives, by status id, in
      dbo.InsightsFreeDashboardStatusRule (RuleName 'overdue') - created and
      seeded by THIS file so no proc carries a status literal (CLAUDE.md
      non-negotiable 4) and the shared dictionary (sql/01) is untouched.

  The paid tier (sql/03 tvfInsightsScopedInstances, tvfInsightsOverdueSchedules)
  is NOT changed - these filters are applied here, on top of it.

  [SUPERSEDED FOR THE FREE TIER ONLY] Two rulings recorded elsewhere in this
  repo do not hold here any more, because the dashboard does not apply them:
    - "a past-due schedule with no transaction is overdue" (BA ruling) - the
      dashboard drops a schedule with no transaction, so this loader does too.
      NeverTouched is therefore always 0; the column stays for the contract.
    - the 181x ownership note (CLAUDE.md Sec.5) - an obligation with no RoleID 3
      assignment is not counted at all, exactly as on the dashboard. Schedule-
      level Performerid still decides WHO the performer is (below).
  Soft-deleted ComplianceInstance rows are KEPT, as the dashboard's MGMT path
  keeps them (it has no CI.IsDeleted filter). That is why this loader selects
  its instances itself instead of reading dbo.tvfInsightsScopedInstances
  (sql/03, shared with the paid tier, which drops them - unchanged).

  -- OUTCOME (latest status, via the dictionary - never a status literal) ----
      completed_on_time   ClosureClass completed, Timeliness on_time
      completed_late      ClosureClass completed, Timeliness delayed
      completed_untimed   ClosureClass completed, Timeliness NULL (expected 0; declared)
      [PARITY] For the statuses in rule 'timed_by_close_date' (the two
      "Approved" ids) the dashboard ignores the status id's timeliness and
      compares the transaction's StatusChangedOn with the due date: on or
      before -> on time, after -> late, no close date -> neither (untimed).
      resolved_terminal   ClosureClass resolved_terminal - closed by the REVIEWER
                          WITHOUT completion. Covers BOTH "not applicable" and
                          "not complied" finals. NEVER label it "not applicable"
                          alone - one of its members is a confirmed miss.
      open                ClosureClass open
  A status the dictionary cannot place THROWs 51238. Never guessed.

  -- OWNERSHIP: SCHEDULE FIRST, INSTANCE SECOND ------------------------------
  [TRAP - the 181x defect] ComplianceScheduleOn.Performerid is populated on
  99.8% of schedules; ComplianceAssignment (RoleID 3) is the instance-level
  mechanism. Reading ComplianceAssignment alone overstated "ownerless" 181x on
  one tenant (44,480 reported vs 245 real). Performer and reviewer here are
  resolved from the SCHEDULE first and fall back to the instance assignment
  only when the schedule carries none. PerformerSource records which, so a
  caller can report "no owner anywhere" honestly.
  Fallback picks MIN(UserID) when an instance has several assignees - a
  deterministic choice for the 0.2% of schedules that need it.

  -- RISK CLASS VIA THE DICTIONARY ------------------------------------------
  RiskType is resolved through InsightsEnumPolarity (Semantic = 'RiskType'),
  the BA-verified mapping (3 = Critical, 0 = High - NOT intuitive). No proc in
  the monthly tier compares RiskType to a literal. A non-NULL RiskType absent
  from the dictionary yields RiskClass NULL - it is then NOT counted as
  critical (the conservative direction for a free email) and the caller
  declares the count in data_quality.

  -- SCOPE --------------------------------------------------------------------
  dbo.tvfInsightsScopePairs for the representative user of the recipient
  group (2-D, branch x category, EntitiesAssignment - the same scope source the
  RegTrack dashboard's MGMT path reads), then the parity filters above. Every
  recipient in a group shares this exact scope by construction - groups are
  keyed on the scope-pair signature (ResolveDigestRecipientsActivity). So any
  entity a slot proc names is one every recipient of that email can already
  access in RegTrack.
  @UserID is REQUIRED. sql/06's tenant-wide NULL branch is not reproduced:
  the orchestrator always passes a real representative user, and a NULL here
  is a caller defect, not a mode.

  [REMOVED 2026-09-29] The @AllowedBranches entitlement filter (RegTrack
  show-entitlements API, added 2026-09-27) is gone on the product owner's
  instruction, with dbo.tvfInsightsEntitledScopePairs. This file DROPS that
  function if an earlier deployment created it.

  IDEMPOTENT. PURE ASCII (CLAUDE.md Sec.5a). Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

/*  [RETIRED 2026-09-29] The entitlement-filter function is no longer used by
    any proc. Dropped here so a database that received the 2026-09-27 revision
    is cleaned up by redeploying this file.                                  */
IF OBJECT_ID('dbo.tvfInsightsEntitledScopePairs', 'IF') IS NOT NULL
    DROP FUNCTION dbo.tvfInsightsEntitledScopePairs;
GO

/*---------------------------------------------------------------------------
  RegTrack 2.0 dashboard STATUS RULES (2026-09-29), by ComplianceStatus id.
  Source: SP_GetManagementDashboardGraphCounts_Statutory (UAT, read 2026-09-29).
    overdue              IsOverdue: these latest statuses, due date before
                         today. NOT overdue there: pending review 2,3,11,16,18 /
                         rejected 6,8 / in progress 10 / closed and final.
    timed_by_close_date  the two "Approved" ids: on time vs late is decided by
                         StatusChangedOn vs the due date, not by the id.
  LICENCE rules (read by sql/35), by Lic_tbl_StatusMaster id. Source:
  SP_LicenseInstanceTransactionCount (UAT), MGRStatus: Active / Expiring /
  Expired are the latest status names. Ids per sql/01's verified seed notes
  (2 Active, 4 Expiring, 3 Expired - each an exact-unique name).
    lic_active / lic_expiring / lic_expired
  Re-seeded on every deploy so the table always equals this list.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.InsightsFreeDashboardStatusRule', 'U') IS NULL
    CREATE TABLE dbo.InsightsFreeDashboardStatusRule (
        RuleName  VARCHAR(30)   NOT NULL,
        StatusId  INT           NOT NULL,               -- ComplianceStatus.ID
        Note      NVARCHAR(200) NOT NULL,
        CONSTRAINT PK_InsightsFreeDashboardStatusRule PRIMARY KEY (RuleName, StatusId)
    );
GO

DELETE FROM dbo.InsightsFreeDashboardStatusRule;
INSERT dbo.InsightsFreeDashboardStatusRule (RuleName, StatusId, Note)
VALUES ('overdue', 1,  N'Open'),
       ('overdue', 12, N'Submitted For Interim Review'),
       ('overdue', 13, N'Interim Review Approved - interim is not final'),
       ('overdue', 14, N'Interim Rejected'),
       ('overdue', 21, N'Deviation Applied'),
       ('overdue', 22, N'Deviation Rejected'),
       ('overdue', 23, N'Deviation Approved - still open'),
       ('timed_by_close_date', 7, N'Approved (closed before due date by id)'),
       ('timed_by_close_date', 9, N'Approved (closed after due date by id)'),
       ('lic_active',   2, N'Licence status Active (Lic_tbl_StatusMaster)'),
       ('lic_expiring', 4, N'Licence status Expiring (Lic_tbl_StatusMaster)'),
       ('lic_expired',  3, N'Licence status Expired (Lic_tbl_StatusMaster)');
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_LoadFacts', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeMonthly_LoadFacts;
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

PRINT 'Free monthly fact loader installed (usp_Insights_FreeMonthly_LoadFacts).';
GO
