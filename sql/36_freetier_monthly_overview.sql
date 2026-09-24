/*===========================================================================
  RegTrack Insights - Free tier, MONTHLY edition
  Error block 51250-51259.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.

  Codes in use: 51251 window outcome decomposition | 51252 overdue age bands |
  51253 licence partition | 51254 detector contract.
  (51250 unused - scope is enforced by the loaders, 51230 / 51240.
   51255-51259 unused - dictionary checks are owned by the loaders.)

  SLOT: OVERVIEW - the Sunday-1 email of every month.
  Two calendar months, whole estate, presented in reading order:
      what happened last month  ->  where things stand today  ->  what is
      coming before month end   ->  licences
  (The email never labels these "past / present / future" - that is the
   reading order only.)

  STATUS : PROPOSED - not yet executed against any database. See sql/34.

  -- CONTRACT - FIVE RESULT SETS, in this order ------------------------------
    1. control_totals   reconciled counts + the window, for provenance
    2. facts            the COMPLETE closed set the narrative may cite:
                        FactKey, FactValue (INT), DisplayLabel, Section,
                        DisplayOrder, WindowScope, ImpactClass, SeverityTier,
                        AsAtRequired, IsHeadline
    3. detector_policy  sql/05 shape + Note
    4. candidates       up to 5 per detector, individual-mode only. DefaultSlot
                        1 / 2 marks the (max) 2 the email names by default;
                        C# may re-pick but never exceed 2. The ONLY entity
                        names this email may carry come from this grid.
    5. data_quality     declared gaps

  -- WHY THE FACTS GRID CARRIES ImpactClass AND SeverityTier -----------------
  "What matters and how bad it is" is a JUDGEMENT ABOUT FACTS. Non-negotiable
  #1 keeps that out of the LLM. So SQL classifies every fact:

      ImpactClass     personal_liability      criminal liability for a named officer
                      licence_continuity      the right to operate lapses
                      operational_continuity  work with no owner / never started / aged
                      performance             closure outcome
                      volume                  context only
      SeverityTier    1 (most severe) .. 5 (context)
      IsHeadline      exactly one fact, picked by an explicit BA priority
                      (HeadlineRank - see section 6), so the email always
                      leads with what matters most this month

  The narrative's job is ordering and plain-English prose around a lead the
  data layer already chose. The validator can check every one of those claims.

  -- WHY PREVIOUS-MONTH FACTS ARE A DUE-DATE COHORT --------------------------
  "Last month" = obligations whose DUE DATE fell in the previous calendar
  month, with their outcome AS AT @AsOf. Not "closures that happened last
  month" - that mixes in work due in other months and cannot be decomposed.
  The cohort decomposes exactly (invariant 51251):

      due = on_time + late + untimed + closed_without_completion + still_open

  and the published rate is on_time / completed (sql/05's convention), never
  on_time / due - status 15 (reviewer-final n.a., ~1.1M schedules system-
  wide) carries no timeliness and would drag a /due rate down on every tenant.
  The rate is FLOORED: 99.6% prints as 99, so "100% on time" can only ever
  appear when it is true.

  Late closures for last month can still land after @AsOf, so every prev-
  month fact carries AsAtRequired = 1; the renderer states the as-at date.

  -- THIS MONTH: NO RATIOS ----------------------------------------------------
  The current month is 0-6 days old on Sunday 1. It is reported as STOCK
  (what is already past due and open) and FORWARD (what falls due before month
  end, with its liability, criticality and ownership) - never as a rate.

  -- DETECTORS (CLAUDE.md Sec.4 emission policy, sql/05 shape) ---------------
    liability_overdue_location  members = locations holding >= 1 obligation that
                                carries personal criminal liability. Flagged when
                                the location's overdue rate ON THOSE obligations is
                                >= @RelativeRiskFactor x the tenant's own rate.
    category_overdue_skew       members = compliance categories at or above the
                                materiality floor. Flagged when the category's
                                overdue rate is >= @RelativeRiskFactor x the
                                tenant's own overall overdue rate.
  Both thresholds are PEER-RELATIVE (a multiple of this tenant's own rate),
  never an absolute constant. Fewer than 2 members -> comparatives suppressed
  ("rank 1 of 1" is vacuous). Materiality floor with a declared fallback to a
  wider sample when fewer than 2 members meet it (degraded_peer_sample).

  -- NAMED FINDINGS - the free-tier disclosure cap ----------------------------
  [2026-09-23] C# now names up to FOUR (DefaultSlot 1-2 here, 3-4 from the
  remaining ranked rows), and an aggregate-mode detector additionally emits
  up to @MaxExamples EXAMPLE members in grid #6 (counts only, never a rate) -
  see docs/superpowers/specs/2026-09-23-aggregate-mode-examples-design.md.
  Error codes 51255/51256 (examples contract) sit outside the x1-x4
  reconciliation range because that range was already full in this file.
  Original design, still how DefaultSlot is assigned:
  At most TWO per email, chosen deterministically:
      priority 1  licence_expiring_unrenewed  - the soonest licence expiring
                  before month end with no renewal filed. A DATED EVENT, not a
                  pattern detector: it is a fact about one licence, so the
                  emission policy (which exists to stop pattern floods) does
                  not apply; the 2-name cap does.
      priority 2  liability_overdue_location  - only when EmitMode = individual
      priority 3  category_overdue_skew       - only when EmitMode = individual
  An aggregate-mode detector is reported as a count, never named.
  Every named row carries ProblemCount (members with the same problem) and
  ResidualCount (= ProblemCount - 1, the ones NOT named).
  Names are emitted here as data; C# binds them into {{NAME_n}} placeholders
  AFTER validation - the LLM never writes an entity name.

  -- EXPOSURE, NOT MONEY -----------------------------------------------------
  No rupee figures. Compliance.FixedMaximum / VariableAmountPerDay exist but
  their population and summation semantics (per occurrence? per day? per
  prosecution?) are unverified, and a summed statutory maximum is a number
  nobody has signed off. Impact is carried by counts + liability class, which
  are verified. Revisit with BA sign-off (Phase 2).

  IDEMPOTENT. PURE ASCII (CLAUDE.md Sec.5a). Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Overview', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Overview;
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
    DECLARE @licTotal INT, @licValid INT, @licLapsed INT, @licEndedOther INT, @licNoEnd INT,
            @licLapsingRom INT, @licLapsingUnrenewed INT, @licLapsedLastMonth INT,
            @licLapsedThisMonth INT, @licLapsedUnrenewed INT;

    SELECT @licTotal            = COUNT(*),
           @licValid            = ISNULL(SUM(CASE WHEN LicenceState = 'valid'       THEN 1 ELSE 0 END), 0),
           @licLapsed           = ISNULL(SUM(CASE WHEN LicenceState = 'lapsed'      THEN 1 ELSE 0 END), 0),
           @licEndedOther       = ISNULL(SUM(CASE WHEN LicenceState = 'ended_other' THEN 1 ELSE 0 END), 0),
           @licNoEnd            = ISNULL(SUM(CASE WHEN LicenceState = 'no_end_date' THEN 1 ELSE 0 END), 0),
           @licLapsingRom       = ISNULL(SUM(CASE WHEN LapsesRestOfMonth = 1 THEN 1 ELSE 0 END), 0),
           @licLapsingUnrenewed = ISNULL(SUM(CASE WHEN LapsesRestOfMonth = 1 AND RenewalInProgress = 0 THEN 1 ELSE 0 END), 0),
           @licLapsedLastMonth  = ISNULL(SUM(CASE WHEN LapsedLastMonth = 1 THEN 1 ELSE 0 END), 0),
           @licLapsedThisMonth  = ISNULL(SUM(CASE WHEN LapsedThisMonth = 1 THEN 1 ELSE 0 END), 0),
           @licLapsedUnrenewed  = ISNULL(SUM(CASE WHEN LicenceState = 'lapsed' AND RenewalInProgress = 0 THEN 1 ELSE 0 END), 0)
    FROM #lic;

    IF @licTotal <> @licValid + @licLapsed + @licEndedOther + @licNoEnd
        THROW 51253, N'FREE MONTHLY OVERVIEW RECONCILIATION FAILED - licence states do not partition the scoped licence set. Refusing to publish.', 1;

    /*===================================================================
      4. ESTATE CONTEXT
    ===================================================================*/
    DECLARE @obligations      INT = (SELECT COUNT(*) FROM #inst);
    DECLARE @locationsInScope INT = (SELECT COUNT(DISTINCT sp.BranchID)
                                     FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp);
    DECLARE @locationsWithObl INT = (SELECT COUNT(DISTINCT BranchID) FROM #inst);

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

PRINT 'Free monthly overview installed (usp_Insights_FreeMonthly_Overview).';
GO
