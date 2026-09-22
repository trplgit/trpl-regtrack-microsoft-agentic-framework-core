/*===========================================================================
  RegTrack Insights - Free tier, MONTHLY edition
  Error block 51270-51279.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.

  Codes in use: 51274 detector contract.
  (51270 unused - scope is enforced by the loader, 51230. Member-sum
   reconciliation is enforced by sql/37, 51263.)

  SLOT: USERS - Sunday 2. The people behind the obligations.
  STATUS : PROPOSED - not yet executed against any database. See sql/34.

  -- WHAT THIS WEEK ANSWERS ---------------------------------------------------
    Who is carrying too much?            overdue_concentration (sql/37)
    Whose work slipped last month?       last_month_slippage   (sql/37)
    Whose backlog is liability-heavy?    liability_share       (sql/37)
    Whose backlog is going stale?        chronic_backlog       (sql/37)
    Is work sitting with people who can
      no longer act on it?               deactivated_owner     (here)
    Is anyone checking their own work?   self_review           (here)
  Plus unnamed facts: work with no owner, work with no reviewer, the share of
  all overdue work sitting with the 3 busiest people.

  -- WHY deactivated_owner LEADS ----------------------------------------------
  An item assigned to a deactivated account is not late because someone is
  slow - no one CAN do it. It stays invisible until it is overdue. sql/01
  records the BA ruling: deactivated users are kept and FLAGGED, never hidden.

  -- WHY self_review ----------------------------------------------------------
  The performer and the reviewer on an open item are the same person: the
  four-eyes control the review step exists for is absent. A control failure,
  not a performance issue - reported as a count per person, never a rate.

  -- PEOPLE AS MEMBERS ----------------------------------------------------------
  Members = every user who is the performer on a scoped schedule (schedule
  first - 99.8% populated) OR the instance-level performer (ComplianceAssignment
  RoleID 3) on a scoped obligation. Not from the facts alone: a person holding
  obligations with nothing due in the window is still a member.
  A performer id with no row in [User] is kept, flagged with deactivated_owner
  ("not an active user"), and declared in data_quality.

  -- PERSON NAMES -------------------------------------------------------------
  Business decision: named (spec Sec.4). @AllowPersonNames = 0 withholds every
  person name at the data layer (EntityLabel NULL -> "one person") - the
  rollback lever if a legal / HR review comes back negative. No redeploy.

  -- CONTRACT - FIVE RESULT SETS (identical shape in every monthly slot) ------
    1. control_totals  2. facts  3. detector_policy  4. candidates  5. data_quality
  See sql/36 for the column-level contract of facts and candidates.

  IDEMPOTENT. PURE ASCII (CLAUDE.md Sec.5a). Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Users', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Users;
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
    @MaxPerDetector      INT          = 5
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
         @MemberFloor = @MemberFloor, @MaxPerDetector = @MaxPerDetector;

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

    IF @deactMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_deactivated_owner', @deactHold, N'people holding open work are no longer active users - a pattern across your scope', 'patterns', 690, 'stock', 'operational_continuity', 1, 0, 1);

    IF @selfMode = 'aggregate'
        INSERT #facts (FactKey, FactValue, DisplayLabel, Section, DisplayOrder, WindowScope, ImpactClass, SeverityTier, AsAtRequired, HeadlineRank)
        VALUES ('pat_self_review', @selfRevPeople, N'people review their own work on open items - a pattern across your scope', 'patterns', 740, 'stock', 'operational_continuity', 2, 0, 6);

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
END
GO

PRINT 'Free monthly users installed (usp_Insights_FreeMonthly_Users).';
GO
