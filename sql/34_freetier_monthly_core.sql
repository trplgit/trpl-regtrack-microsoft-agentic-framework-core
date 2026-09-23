/*===========================================================================
  RegTrack Insights - Free tier, MONTHLY edition
  Error block 51230-51239.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.
  (Window-input contract failures are placed in x5-x7: the convention has no
  dedicated class for a malformed caller input, and x5-x9 is the nearest -
  "the system was handed something it cannot interpret". Declared here so an
  operator is not surprised.)

  Codes in use: 51230 scope | 51231-51233 structural invariants (51234 free) |
  51235-51237 window input | 51238-51239 dictionary.

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

  -- OVERDUE: THE CANONICAL PREDICATE, RESTATED VERBATIM ---------------------
  IsOverdue = ScheduleOn <= @AsOf AND (OverdueEligible = 1 OR no transaction)
  This is dbo.tvfInsightsOverdueSchedules' predicate exactly (sql/01), with
  the same estate filters as dbo.tvfInsightsLatestStatus. It is restated here
  rather than joined because a second read breaks the same-instant rule above;
  sql/24 already mirrors it the same way. sql/42 compares the two counts.

  [TRAP - the never-touched ruling is PAST-DUE only] BA RULING: "a past-due
  schedule with no transaction is to be considered overdue". A schedule due
  LATER this month with no transaction is simply not started yet. The
  "ScheduleOn <= @AsOf" guard is what keeps every future obligation from being
  labelled overdue. Invariant 51232 enforces it.

  -- OUTCOME (latest status, via the dictionary - never a status literal) ----
      completed_on_time   ClosureClass completed, Timeliness on_time
      completed_late      ClosureClass completed, Timeliness delayed
      completed_untimed   ClosureClass completed, Timeliness NULL (expected 0; declared)
      resolved_terminal   ClosureClass resolved_terminal - closed by the REVIEWER
                          WITHOUT completion. Covers BOTH "not applicable" and
                          "not complied" finals. NEVER label it "not applicable"
                          alone - one of its members is a confirmed miss.
      open                ClosureClass open, OR no transaction at all
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
  Unchanged from today's free tier: dbo.tvfInsightsScopedInstances for the
  representative user of the recipient group (2-D, branch x category). Every
  recipient in a group shares this exact scope by construction - groups are
  keyed on the scope-pair signature (ResolveDigestRecipientsActivity). So any
  entity a slot proc names is one every recipient of that email can already
  access in RegTrack.
  @UserID is REQUIRED. sql/06's tenant-wide NULL branch is not reproduced:
  the orchestrator always passes a real representative user, and a NULL here
  is a caller defect, not a mode.

  IDEMPOTENT. PURE ASCII (CLAUDE.md Sec.5a). Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
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

    IF @AsOf < @CurrStart OR @AsOf >= @NextMonthStart
        THROW 51237, N'FREE MONTHLY - @AsOf FALLS OUTSIDE THE EDITION MONTH. Most likely a clock mismatch (a UTC @AsOf against a local-time month). Pass @AsOf in the same clock as ComplianceScheduleOn.ScheduleOn. Refusing to compute.', 1;

    /*===================================================================
      1. SCOPE + DICTIONARY PRE-FLIGHT
    ===================================================================*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51230, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

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
        CAST(CASE WHEN cso.ScheduleOn <= @AsOf
                   AND (d.OverdueEligible = 1 OR lt.ID IS NULL) THEN 1 ELSE 0 END AS BIT),
        CASE WHEN cso.ScheduleOn <= @AsOf AND (d.OverdueEligible = 1 OR lt.ID IS NULL)
             THEN DATEDIFF(DAY, cso.ScheduleOn, @AsOf) END,
        CASE WHEN cso.ScheduleOn <= @AsOf AND (d.OverdueEligible = 1 OR lt.ID IS NULL)
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
           OR (cso.ScheduleOn <= @AsOf AND (d.OverdueEligible = 1 OR lt.ID IS NULL)) );

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
END
GO

PRINT 'Free monthly fact loader installed (usp_Insights_FreeMonthly_LoadFacts).';
GO
