/*===========================================================================
  RegTrack Insights - Free tier, MONTHLY edition
  Error block 51300-51309.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.

  Codes in use: 51301 licence states do not partition | 51302 per-location
  sums do not tie | 51303 typed + untyped do not tie | 51304 detector contract.
  (51300 unused - scope and the licence dictionary are enforced by sql/35.)

  SLOT: LICENCE - Sunday 5 (only in months that have one). The right to operate.
  STATUS : PROPOSED - not yet executed against any database. See sql/34.

  -- WHAT THIS WEEK ANSWERS ---------------------------------------------------
    What expires before month end with no
      renewal filed?                        licence_expiring_unrenewed  (event)
    What lapsed in the last two months and
      still has no renewal?                 licence_lapsed_recent_unrenewed (event)
    Which sites are carrying expired,
      unrenewed licences?                   expired_unrenewed_location  (detector)
    Which licence types lapse most?         licence_type_lapse_rate     (detector)

  -- WHY "UNRENEWED" IS THE SPLIT THAT MATTERS -------------------------------
  sql/01: of 1,554 lapsed licences on one tenant, 311 had a renewal in progress
  and 1,228 showed no visible action. A lapse with a renewal filed is being
  handled; a lapse with nothing filed is the finding. Every licence fact here
  carries that split.

  -- EVENTS vs DETECTORS -------------------------------------------------------
  An expiry or a lapse is a DATED FACT about one licence - not a pattern - so
  the two event lists are bounded by the 2-name cap, not the emission policy.
  The two per-member measures ARE patterns and go through Sec.4 (with the
  Flagged >= 2 rule for aggregate mode - see sql/36).

  -- SOURCE / SCOPE --------------------------------------------------------------
  sql/35 (the licence register, BA lapse rulings of sql/21 replicated). Scope
  is branch-only - declared in data_quality on every run.

  -- A TENANT WITH NO LICENCES STILL GETS THIS EMAIL -------------------------
  The schedule is calendar-pure (spec Sec.2): no substitution of another topic.
  Every fact is 0, no candidates, and data_quality says so.

  -- CONTRACT - FIVE RESULT SETS (shared shape - see sql/36 / sql/38) ---------

  IDEMPOTENT. PURE ASCII (CLAUDE.md Sec.5a). Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Licence', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Licence;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeMonthly_Licence
    @UserID              INT,
    @CustomerID          INT,
    @CurrMonthStart      DATE,
    @AsOf                DATETIME,
    @RelativeRiskFactor  DECIMAL(4,2) = 1.50,
    @TypeFloor           INT          = 10,    -- min licences for a type to be compared (declared fallback to 1)
    @MaxPerDetector      INT          = 5
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
    DECLARE @total INT, @valid INT, @lapsed INT, @endedOther INT, @noEnd INT,
            @lapsedUnrenewed INT, @lapsedRenewing INT,
            @expRom INT, @expRomUnrenewed INT, @expRomRenewing INT,
            @lapsedLm INT, @lapsedLmUnrenewed INT, @lapsedTm INT, @lapsedTmUnrenewed INT,
            @untyped INT, @noStatusRow INT;

    SELECT @total             = COUNT(*),
           @valid             = ISNULL(SUM(CASE WHEN LicenceState = 'valid'       THEN 1 ELSE 0 END), 0),
           @lapsed            = ISNULL(SUM(CASE WHEN LicenceState = 'lapsed'      THEN 1 ELSE 0 END), 0),
           @endedOther        = ISNULL(SUM(CASE WHEN LicenceState = 'ended_other' THEN 1 ELSE 0 END), 0),
           @noEnd             = ISNULL(SUM(CASE WHEN LicenceState = 'no_end_date' THEN 1 ELSE 0 END), 0),
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

    IF @total <> @valid + @lapsed + @endedOther + @noEnd
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
END
GO

PRINT 'Free monthly licence installed (usp_Insights_FreeMonthly_Licence).';
GO
