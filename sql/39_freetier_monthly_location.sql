/*===========================================================================
  RegTrack Insights - Free tier, MONTHLY edition
  Error block 51280-51289.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.

  Codes in use: 51281 obligation outside branch list | 51282 rows do not sum |
  51284 detector contract.
  (51280 unused - scope is enforced by the loader, 51230.)

  SLOT: LOCATION - Sunday 3. Where the risk sits.
  STATUS : PROPOSED - not yet executed against any database. See sql/34.

  -- WHAT THIS WEEK ANSWERS ---------------------------------------------------
    Which site slipped last month?           last_month_slippage   (sql/37)
    Where is backlog liability-heavy?        liability_share       (sql/37)
    Where is backlog going stale?            chronic_backlog       (sql/37)
    Where does the backlog pile up?          overdue_concentration (sql/37)
    Which site depends on one person?        single_point_of_failure (here)
    Which sites have nothing configured?     ghost_location          (here)

  -- MEMBERS: THE BRANCH LIST, NOT THE FACTS ---------------------------------
  [TRAP - CLAUDE.md Sec.3] Built from the recipient's authorised branches
  (tvfInsightsScopePairs), never from #inst. A location with ZERO obligations
  would vanish from a fact-driven list - and a childless site with nothing
  configured is itself the coverage finding (sql/05: hid 4 of 16 branches).

  -- single_point_of_failure --------------------------------------------------
  Eligible = locations with >= @SpofFloor OPEN items (the Instances > 0 guard
  sql/05 lacked, which flagged 120 of 99 eligible). Flagged = EVERY open item at
  the site has an owner AND they are all the same person. A site whose open
  work is partly or wholly unowned is not SPOF - its problem is "no owner",
  reported as a fact (COUNT(DISTINCT) skips NULL owners, so without this guard
  39 unowned items + 1 owned would read as "one performer").

  -- ghost_location -------------------------------------------------------------
  Eligible = every location in scope. Flagged = no obligation configured.
  Emission policy applies: on some tenants ghosts are 55-66% of branches - a
  structural pattern (an HR/ERP location master), reported once, unnamed.

  -- CONTRACT - FIVE RESULT SETS (shared shape - see sql/36 / sql/38) ---------

  IDEMPOTENT. PURE ASCII (CLAUDE.md Sec.5a). Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Location', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Location;
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
      1. MEMBERS = the recipient's authorised branches (incl. empty ones)
    ===================================================================*/
    INSERT #mem (MemberId, MemberLabel)
    SELECT b.BranchID, cb.Name
    FROM (SELECT DISTINCT sp.BranchID FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp) b
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

PRINT 'Free monthly location installed (usp_Insights_FreeMonthly_Location).';
GO
