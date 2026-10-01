/*===========================================================================
  RegTrack Insights - Free tier, MONTHLY edition
  Error block 51290-51299.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.

  Codes in use: 51291 schedule without a law | 51292 rows do not sum |
  51294 detector contract.   (51290 unused - scope is enforced by the loader.)

  SLOT: ACT - Sunday 4. Which laws carry the exposure.
  STATUS : PROPOSED - not yet executed against any database. See sql/34.

  -- WHAT THIS WEEK ANSWERS ---------------------------------------------------
    Which law's work slipped last month?     last_month_slippage   (sql/37)
    Which law's backlog is liability-heavy?  liability_share       (sql/37)
    Which law's backlog is going stale?      chronic_backlog       (sql/37)
    Which law holds the most overdue work?   overdue_concentration (sql/37)
    Which law is overdue at MANY sites?      multi_location_pattern (here)

  -- multi_location_pattern - the COMMONALITY insight -----------------------
  One site overdue on a law is a local problem. The same law overdue across
  many sites is a SYSTEMIC one - usually no central owner for that law, or a
  process nobody runs - and it is fixed once, centrally, not site by site.
      Eligible = laws applicable at >= 2 locations in scope
      Measure  = share of those locations where the law has overdue work
      Flagged  = at >= 2 locations AND share >= @RelativeRiskFactor x the
                 AVERAGE eligible law's share (unweighted - TenantPct on the
                 candidate is this average, so the email compares like with like)
  ItemCount / BaseCount on this detector count LOCATIONS, not items - the
  Metric name says so. Never relate them to an item fact in one sentence.

  -- MEMBERS ---------------------------------------------------------------------
  Laws applicable in scope = distinct Act of the scoped obligations. A law
  with no obligation in scope is not "in scope" - there is no empty-member
  case to preserve here, unlike Location. Act.Name is varchar(MAX): labels are
  NVARCHAR(MAX), never truncated.

  -- CONTRACT - FIVE RESULT SETS (shared shape - see sql/36 / sql/38) ---------

  IDEMPOTENT. PURE ASCII (CLAUDE.md Sec.5a). Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Act', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Act;
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

PRINT 'Free monthly act installed (usp_Insights_FreeMonthly_Act).';
GO
