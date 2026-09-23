/*===========================================================================
  RegTrack Insights - Free tier, MONTHLY edition
  Error block 51260-51269.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.

  Codes in use: 51261 unmapped schedule | 51262 unknown member |
  51263 member sums do not tie | 51264 detector contract |
  51265 input missing.   (51260 unused - scope is owned by the loaders.)

  SHARED MEMBER DETECTORS - one implementation of the four insights every
  dimension week needs, so Users (sql/38), Location (sql/39) and Act (sql/40)
  cannot drift apart in how they define them:

    last_month_slippage    PERIOD. Share of LAST MONTH's items still open,
                           vs the tenant's own share. (Previous month only -
                           a rate over the running month would read unfinished
                           work as failure.)
    liability_share        Share of a member's overdue work that carries
                           personal criminal liability, vs the tenant's share.
    chronic_backlog        Share of a member's overdue work open > 90 days,
                           vs the tenant's share.
    overdue_concentration  A member's share of ALL overdue work, vs a fair
                           share (1 / members with overdue work).

  All four are PEER-RELATIVE (a multiple of this tenant's own figure - never an
  absolute constant), share one materiality floor with a declared fallback,
  suppress comparatives below 2 members, and go through the Sec.4 emission
  policy with the Flagged >= 2 rule for aggregate mode (see sql/36 for why).

  STATUS : PROPOSED - not yet executed against any database. See sql/34.

  -- CONTRACT: THE CALLER CREATES, THIS PROC READS/FILLS, NO RESULT SET -----
  Reads  : #inst, #sched (filled by sql/34), and the caller's member tables:

    CREATE TABLE #mem (                         -- the FULL member list, built
        MemberId     BIGINT        NOT NULL PRIMARY KEY,  -- from the dimension's
        MemberLabel  NVARCHAR(MAX) NULL        -- master, never from the facts.
    );                                          -- NULL label = never named.
    CREATE TABLE #smap (                        -- each schedule -> at most one
        ComplianceScheduleOnID BIGINT NOT NULL PRIMARY KEY,   -- member
        MemberId               BIGINT NOT NULL
    );

  Fills  : #mm (per-member metrics), #detector, #cand, and appends to #facts.
  Exact DDL for #mm below; #detector / #cand / #facts are the shared slot
  shapes - copy them verbatim from sql/38.

  [MAINTENANCE TRAP] sql/36, 38, 39, 40 and 41 each declare these temp tables
  themselves (T-SQL has no shared type for a temp table). The declarations
  MUST stay byte-identical across all five files. Adding a column to #cand or
  #facts is a five-file edit - no compiler catches a missed file, and the
  install succeeds regardless (deferred name resolution). The failure only
  appears at run time, as "Invalid column name". Diff all five before
  deploying any change to a shared shape.

    CREATE TABLE #mm (
        MemberId            BIGINT NOT NULL PRIMARY KEY,
        LmDue INT NOT NULL, LmOnTime INT NOT NULL, LmLate INT NOT NULL, LmOpen INT NOT NULL,
        TmDue INT NOT NULL, TmOpen INT NOT NULL,
        RmDue INT NOT NULL, RmLiab INT NOT NULL, RmNoOwner INT NOT NULL,
        OpenItems INT NOT NULL, OverdueItems INT NOT NULL, Overdue90Items INT NOT NULL,
        LiabOverdueItems INT NOT NULL, NeverTouchedOverdue INT NOT NULL, NoOwnerOpen INT NOT NULL,
        EligSlip BIT NOT NULL DEFAULT 0, FlagSlip BIT NOT NULL DEFAULT 0,
        EligLiab BIT NOT NULL DEFAULT 0, FlagLiab BIT NOT NULL DEFAULT 0,
        EligChron BIT NOT NULL DEFAULT 0, FlagChron BIT NOT NULL DEFAULT 0,
        EligConc BIT NOT NULL DEFAULT 0, FlagConc BIT NOT NULL DEFAULT 0
    );

  -- UNITS --------------------------------------------------------------------
  Every count here is ITEMS (schedules - individual due dates), the same unit
  as the slot facts, so "{{NAME}} holds 38 of them" is a true sentence.

  -- PRIORITY (lower leads; slot procs use 1 and 6+) ---------------------------
      2 last_month_slippage   period-scoped - leads, so the email changes monthly
      3 liability_share       tier 1 severity, but stock
      4 chronic_backlog       stock
      5 overdue_concentration stock

  -- TUNABLES - all parameters, so retuning is a config change, not a redeploy -
      @RelativeRiskFactor   flag at >= this multiple of the tenant's own share
      @ConcentrationFactor  flag at >= this multiple of a fair share
      @MemberFloor          min items for a member to be compared
      @MaxPerDetector       candidates kept per detector (Sec.4: top 5)

  IDEMPOTENT. PURE ASCII (CLAUDE.md Sec.5a). Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_MemberDetectors', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeMonthly_MemberDetectors;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_MemberDetectors
    @EntityKind           VARCHAR(20),          -- location | act | person
    @EntityPlural         NVARCHAR(40),         -- "locations" | "laws" | "people" - fact labels only
    @RelativeRiskFactor   DECIMAL(4,2) = 1.50,
    @ConcentrationFactor  DECIMAL(4,2) = 2.00,
    @MemberFloor          INT          = 5,
    @MaxPerDetector       INT          = 5
AS
BEGIN
    SET NOCOUNT ON;

    IF @EntityKind IS NULL OR @EntityPlural IS NULL OR @RelativeRiskFactor IS NULL
       OR @ConcentrationFactor IS NULL OR @MemberFloor IS NULL OR @MaxPerDetector IS NULL
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
END
GO

PRINT 'Free monthly member detectors installed (usp_Insights_FreeMonthly_MemberDetectors).';
GO
