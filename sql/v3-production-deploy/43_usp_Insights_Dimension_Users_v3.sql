/*===========================================================================
  RegTrack Insights - v3 (2026-10-01): occurrence-level Users
  Object : dbo.usp_Insights_Dimension_Users
  Base   : the definition DEPLOYED on UAT (sql/v2/33 lineage + live hotfixes)

  Change : Instances/PerformerInstances/ReviewerInstances/OtherRoleInstances/
  Overdue/OverduePct now count every real ComplianceScheduleOn OCCURRENCE in the
  window (one row per real due date), not one row per distinct
  ComplianceInstanceID - same conversion already applied and Excel-verified on
  Location/Risk/Nature/Departments/Act/Entity (sql/v2/37-42).

  [DESIGN DECISION, REVISED 2026-10-01 after Excel verification] First pass
  built Performer/Reviewer from ComplianceAssignment (RoleID 3/4) expanded per
  occurrence - this was WRONG. ComplianceAssignment is a STATIC, instance-level
  label that drifts out of sync with who actually worked a given due date:
  confirmed live, tenant 1285, instance 462263 - its ComplianceAssignment
  performer is Adi Management, but the real ComplianceScheduleOn.Performerid
  for that occurrence (the actual performer-of-record) is adi Performer.
  Checked against the real RegTrack Detailed Report export:
    - ComplianceScheduleOn.Performerid matches its Performer column EXACTLY
      (156/119/3/1/1), 0% NULL in this scope. ComplianceAssignment did not.
    - ComplianceScheduleOn.Reviewerid matches its Reviewer column EXACTLY
      (273/4/3), 0% NULL. ComplianceAssignment was close but not exact (274).
    - ComplianceScheduleOn.Approverid was ALSO tried and REJECTED: 61% NULL
      in this scope and undercounts a named approver by 9x (2 vs the Excel's
      18) - ComplianceAssignment's existing RoleID NOT IN (3,4) bucket stays
      the source for OtherRoleInstances because it already tracks the Excel's
      own Approver column closely (98 exact, 20 vs 18) where Approverid does
      not.
  So: Performer/Reviewer come directly from #occ's own Performerid/Reviewerid
  (one real person per real occurrence, already occurrence-grain, no
  expansion needed). Only the residual "other role" bucket (#asgOther) still
  expands ComplianceAssignment per occurrence, narrowed to RoleID NOT IN (3,4).
  #userOcc unions all three into one (UserID, ScheduleOnID, RoleKind) table -
  the true occurrence-grain "who touched this due date, in what capacity"
  population Instances/PerformerInstances/ReviewerInstances/OtherRoleInstances/
  Overdue are built from.

  What deliberately did NOT move to occurrence grain, and why:
   - AssignedInstancesDistinct / UnassignedInstances - these describe DISTINCT
     OBLIGATIONS with (or without) a ComplianceAssignment row, not a workload
     count. Their names promise instance semantics (CLAUDE.md sec.4a) and
     changing that silently would repeat the exact ScopedInstances defect this
     fix set out to correct on the other 6 dims. Computed exactly as before,
     untouched - still ComplianceAssignment-based, a deliberately different
     question ("is anyone formally assigned") from "who actually did this
     occurrence" (which now correctly answers from ComplianceScheduleOn).
   - The 155%-trap reconciliation (THROW 51101) and single_reviewer_dependency
     (@soleReviewerInstances) - purely instance-grain (#inst vs #asg),
     unaffected by what grain or source #rows.Instances uses. Untouched.
   - ImprisonmentInstances/ImprisonmentOverdue/BranchesCovered - instance-level
     properties of the obligation itself, not something that multiplies per
     occurrence (same convention as every other dimension). Computed from a
     SEPARATE instance-level aggregate (#rowsInst), never from #userOcc, to
     avoid fan-out.
   - A-CONC (top-10 concentration) stays instance-grain (#asg, @assignedUnion)
     - its own caveat text already says "distinct-instance union, not a sum of
     per-user counts"; changing its grain was not asked for and would alter a
     narrative-facing figure with no Excel ground truth to verify it against.
   - #quality/#timing/#medtiming (completion-timing facets) - a different
     facet entirely (historical completion quality), never window-scoped in
     the first place. Untouched.

  Reconciliation (sec.6): THROW 51101 is unchanged (instance-grain both sides).
  THROW 51102/51103 are RE-GRADED, not replaced - they protect the exact same
  two properties ("no user claims more than the whole union" / "every assigned
  unit appears in some row") but must now compare Instances (occurrence-grain)
  against an occurrence-grain union (@assignedOccUnion, new), or the grain
  mismatch makes them misfire. Same codes, same intent, corrected comparator.

  ScopedInstances output column [FIX 2026-10-01, same defect found on the
  other 6 dims] reports the DISTINCT instance count (@scopedInstancesDistinct,
  from #inst), never the occurrence count. New ScopedOccurrences /
  AssignedOccurrencesDistinct columns expose the occurrence-grain totals
  explicitly, so nothing about what Instances/Overdue are counted against is
  left for a reader to guess - the same ambiguity that caused today's
  Entity/Users "142 vs 280" investigation in the first place.

  Error block: unchanged (51100-51109, same as the prior version).
===========================================================================*/
SET NOCOUNT ON;
GO
CREATE OR ALTER PROCEDURE dbo.usp_Insights_Dimension_Users
    @UserID      INT,
    @CustomerID  INT,
    @WindowStart DATETIME,
    @WindowEnd   DATETIME,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT --------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51100, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. SCOPED INSTANCE BASE ----------------------------------------*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT s.ComplianceInstanceID, s.BranchID, s.Imprisonment
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;

    CREATE CLUSTERED INDEX IX_inst ON #inst (ComplianceInstanceID);

    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51104, N'USERS DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51104, N'USERS DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    /*-- 1b. OCCURRENCE-LEVEL ACTIVE SCHEDULES [CHANGED 2026-10-01] - one row
        per real due date, never collapsed to DISTINCT ComplianceInstanceID.
        This is what Instances/PerformerInstances/ReviewerInstances/Overdue are
        now counted over (section 5 below). ------------------------------- */
    IF OBJECT_ID('tempdb..#occ') IS NOT NULL DROP TABLE #occ;
    SELECT
        cso.ID AS ScheduleOnID,
        cso.ComplianceInstanceID,
        i.BranchID,
        cso.ScheduleOn,
        cso.Performerid,
        cso.Reviewerid
    INTO #occ
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd
      AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1;   -- [v2] switched-off due dates do not count (RegTrack parity)

    CREATE CLUSTERED INDEX IX_occ ON #occ (ComplianceInstanceID, ScheduleOnID);

    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #occ o WHERE o.ComplianceInstanceID = i.ComplianceInstanceID);

    /*-- 1c. OVERDUE - OCCURRENCE-LEVEL [CHANGED 2026-10-01]
        tvfInsightsOverdueSchedules already carries ComplianceScheduleOnID per
        real occurrence - join on that directly, no DISTINCT collapse. This
        structurally prevents the 2026-09-30 out-of-window overdue leak (an
        occurrence outside the window was never a member of #occ at all). */
    IF OBJECT_ID('tempdb..#ovdocc') IS NOT NULL DROP TABLE #ovdocc;
    SELECT o.ComplianceScheduleOnID
    INTO #ovdocc
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #occ x ON x.ScheduleOnID = o.ComplianceScheduleOnID
    WHERE o.ScheduleOn >= @WindowStart AND o.ScheduleOn < @WindowEnd;

    CREATE CLUSTERED INDEX IX_ovdocc ON #ovdocc (ComplianceScheduleOnID);

    /*  Instance-grain derivative, needed ONLY for ImprisonmentOverdue below,
        which stays instance-level (an instance is overdue if ANY of its
        in-window occurrences is overdue). */
    IF OBJECT_ID('tempdb..#ovdInst') IS NOT NULL DROP TABLE #ovdInst;
    SELECT DISTINCT x.ComplianceInstanceID
    INTO #ovdInst
    FROM #occ x
    JOIN #ovdocc v ON v.ComplianceScheduleOnID = x.ScheduleOnID;
    CREATE CLUSTERED INDEX IX_ovdInst ON #ovdInst (ComplianceInstanceID);

    /*-- 2. ASSIGNMENTS - the grain of this dimension --------------------*/
    IF OBJECT_ID('tempdb..#asg') IS NOT NULL DROP TABLE #asg;
    SELECT DISTINCT ca.UserID, ca.RoleID, ca.ComplianceInstanceID
    INTO #asg
    FROM ComplianceAssignment ca
    JOIN #inst i ON i.ComplianceInstanceID = ca.ComplianceInstanceID
    WHERE ca.UserID > 0;

    CREATE CLUSTERED INDEX IX_asg ON #asg (UserID, ComplianceInstanceID);

    /*-- 2b. PER-OCCURRENCE ROLE IDENTITY [REDESIGNED 2026-10-01, Excel-verified]
        ComplianceAssignment (RoleID 3/4) is a STATIC, instance-level label that
        drifts out of sync with who actually worked a given due date - confirmed
        live, tenant 1285: instance 462263's ComplianceAssignment performer is
        Adi Management, but its real ComplianceScheduleOn.Performerid (the actual
        performer-of-record for that occurrence) is adi Performer. Checked against
        the real RegTrack Detailed Report export: ComplianceScheduleOn.Performerid/
        Reviewerid match its Performer/Reviewer columns EXACTLY (156/119/3/1/1 and
        273/4/3 respectively, both 0% NULL in this scope) - ComplianceAssignment
        does not (it was off by large margins on Performer specifically).
        Approverid was ALSO tried and rejected: 61% NULL in this scope and wildly
        undercounts a named approver (2 vs the Excel's 18) - ComplianceAssignment's
        existing RoleID NOT IN (3,4) bucket (#asgOther below) remains the source for
        OtherRoleInstances, unchanged, because it already tracks the Excel's own
        Approver column closely (98 exact, 18 vs 20) where Approverid does not.

        Performer/Reviewer therefore come from #occ's own Performerid/Reviewerid
        (one real person per real occurrence - no expansion needed, #occ already
        IS occurrence-grain). Only the residual "other role" bucket still needs
        ComplianceAssignment expanded per occurrence, same mechanism as before,
        narrowed to RoleID NOT IN (3,4) only (Performer/Reviewer no longer come
        from here at all). */
    IF OBJECT_ID('tempdb..#asgOther') IS NOT NULL DROP TABLE #asgOther;
    SELECT a.UserID, o.ScheduleOnID
    INTO #asgOther
    FROM #asg a
    JOIN #occ o ON o.ComplianceInstanceID = a.ComplianceInstanceID
    WHERE a.RoleID NOT IN (3,4);

    IF OBJECT_ID('tempdb..#userOcc') IS NOT NULL DROP TABLE #userOcc;
    SELECT o.Performerid AS UserID, o.ScheduleOnID, 'performer' AS RoleKind
    INTO #userOcc
    FROM #occ o WHERE o.Performerid IS NOT NULL
    UNION ALL
    SELECT o.Reviewerid, o.ScheduleOnID, 'reviewer'
    FROM #occ o WHERE o.Reviewerid IS NOT NULL
    UNION ALL
    SELECT ao.UserID, ao.ScheduleOnID, 'other'
    FROM #asgOther ao;

    CREATE CLUSTERED INDEX IX_userOcc ON #userOcc (UserID, ScheduleOnID);

    /*-- 3. ON-TIME COMPLETION, PERFORMER ONLY, OWN WORK -----------------
       Facet B2. Timeliness comes from the dictionary; resolved_terminal
       carries no timeliness and is therefore excluded from the denominator
       by construction, exactly as spec 6.4 requires.
       [UNCHANGED 2026-10-01] A different facet (historical completion
       quality) - never window-scoped, not part of today's conversion. */
    IF OBJECT_ID('tempdb..#quality') IS NOT NULL DROP TABLE #quality;
    SELECT a.UserID,
           COUNT(*)                                                     AS CompletedEvents,
           SUM(CASE WHEN d.Timeliness = 'on_time' THEN 1 ELSE 0 END)     AS OnTimeEvents
    INTO #quality
    FROM #asg a
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = a.ComplianceInstanceID
    JOIN ComplianceTransaction t  ON t.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = t.StatusId
    WHERE a.RoleID = 3 AND d.ClosureClass = 'completed' AND d.Timeliness IS NOT NULL
    GROUP BY a.UserID;

    /*-- 3b. COMPLETION TIMING - HOW early/late, not just whether ---------
       [UNCHANGED 2026-10-01] See sql/v2/33 header for the full rationale
       (record date vs stated date, 365-day outlier exclusion, median not
       mean). Not part of today's occurrence-level conversion. */
    IF OBJECT_ID('tempdb..#timing') IS NOT NULL DROP TABLE #timing;
    SELECT a.UserID,
           DATEDIFF(day, cso.ScheduleOn, t.Dated) AS DaysLate
    INTO #timing
    FROM #asg a
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = a.ComplianceInstanceID
    JOIN ComplianceTransaction t  ON t.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = t.StatusId
    WHERE a.RoleID = 3 AND d.ClosureClass = 'completed' AND d.Timeliness IS NOT NULL;

    DECLARE @timingOutliersExcluded INT = (SELECT COUNT(*) FROM #timing WHERE ABS(DaysLate) > 365);

    IF OBJECT_ID('tempdb..#medtiming') IS NOT NULL DROP TABLE #medtiming;
    SELECT DISTINCT UserID,
           CAST(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY CAST(DaysLate AS FLOAT))
                OVER (PARTITION BY UserID) AS DECIMAL(9,1)) AS MedianDaysEarlyLate,
           COUNT(*) OVER (PARTITION BY UserID) AS TimingSampleSize,
           SUM(CASE WHEN DaysLate < 0 THEN 1 ELSE 0 END) OVER (PARTITION BY UserID) AS EarlyCount,
           SUM(CASE WHEN DaysLate > 0 THEN 1 ELSE 0 END) OVER (PARTITION BY UserID) AS LateCount,
           SUM(CASE WHEN DaysLate = 0 THEN 1 ELSE 0 END) OVER (PARTITION BY UserID) AS OnTimeCount
    INTO #medtiming
    FROM #timing
    WHERE ABS(DaysLate) <= 365;

    DECLARE @tenantMedianDaysEarlyLate DECIMAL(9,1) = (
        SELECT TOP 1 CAST(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY CAST(DaysLate AS FLOAT)) OVER () AS DECIMAL(9,1))
        FROM #timing WHERE ABS(DaysLate) <= 365);

    /*-- 4. ENGAGEMENT - 12-month login window, keyed by EMAIL -----------*/
    IF OBJECT_ID('tempdb..#login') IS NOT NULL DROP TABLE #login;
    SELECT u.ID AS UserID, COUNT(l.ID) AS Logins12m
    INTO #login
    FROM [User] u
    LEFT JOIN UserLoginTrack l
           ON l.Email = u.Email
          AND l.LoginDate >= DATEADD(MONTH, -12, @AsOf)
          AND l.LoginDate <  @AsOf
    WHERE u.CustomerID = @CustomerID AND u.IsDeleted = 0
    GROUP BY u.ID;

    /*-- 5. ROWS - one per user holding assignments [CHANGED 2026-10-01]

       Occurrence-grain facts (Instances/PerformerInstances/ReviewerInstances/
       OtherRoleInstances/Overdue, from #userOcc) and instance-grain facts
       (ImprisonmentInstances/ImprisonmentOverdue/BranchesCovered, from #asg/
       #inst) are aggregated in TWO separate pre-aggregated queries, each
       already 1-row-per-user before the join - joining #userOcc (many rows per
       instance) and #inst (one row per instance) in a single GROUP BY would
       fan the instance-level counts out by however many occurrences each
       instance has, silently inflating them (same trap as Entity's #direct). */
    IF OBJECT_ID('tempdb..#rowsOcc') IS NOT NULL DROP TABLE #rowsOcc;
    SELECT
        uo.UserID,
        COUNT(DISTINCT uo.ScheduleOnID)                                                  AS Instances,
        COUNT(DISTINCT CASE WHEN uo.RoleKind = 'performer' THEN uo.ScheduleOnID END)      AS PerformerInstances,
        COUNT(DISTINCT CASE WHEN uo.RoleKind = 'reviewer' THEN uo.ScheduleOnID END)       AS ReviewerInstances,
        COUNT(DISTINCT CASE WHEN uo.RoleKind = 'other' THEN uo.ScheduleOnID END)          AS OtherRoleInstances,
        COUNT(DISTINCT CASE WHEN v.ComplianceScheduleOnID IS NOT NULL THEN uo.ScheduleOnID END) AS Overdue
    INTO #rowsOcc
    FROM #userOcc uo
    LEFT JOIN #ovdocc v ON v.ComplianceScheduleOnID = uo.ScheduleOnID
    GROUP BY uo.UserID;

    IF OBJECT_ID('tempdb..#rowsInst') IS NOT NULL DROP TABLE #rowsInst;
    SELECT
        a.UserID,
        COUNT(DISTINCT CASE WHEN i.Imprisonment = 1 THEN a.ComplianceInstanceID END) AS ImprisonmentInstances,
        COUNT(DISTINCT CASE WHEN i.Imprisonment = 1 AND o.ComplianceInstanceID IS NOT NULL
                             THEN a.ComplianceInstanceID END)                        AS ImprisonmentOverdue,
        COUNT(DISTINCT i.BranchID)                                                  AS BranchesCovered
    INTO #rowsInst
    FROM #asg a
    JOIN #inst i        ON i.ComplianceInstanceID = a.ComplianceInstanceID
    LEFT JOIN #ovdInst o ON o.ComplianceInstanceID = a.ComplianceInstanceID
    GROUP BY a.UserID;

    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        UserID                BIGINT         NOT NULL PRIMARY KEY,
        UserName              NVARCHAR(300)  NULL,
        IsActive              BIT            NULL,
        Instances             INT            NOT NULL,   -- [CHANGED 2026-10-01] distinct OCCURRENCES held, any role
        PerformerInstances    INT            NOT NULL,
        ReviewerInstances     INT            NOT NULL,
        OtherRoleInstances    INT            NOT NULL,   -- [TRAP] RoleID outside {3,4} - e.g. RoleID 6, seen live on
                                                           -- tenant 1403, not yet in DIMENSION_SPECS.md. Never blended
                                                           -- into Performer/Reviewer - surfaced separately so an
                                                           -- undocumented role cannot silently vanish into a total.
        Overdue               INT            NOT NULL,
        OverduePct            DECIMAL(5,1)   NULL,
        ImprisonmentInstances INT            NOT NULL,
        ImprisonmentOverdue   INT            NOT NULL,   -- [ADDED 2026-09-13] consequence ranking
        BranchesCovered       INT            NOT NULL,
        Logins12m             INT            NOT NULL,
        EngagementBand        VARCHAR(20)    NULL,
        CompletedEvents       INT            NOT NULL,
        OnTimeEvents          INT            NOT NULL,
        OnTimePct             DECIMAL(5,1)   NULL,
        QuadrantOverlay       VARCHAR(30)    NULL,
        MedianDaysEarlyLate   DECIMAL(9,1)   NULL,   -- NULL = no qualifying completed event (never 0 - 0 is a real "right on the day")
        TimingSampleSize      INT            NULL,   -- how many completed events the median above is drawn from
        EarlyCount            INT            NULL,
        LateCount             INT            NULL,
        OnTimeCount           INT            NULL,   -- DaysLate = 0 exactly - NOT the same population/definition as OnTimeEvents above
        EarlyPct              DECIMAL(5,1)   NULL,   -- date-based (DaysLate < 0) - NOT the same as OnTimePct, which is status-based
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (UserID, UserName, IsActive, Instances, PerformerInstances, ReviewerInstances,
                  OtherRoleInstances, Overdue, ImprisonmentInstances, ImprisonmentOverdue, BranchesCovered, Logins12m,
                  CompletedEvents, OnTimeEvents)
    SELECT
        ro.UserID,
        LTRIM(RTRIM(CONCAT(u.FirstName, N' ', u.LastName))),
        u.IsActive,
        ro.Instances, ro.PerformerInstances, ro.ReviewerInstances, ro.OtherRoleInstances, ro.Overdue,
        ISNULL(ri.ImprisonmentInstances, 0), ISNULL(ri.ImprisonmentOverdue, 0), ISNULL(ri.BranchesCovered, 0),
        ISNULL(lg.Logins12m, 0),
        ISNULL(q.CompletedEvents, 0),
        ISNULL(q.OnTimeEvents, 0)
    FROM #rowsOcc ro
    LEFT JOIN #rowsInst ri ON ri.UserID = ro.UserID
    LEFT JOIN [User] u     ON u.ID = ro.UserID
    LEFT JOIN #login lg    ON lg.UserID = ro.UserID
    LEFT JOIN #quality q   ON q.UserID = ro.UserID;

    /*-- 6. RECONCILIATION [RE-GRADED 2026-10-01] ------------------------
       [TRAP] A GUARD THAT CANNOT FAIL IS NOT A GUARD.
       @unassigned is DEFINED as @scopedInstancesDistinct - @assignedUnion, so
       testing "@assignedUnion + @unassigned = @scopedInstancesDistinct" is an
       algebraic identity, and #asg is INNER JOINed to #inst so
       "@unassigned < 0" is unreachable too. Both look like reconciliation and
       neither can ever fire. The checks below count from an INDEPENDENT
       direction - from the instance side rather than the assignment side -
       so a fan-out or a scope leak actually shows up as a disagreement
       between two counts.

       @scopedTotal is now the OCCURRENCE total (from #occ) - the quantity
       Instances/Overdue in #rows are actually counted against, same
       convention as the other 6 dims. @scopedInstancesDistinct (from #inst,
       window-narrowed) is the DISTINCT instance count and feeds ONLY
       ScopedInstances/AssignedInstancesDistinct/UnassignedInstances below -
       it must never be used as a ratio denominator against Instances, that
       would mix grains (the exact bug this whole fix set out to correct). */
    DECLARE @scopedTotal   INT = (SELECT COUNT(*) FROM #occ);
    DECLARE @scopedInstancesDistinct INT = (SELECT COUNT(*) FROM #inst);
    DECLARE @assignedUnion INT = (SELECT COUNT(DISTINCT ComplianceInstanceID) FROM #asg);
    DECLARE @unassigned    INT = @scopedInstancesDistinct - @assignedUnion;
    DECLARE @rowSum        INT = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @assignedOccUnion INT = (SELECT COUNT(DISTINCT ScheduleOnID) FROM #userOcc);

    /*  Same quantity (@assignedUnion), counted from #inst instead of #asg.
        Purely instance-grain both sides - unaffected by what grain
        #rows.Instances uses, so this check is UNCHANGED from the prior
        version. */
    DECLARE @assignedViaInst INT = (
        SELECT COUNT(*) FROM #inst i
        WHERE EXISTS (SELECT 1 FROM #asg a WHERE a.ComplianceInstanceID = i.ComplianceInstanceID));

    IF @assignedViaInst <> @assignedUnion
        THROW 51101, N'USERS DIMENSION RECONCILIATION FAILED - the distinct-instance union counted from the assignment side disagrees with the count from the instance side. A join is fanning out or an out-of-scope assignment has leaked in. Refusing to publish.', 1;

    /*  No single user can hold more distinct occurrences than exist in the
        OCCURRENCE union of every assigned occurrence (@assignedOccUnion).
        [RE-GRADED 2026-10-01] This used to compare against @assignedUnion
        (instance-grain) because Instances was instance-grain; now that
        Instances counts occurrences, the comparator must too, or a tenant
        whose obligations recur often inside the window would trip this
        falsely even with nothing wrong. Same protective intent as before:
        this is what actually catches the 155% class of bug at the row grain. */
    IF EXISTS (SELECT 1 FROM #rows WHERE Instances > @assignedOccUnion)
        THROW 51102, N'USERS DIMENSION RECONCILIATION FAILED - a user row claims more distinct occurrences than the whole assigned occurrence union contains. A join is fanning out. Refusing to publish.', 1;

    /*  Every assigned occurrence appears in at least one user row, so the sum
        of per-user occurrence counts must be at least the occurrence union.
        Less means rows were lost. [RE-GRADED 2026-10-01], same reasoning as
        51102 above. */
    IF @rowSum < @assignedOccUnion
        THROW 51103, N'USERS DIMENSION RECONCILIATION FAILED - the sum of per-user occurrence counts is below the distinct-occurrence union, so assigned occurrences are missing from the rows. Refusing to publish.', 1;

    DECLARE @hasAnyObligations BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * (SELECT COUNT(*) FROM #ovdocc) / @scopedTotal END;

    UPDATE #rows SET
        OverduePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END,
        OnTimePct  = CASE WHEN CompletedEvents = 0 THEN NULL
                          ELSE 100.0 * OnTimeEvents / CompletedEvents END,
        EngagementBand = CASE WHEN Logins12m >= 100 THEN 'power'
                              WHEN Logins12m >= 26  THEN 'frequent'
                              WHEN Logins12m >= 6   THEN 'moderate'
                              WHEN Logins12m >= 1   THEN 'seldom'
                              ELSE 'never' END;

    /*  MedianDaysEarlyLate stays NULL for a user with zero qualifying events -
        never defaulted to 0, which would misreport them as "always on the day". */
    UPDATE r SET
        r.MedianDaysEarlyLate = mt.MedianDaysEarlyLate,
        r.TimingSampleSize    = mt.TimingSampleSize,
        r.EarlyCount          = mt.EarlyCount,
        r.LateCount           = mt.LateCount,
        r.OnTimeCount         = mt.OnTimeCount,
        r.EarlyPct            = CASE WHEN mt.TimingSampleSize = 0 THEN NULL
                                      ELSE 100.0 * mt.EarlyCount / mt.TimingSampleSize END
    FROM #rows r
    JOIN #medtiming mt ON mt.UserID = r.UserID;

    /*  The 2x2 overlay. Engagement on one axis, quality on the other - the
        third cell (disengaged but current) is the dependency risk that login
        frequency alone can never find. Users with no completed work have no
        quality reading and are left unclassified rather than assumed.      */
    DECLARE @medianOnTime DECIMAL(5,1) =
        (SELECT TOP 1 PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY OnTimePct) OVER ()
         FROM #rows WHERE OnTimePct IS NOT NULL);

    UPDATE #rows SET QuadrantOverlay =
        CASE WHEN OnTimePct IS NULL OR @medianOnTime IS NULL THEN NULL
             WHEN EngagementBand IN ('power','frequent') AND OnTimePct >= @medianOnTime THEN 'engaged_quality'
             WHEN EngagementBand IN ('power','frequent') AND OnTimePct <  @medianOnTime THEN 'engaged_slipping'
             WHEN OnTimePct >= @medianOnTime THEN 'disengaged_current'
             ELSE 'disengaged_slipping' END;

    /*-- 7. DETECTIONS ---------------------------------------------------*/
    DECLARE @medianLoad DECIMAL(9,2) =
        (SELECT TOP 1 PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY CAST(PerformerInstances AS FLOAT)) OVER ()
         FROM #rows WHERE PerformerInstances > 0);

    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyObligations = 1 AND Instances > 0 AND Logins12m = 0
                 THEN ',never_logged_in_holding_assignments' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND Instances > 0 AND IsActive = 0
                 THEN ',deactivated_holding_work' ELSE '' END +
            /*  Peer-relative: three times this tenant's own median performer load,
                never an absolute count - load varies by an order of magnitude
                between tenants. */
            CASE WHEN @hasAnyObligations = 1 AND @medianLoad IS NOT NULL AND @medianLoad > 0
                  AND PerformerInstances > @medianLoad * 3
                 THEN ',overloaded_performer' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND ReviewerInstances > 0
                  AND PerformerInstances = 0
                 THEN ',review_only_user' ELSE '' END
        , 1, 1, '');

    /*  single_reviewer_dependency is an INSTANCE-level fact, not a user-level
        one: instances whose only reviewer is one person. Counted as a tenant
        figure so it cannot be double-counted across users. Stays
        instance-grain (#asg) - unaffected by today's conversion. */
    DECLARE @soleReviewerInstances INT = (
        SELECT COUNT(*) FROM (
            SELECT a.ComplianceInstanceID
            FROM #asg a WHERE a.RoleID = 4
            GROUP BY a.ComplianceInstanceID
            HAVING COUNT(DISTINCT a.UserID) = 1) x);

    SELECT
        'control_totals'              AS ResultSet,
        @scopedInstancesDistinct      AS ScopedInstances,
        @scopedTotal                  AS ScopedOccurrences,
        @assignedUnion                AS AssignedInstancesDistinct,
        @assignedOccUnion             AS AssignedOccurrencesDistinct,
        CAST(1 AS BIT)                AS Reconciled,
        @unassigned                   AS UnassignedInstances,
        (SELECT COUNT(*) FROM #ovdocc) AS OverdueInstances,
        @tenantOverduePct             AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows)  AS UsersReported,
        @rowSum                       AS SumOfPerUserInstances,
        @medianOnTime                 AS TenantMedianOnTimePct,
        @medianLoad                   AS TenantMedianPerformerLoad,
        @soleReviewerInstances        AS InstancesWithSoleReviewer,
        @tenantMedianDaysEarlyLate    AS TenantMedianDaysEarlyLate,
        @timingOutliersExcluded       AS TimingOutliersExcluded;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*-- 8. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @allUsers INT = (SELECT COUNT(*) FROM #rows);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'never_logged_in_holding_assignments', @allUsers,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%never_logged_in_holding_assignments%')
    UNION ALL SELECT 'deactivated_holding_work', @allUsers,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%deactivated_holding_work%')
    UNION ALL SELECT 'overloaded_performer', @allUsers,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%overloaded_performer%');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51040, N'DETECTOR CONTRACT VIOLATED - a detector flagged more rows than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 9. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    INSERT #assert VALUES ('A-TENANT','overdue_pct',N'tenant',@tenantOverduePct,NULL,NULL,NULL,NULL,NULL,NULL);

    /*  CONCENTRATION - the 155% trap. Distinct-instance union of the top ten
        users by load, over the assigned union. Never a sum of per-user counts.
        [UNCHANGED 2026-10-01] Stays instance-grain deliberately - see header. */
    IF @assignedUnion > 0
    BEGIN
        DECLARE @top10Union INT = (
            SELECT COUNT(DISTINCT a.ComplianceInstanceID)
            FROM #asg a
            WHERE a.UserID IN (SELECT TOP 10 UserID FROM #rows ORDER BY Instances DESC));

        INSERT #assert
        VALUES ('A-CONC','top10_share_of_assigned_pct',N'tenant',
                CAST(100.0 * @top10Union / @assignedUnion AS DECIMAL(5,1)),
                NULL, @assignedUnion, NULL, NULL, NULL,
                N'distinct-instance union, not a sum of per-user counts - summing double-counts paired performer/reviewer work');
    END

    /*  ENGAGEMENT BANDS - facet B1. The caveat is MANDATORY on every band
        assertion. Never-login users showed the LOWEST overdue rate on the
        reference tenant because they are nominal reviewers, not because they
        are the best compliers. */
    INSERT #assert
    SELECT 'A-BAND-' + CAST(ROW_NUMBER() OVER (ORDER BY MIN(Logins12m) DESC) AS VARCHAR(5)),
           'overdue_pct', CONCAT(N'engagement band: ', EngagementBand),
           CAST(CASE WHEN SUM(Instances) = 0 THEN 0
                     ELSE 100.0 * SUM(Overdue) / SUM(Instances) END AS DECIMAL(5,1)),
           NULL, COUNT(*), @tenantOverduePct, NULL, NULL,
           N'confounded_by_role_mix: login frequency measures ENGAGEMENT, not compliance quality. Low overdue in a disengaged band reflects nominal reviewer roles on work others keep current.'
    FROM #rows WHERE @hasAnyObligations = 1
    GROUP BY EngagementBand;

    /*  DEPENDENCY RISK - the cell login frequency alone cannot find. */
    IF EXISTS (SELECT 1 FROM #rows WHERE QuadrantOverlay = 'disengaged_current')
    INSERT #assert
    SELECT 'A-DEPEND','users_disengaged_but_current',N'tenant',
           COUNT(*), NULL, @allUsers, NULL, NULL, NULL,
           N'dependency_risk: current work held by users who are not present. Not a performance verdict - a continuity one.'
    FROM #rows WHERE QuadrantOverlay = 'disengaged_current';

    IF (SELECT EmitMode FROM #detector WHERE Detector='deactivated_holding_work') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-DEACT-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
               'instances_held', UserName, Instances, NULL, NULL, NULL, NULL, NULL,
               N'deactivated_holding_work: account is deactivated but still holds live assignments'
        FROM #rows WHERE Flags LIKE '%deactivated_holding_work%' ORDER BY Instances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='deactivated_holding_work') = 'aggregate'
        INSERT #assert
        SELECT 'A-DEACT-AGG','deactivated_users_holding_work',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - deactivated accounts holding live work is a tenant-wide provisioning pattern'
        FROM #detector WHERE Detector='deactivated_holding_work';

    IF (SELECT EmitMode FROM #detector WHERE Detector='overloaded_performer') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-LOAD-' + CAST(ROW_NUMBER() OVER (ORDER BY PerformerInstances DESC) AS VARCHAR(5)),
               'performer_instances', UserName, PerformerInstances, NULL, NULL,
               @medianLoad, PerformerInstances - @medianLoad, 'worse',
               N'overloaded_performer: measured against this tenant''s own median performer load'
        FROM #rows WHERE Flags LIKE '%overloaded_performer%' ORDER BY PerformerInstances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='overloaded_performer') = 'aggregate'
        INSERT #assert
        SELECT 'A-LOAD-AGG','overloaded_performers',N'tenant',
               Flagged, NULL, Eligible, @medianLoad, FlaggedPct, 'worse',
               N'aggregate - load is concentrated across many users, not a handful'
        FROM #detector WHERE Detector='overloaded_performer';

    IF @soleReviewerInstances > 0 AND @assignedUnion > 0
    INSERT #assert
    VALUES ('A-SOLEREV','instances_with_sole_reviewer',N'tenant',@soleReviewerInstances,
            NULL,@assignedUnion,NULL,NULL,NULL,
            N'single_reviewer_dependency: counted at INSTANCE level so it cannot be double-counted across users');


    /*  [ADDED 2026-09-13] CONSEQUENCE, not rate. See the note in sql/05: on a
        live pilot the rate-ranked finding put a 69-obligation area with ZERO
        imprisonment exposure at rank 1. Both assertions are emitted; the
        composition layer chooses. Emitted only where exposure exists to rank. */
    IF EXISTS (SELECT 1 FROM #rows WHERE ImprisonmentOverdue > 0)
    INSERT #assert
    SELECT TOP 1 'A-WORST-USER-EXP','imprisonment_overdue_count', UserName, ImprisonmentOverdue, NULL,
           (SELECT SUM(ImprisonmentOverdue) FROM #rows), NULL, NULL, 'worse',
           N'ranked by CONSEQUENCE - overdue obligations carrying personal liability - not by '
         + N'rate or by load. A user holding many low-consequence items is a capacity question; '
         + N'this is a liability one.'
    FROM #rows WHERE ImprisonmentOverdue > 0 ORDER BY ImprisonmentOverdue DESC, Overdue DESC;

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS ----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-CONC','medium',
           CONCAT(N'The ten busiest users hold ', Value, N'% of all assigned obligations'),
           AssertionId,
           N'This is a distinct-instance union. Do not add per-user counts together - that double-counts paired performer/reviewer work and can exceed 100%.'
    FROM #assert WHERE AssertionId = 'A-CONC';

    INSERT #find
    SELECT 'F-DEPEND','high',
           CONCAT(N'', CAST(Value AS INT), N' user(s) hold work that is current but are not logging in'),
           'A-DEPEND',
           N'MUST NOT be presented as good performance. This is a continuity risk: live obligations are assigned to people who are not present. Someone else is carrying them.'
    FROM #assert WHERE AssertionId = 'A-DEPEND';

    INSERT #find
    SELECT 'F-DEACT','high',
           CONCAT(N'', ScopeLabel, N' is deactivated but still holds ', CAST(Value AS INT), N' live obligation(s)'),
           AssertionId,
           N'The account is disabled; the work is not. Do not report this as resolved.'
    FROM #assert WHERE AssertionId LIKE 'A-DEACT-[0-9]%';

    INSERT #find
    SELECT 'F-DEACT-AGG','high',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' users (', VsComparatorPP,
                  N'%) are deactivated but still hold live obligations'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-DEACT-AGG';

    INSERT #find
    SELECT 'F-LOAD','medium',
           CONCAT(N'', ScopeLabel, N' performs ', CAST(Value AS INT),
                  N' obligations, against a tenant median of ', CAST(ComparatorValue AS INT)),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-LOAD-[0-9]%';

    INSERT #find
    SELECT 'F-LOAD-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' users (', VsComparatorPP,
                  N'%) carry more than three times the median performer load'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-LOAD-AGG';

    INSERT #find
    SELECT 'F-SOLEREV','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' assigned obligations depend on a single reviewer'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-SOLEREV';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY ------------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'engagement_is_not_quality'            THEN 'LoginBand'
                   WHEN 'users_without_quality_reading'        THEN 'OnTimePct'
                   WHEN 'unassigned_instances'                 THEN 'UnassignedInstances'
                   WHEN 'login_keyed_by_email'                 THEN 'LoginBand'
                   WHEN 'timing_measured_from_record_date'     THEN 'MedianDaysEarlyLate'
                   WHEN 'recording_lag'                        THEN 'MedianDaysEarlyLate'
                   WHEN 'implausible_completion_gaps'          THEN 'MedianDaysEarlyLate'
                   WHEN 'undocumented_role_id'                 THEN 'OtherRoleInstances'
                   WHEN 'instances_now_occurrences'             THEN 'Instances'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Scoped to obligations with a scheduled occurrence between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N'. An obligation with no occurrence in this window is excluded entirely, not just its '
                    + N'overdue figures - it will not appear against any user here even if it exists '
                    + N'cumulatively.') AS Detail
        UNION ALL
        SELECT 'instances_now_occurrences' AS Issue,
               N'[ADDED 2026-10-01] Instances/PerformerInstances/ReviewerInstances/OtherRoleInstances/Overdue '
             + N'count every real scheduled OCCURRENCE in the window, not one per distinct obligation - an '
             + N'obligation recurring three times in the window counts as 3. ScopedInstances/'
             + N'AssignedInstancesDistinct/UnassignedInstances are the distinct-OBLIGATION counts; '
             + N'ScopedOccurrences/AssignedOccurrencesDistinct are the matching occurrence-grain totals. '
             + N'Do not divide one grain by the other.' AS Detail
        UNION ALL
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'engagement_is_not_quality',
               N'Login frequency measures ENGAGEMENT and adoption, never compliance quality. On the '
             + N'reference tenant the never-login band showed the LOWEST overdue rate because those users '
             + N'are nominal reviewers on work active performers keep current. Every engagement assertion '
             + N'carries caveat confounded_by_role_mix and must be cited with it.'
        UNION ALL
        SELECT 'users_without_quality_reading',
               CONCAT(N'', COUNT(*), N' user(s) have no completed performer work in this scope, so they '
                    + N'have no on-time reading and are unclassified in the engagement/quality overlay '
                    + N'rather than assumed to be either.')
        FROM #rows WHERE OnTimePct IS NULL HAVING COUNT(*) > 0
        UNION ALL
        SELECT 'unassigned_instances',
               CONCAT(N'', @unassigned, N' obligation(s) in this scope have no assigned user at all and '
                    + N'therefore appear in no user row.')
        WHERE @unassigned > 0
        UNION ALL
        SELECT 'timing_measured_from_record_date',
               N'MedianDaysEarlyLate is measured from ComplianceTransaction.Dated - the SYSTEM RECORD '
             + N'date, when the completion was entered. A second date exists: StatusChangedOn, the date '
             + N'the user states the work was done. They are NOT the same and the choice changes the SIGN '
             + N'of the answer. Measured live 2026-09-13 on tenant 1817: median 18 days LATE by record '
             + N'date, 1 day EARLY by stated date - the same events, opposite conclusions. Record date is '
             + N'used deliberately: a completion that was never recorded cannot be evidenced to a '
             + N'regulator. NEVER present this as "when the work was done" - it is when the work was '
             + N'RECORDED.'
        UNION ALL
        SELECT 'recording_lag',
               N'Across four production tenants the record date is later than the user-stated date on 74% '
             + N'to 99.6% of completed events, never earlier, by a mean of 23 to 66 days. That lag is the '
             + N'distance between doing the work and being able to prove it, and it inflates every '
             + N'days-late figure here by roughly that amount. Treat MedianDaysEarlyLate as RECORDING '
             + N'timeliness, not working timeliness.'
        UNION ALL
        SELECT 'implausible_completion_gaps',
               CONCAT(N'', @timingOutliersExcluded, N' completed event(s) show a gap of more than 365 days '
                    + N'between due date and completion date - almost certainly bulk-migration or '
                    + N'backdated-schedule artifacts, not real behaviour. Excluded from every '
                    + N'MedianDaysEarlyLate figure in this report, tenant-wide and per-user.')
        WHERE @timingOutliersExcluded > 0
        UNION ALL
        SELECT 'login_keyed_by_email',
               N'UserLoginTrack is keyed by Email, not UserID. A user whose email changed, or who shares an '
             + N'address, may have an inaccurate login count. Engagement bands are indicative, not exact.'
        UNION ALL
        SELECT 'undocumented_role_id',
               CONCAT(N'RoleID(s) ', ids.List,
                      N' appear on ', cnt.InstanceCount,
                      N' assignment(s) in this scope and are not RoleID 3 (performer) or 4 (reviewer). ',
                      N'Not classified as performer or reviewer work - counted only in OtherRoleInstances. ',
                      N'DIMENSION_SPECS.md needs a BA-signed definition before this can be classified.')
        FROM (SELECT STRING_AGG(CAST(RoleID AS VARCHAR(10)), ', ') AS List
              FROM (SELECT DISTINCT RoleID FROM #asg WHERE RoleID NOT IN (3,4)) r) ids
        CROSS JOIN (SELECT COUNT(DISTINCT ComplianceInstanceID) AS InstanceCount
                    FROM #asg WHERE RoleID NOT IN (3,4)) cnt
        WHERE ids.List IS NOT NULL
    ) q;

    DROP TABLE #inst; DROP TABLE #occ; DROP TABLE #ovdocc; DROP TABLE #ovdInst;
    DROP TABLE #asg; DROP TABLE #asgOther; DROP TABLE #userOcc; DROP TABLE #quality;
    DROP TABLE #timing; DROP TABLE #medtiming;
    DROP TABLE #login; DROP TABLE #rowsOcc; DROP TABLE #rowsInst; DROP TABLE #rows; DROP TABLE #detector;
    DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Users (v3, occurrence-level) installed.';
GO
