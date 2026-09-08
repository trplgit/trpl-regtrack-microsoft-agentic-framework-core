/*===========================================================================
  RegTrack Insights - Phase 1b (extension)
  FORWARD RISK DIMENSION

  Pattern     : sql/05_dimension_location.sql (per-branch peer list).
  Emits SIX result sets. Error block 51190-51199.

  -- WHY THIS PROC EXISTS ----------------------------------------------------
  samples/paid_tier_holistic_sample.json carries a forward_pipeline block with
  two fields: due_next_90d and predicted_at_risk. sql/24 (ForwardPipeline,
  deployed) answers the first - WHEN is work due, by window. This proc answers
  the second - WHICH of that work carries present risk factors. They are
  complementary and deliberately separate procedures.

  [RENAMED 2026-09-04] from usp_Insights_Dimension_ForwardPipeline: that name
  is taken by the deployed due-date histogram (sql/24). Error block moved from
  5115x to 5119x.

  -- [TRAP] "PREDICTED" IS A MISNOMER - AND DELIBERATELY NOT IMPLEMENTED ------
  There is no forecast here, and there should not be. A statistical prediction
  would be a black box a CCO cannot challenge and QA cannot test, and it would
  breach the standing rule that every number is explainable and reconcilable.

  What this computes is a COUNT OF OBSERVABLE RISK FACTORS already present on
  each upcoming obligation. Every factor is a fact verifiable today - not an
  estimate of the future.

      "at risk" = "carries N known risk factors right now"
      NOT       = "we forecast a P% chance of being missed"

  Segment is a COMPUTED INDEX, not a raw fact, and must be labelled as such
  wherever rendered (design spec Sec.6.8 rule 6). PredictedAtRisk is emitted only
  for compatibility with the sample's field name; prefer CleanAtRisk.

  -- THE MODEL - SEGMENT FIRST, THEN COUNT FACTORS ---------------------------
  Validated against three production tenants before being written. Two earlier
  designs were tested and discarded, both for reasons only live data showed:

    DISCARDED F5 "sole reviewer": fired on 1,701 of 1,701 instances - 100%. One
      reviewer per instance is the STANDARD RegTrack configuration, not a risk
      differentiator. It also silently inflated every factor count by one, so no
      instance ever scored "none".

    DISCARDED "already behind" as a FACTOR: it conflates a problem that already
      exists with one that might appear, and double-counts what the overdue
      metric already reports. On one tenant it produced the absurd result of
      0% at risk while 3,391 of 3,950 upcoming obligations sat on instances that
      were already overdue.

  What works is to SEGMENT on it instead:

    SEGMENT A  carried_forward - the instance ALREADY has an open overdue
               schedule. A known problem arriving again. Reported, not re-scored.
    SEGMENT B  clean_at_risk   - no existing overdue, but carries >=1 preventable
               risk factor. THIS IS THE ACTIONABLE SET.
    SEGMENT C  healthy         - clean, no factors.

  Factors, applied to segment B only:
    F1 ownerless      no performer assigned. An obligation nobody owns cannot
                      be done by anybody.
    F2 owner_absent   assigned performer deleted or deactivated - work parked
                      with someone who is not there.
    F4 branch_stress  branch overdue rate above 1.5x the TENANT MEDIAN.
                      Peer-relative, never absolute (CLAUDE.md Sec.4). Legitimately
                      fires zero times on tenants with a tight distribution.

  Measured on three production tenants, the segmentation separates them cleanly:
      tenant A  86% carried-forward,  0.0% of clean work at risk
      tenant B  95% carried-forward,  0.4% of clean work at risk
      tenant C  19% carried-forward, 40.8% of clean work at risk
  The first two have a backlog problem; the third has a preventable-risk problem.
  A single blended "at risk" number would have hidden that distinction entirely.

  -- SCOPE - FULL 2-D --------------------------------------------------------
  Unlike sql/14 (events) and sql/21 (licences), this reads ComplianceInstance,
  which resolves a category via Act. Full (branch, category) scope applies.

  -- [DATA] THE FORWARD WINDOW CAN LEGITIMATELY BE EMPTY ---------------------
  Confirmed live. In PRODUCTION the window is healthy - 5-8% of active schedules
  fall in the next 90 days on large tenants (60,527 on one, 109,586 on another).
  But one production tenant had 47 forward schedules of 38,635 (0.12%), and the
  UAT/test database is essentially ALL PAST (one tenant: 54 future of 165,102 =
  0.03%) because its schedule data is frozen.

  An empty forward window is a REAL STATE, not an error: this proc returns
  cleanly with zeroes and declares it. It also means UAT CANNOT MEANINGFULLY
  TEST THIS DIMENSION - use production, or a fixture with seeded future dates.

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_ForwardRisk', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_ForwardRisk;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_ForwardRisk
    @UserID      INT,
    @CustomerID  INT,
    @AsOf        DATETIME = NULL,
    @HorizonDays INT = 90
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();
    IF @HorizonDays IS NULL OR @HorizonDays <= 0 SET @HorizonDays = 90;

    /*-- 0. PRE-FLIGHT - fail closed before computing anything ------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51190, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- THROWs on dictionary gap

    /*-- 1. SCOPED INSTANCE BASE (full 2-D scope) -------------------------*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT s.ComplianceInstanceID, s.BranchID, c.Imprisonment, c.RiskType
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
    JOIN Compliance c ON c.ID = s.ComplianceID;
    CREATE CLUSTERED INDEX IX_inst ON #inst (ComplianceInstanceID);

    /*-- 2. THE FORWARD WINDOW -------------------------------------------
        Only schedules still OPEN count as "due". An occurrence already
        completed ahead of time is not a deadline.                         */
    IF OBJECT_ID('tempdb..#due') IS NOT NULL DROP TABLE #due;
    SELECT cso.ID AS SchedId, i.ComplianceInstanceID, i.BranchID, cso.ScheduleOn,
           i.Imprisonment, i.RiskType,
           DATEDIFF(DAY, @AsOf, cso.ScheduleOn) AS DaysAway
    INTO #due
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    /*  [PERF] explicit latest-status seek, never the 45.7M-row view - see sql/01 */
    OUTER APPLY (SELECT TOP 1 t.StatusId FROM ComplianceTransaction t
                 WHERE t.ComplianceScheduleOnID = cso.ID
                 ORDER BY t.Dated DESC, t.ID DESC) rct
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = rct.StatusId
    WHERE cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn >  @AsOf
      AND cso.ScheduleOn <= DATEADD(DAY, @HorizonDays, @AsOf)
      AND (d.ClosureClass IS NULL OR d.ClosureClass = 'open');
    CREATE CLUSTERED INDEX IX_due ON #due (ComplianceInstanceID);

    /*-- 3. FACTOR INPUTS ------------------------------------------------*/
    IF OBJECT_ID('tempdb..#owned') IS NOT NULL DROP TABLE #owned;
    /*  [CORRECTED 2026-09-05] F1 previously fired on any instance lacking a
        ComplianceAssignment row - which included 34,171 instances that DO have a
        performer named on every schedule. That overstated the factor 181x and
        inflated the clean_at_risk segment this proc exists to produce.

        F1 now means NO OWNER ANYWHERE - neither mechanism. The weaker signal
        (instance-level assignment missing) is still highly predictive but is a
        DIFFERENT factor, reported separately as F1b.                          */
    IF OBJECT_ID('tempdb..#ownership') IS NOT NULL DROP TABLE #ownership;
    SELECT o.ComplianceInstanceID, o.NoInstanceOwner, o.NoOwnerAnywhere, o.OwnerClass
    INTO #ownership
    FROM dbo.tvfInsightsOwnership(@UserID, @CustomerID) o;
    CREATE CLUSTERED INDEX IX_ownership ON #ownership (ComplianceInstanceID);

    SELECT ca.ComplianceInstanceID,
           MAX(CASE WHEN u.IsDeleted = 1 OR u.IsActive = 0 THEN 1 ELSE 0 END) AS AnyAbsentPerformer
    INTO #owned
    FROM ComplianceAssignment ca
    JOIN [User] u ON u.ID = ca.UserID
    WHERE ca.RoleID = 3 AND ca.UserID > 0
      AND EXISTS (SELECT 1 FROM #due d WHERE d.ComplianceInstanceID = ca.ComplianceInstanceID)
    GROUP BY ca.ComplianceInstanceID;

    IF OBJECT_ID('tempdb..#behind') IS NOT NULL DROP TABLE #behind;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #behind
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    WHERE EXISTS (SELECT 1 FROM #due d WHERE d.ComplianceInstanceID = o.ComplianceInstanceID);

    /*  F4 is PEER-RELATIVE. Branch overdue rate is compared to the tenant
        MEDIAN, never a fixed number - an absolute threshold flagged 60% of one
        tenant's members when tried elsewhere (CLAUDE.md Sec.4).            */
    IF OBJECT_ID('tempdb..#branchRate') IS NOT NULL DROP TABLE #branchRate;
    CREATE TABLE #branchRate (
        BranchID    INT NOT NULL PRIMARY KEY,
        Instances   INT NOT NULL,
        Overdue     INT NOT NULL,
        OverduePct  DECIMAL(5,1) NULL
    );
    INSERT #branchRate (BranchID, Instances, Overdue)
    SELECT i.BranchID, COUNT(*),
           SUM(CASE WHEN ov.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END)
    FROM #inst i
    LEFT JOIN (SELECT DISTINCT ComplianceInstanceID
               FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf)) ov
           ON ov.ComplianceInstanceID = i.ComplianceInstanceID
    GROUP BY i.BranchID;

    UPDATE #branchRate
       SET OverduePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END;

    DECLARE @medianBranchOverduePct DECIMAL(9,4);
    SELECT TOP 1 @medianBranchOverduePct =
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY OverduePct) OVER ()
    FROM #branchRate WHERE Instances > 0;
    SET @medianBranchOverduePct = ISNULL(@medianBranchOverduePct, 0);

    /*  1.5x the median. Relative, and it degrades sensibly: on a healthy tenant
        few branches qualify; on a struggling tenant the bar rises with the
        median, so this never flags everyone.                               */
    DECLARE @stressThreshold DECIMAL(9,4) = @medianBranchOverduePct * 1.5;

    /*-- 4. PER-INSTANCE RISK FACTORS ------------------------------------
        Instance grain, not schedule grain: an obligation due three times in
        the window is ONE thing to worry about, not three.

        [TRAP] Declared explicitly with every column, including the derived
        ones. Do NOT use SELECT ... INTO then ALTER TABLE ADD - T-SQL resolves
        names for the whole batch up front and the later UPDATE fails.      */
    IF OBJECT_ID('tempdb..#atrisk') IS NOT NULL DROP TABLE #atrisk;
    CREATE TABLE #atrisk (
        ComplianceInstanceID BIGINT      NOT NULL PRIMARY KEY,
        BranchID             INT         NOT NULL,
        Imprisonment         BIT         NULL,
        RiskType             INT         NULL,
        DaysAway             INT         NULL,
        CarriedForward       BIT         NOT NULL,   -- segment A
        F1_NoOwnerAnywhere         BIT         NOT NULL,
    F1b_NoInstanceOwner        BIT         NOT NULL,
        F2_OwnerAbsent       BIT         NOT NULL,
        F4_BranchStress      BIT         NOT NULL,
        FactorCount          INT         NULL,
        Segment              VARCHAR(16) NULL        -- carried_forward|clean_at_risk|healthy
    );

    INSERT #atrisk (ComplianceInstanceID, BranchID, Imprisonment, RiskType, DaysAway,
                    CarriedForward, F1_NoOwnerAnywhere, F1b_NoInstanceOwner, F2_OwnerAbsent, F4_BranchStress)
    SELECT
        d.ComplianceInstanceID,
        MIN(d.BranchID),
        MAX(CAST(d.Imprisonment AS INT)),
        MAX(d.RiskType),
        MIN(d.DaysAway),
        CASE WHEN MAX(CASE WHEN b.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END) = 1 THEN 1 ELSE 0 END,
        CASE WHEN MAX(CAST(ISNULL(own.NoOwnerAnywhere,0) AS INT)) = 1 THEN 1 ELSE 0 END,   -- MAX over BIT is invalid in T-SQL
        CASE WHEN MAX(CAST(ISNULL(own.NoInstanceOwner,0) AS INT)) = 1 THEN 1 ELSE 0 END,
        CASE WHEN MAX(ISNULL(o.AnyAbsentPerformer,0)) = 1 THEN 1 ELSE 0 END,
        CASE WHEN @medianBranchOverduePct > 0
                  AND MAX(ISNULL(br.OverduePct,0)) > @stressThreshold THEN 1 ELSE 0 END
    FROM #due d
    LEFT JOIN #owned      o   ON o.ComplianceInstanceID   = d.ComplianceInstanceID
    LEFT JOIN #behind     b   ON b.ComplianceInstanceID   = d.ComplianceInstanceID
    LEFT JOIN #branchRate br  ON br.BranchID              = d.BranchID
    LEFT JOIN #ownership  own ON own.ComplianceInstanceID = d.ComplianceInstanceID
    GROUP BY d.ComplianceInstanceID;

    UPDATE #atrisk
       SET FactorCount = CAST(F1_NoOwnerAnywhere AS INT) + CAST(F2_OwnerAbsent AS INT) + CAST(F4_BranchStress AS INT);  -- F1b is REPORTED, not scored - it is a weaker signal  -- BIT + BIT is not addition in T-SQL

    /*  Segment BEFORE scoring. An instance already carrying an overdue
        occurrence is a known problem arriving again, not a new prediction -
        re-scoring it would double-count what the overdue metric already says. */
    UPDATE #atrisk
       SET Segment = CASE WHEN CarriedForward = 1  THEN 'carried_forward'
                          WHEN FactorCount   >= 1  THEN 'clean_at_risk'
                          ELSE 'healthy' END;

    /*-- 5. PER-BRANCH ROWS ----------------------------------------------
        Built from the BRANCH list, not the fact set, so a branch with nothing
        due still appears - "nothing due here" is itself information.        */
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        BranchID            INT           NOT NULL PRIMARY KEY,
        BranchName          NVARCHAR(300) NULL,
        ScopedInstances     INT           NOT NULL,
        DueInWindow         INT           NOT NULL,   -- distinct instances
        SchedulesInWindow   INT           NOT NULL,   -- occurrences
        DueNext30           INT           NOT NULL,
        DueNext60           INT           NOT NULL,
        DueNext90           INT           NOT NULL,
        ImprisonmentDue     INT           NOT NULL,
        CriticalDue         INT           NOT NULL,
        CarriedForward      INT           NOT NULL,   -- segment A
        CleanAtRisk         INT           NOT NULL,   -- segment B (actionable)
        Healthy             INT           NOT NULL,   -- segment C
        F1_NoOwnerAnywhere        INT           NOT NULL,
    F1b_NoInstanceOwner       INT           NOT NULL,
        F2_OwnerAbsent      INT           NOT NULL,
        F4_BranchStress     INT           NOT NULL,
        CleanAtRiskPct      DECIMAL(5,1)  NULL,       -- of CLEAN work, not of all
        CarriedForwardPct   DECIMAL(5,1)  NULL,
        BranchOverduePct    DECIMAL(5,1)  NULL,
        CleanAtRiskRank     INT           NULL,
        Flags               VARCHAR(200)  NULL
    );

    INSERT #rows (BranchID, BranchName, ScopedInstances, DueInWindow, SchedulesInWindow,
                  DueNext30, DueNext60, DueNext90, ImprisonmentDue, CriticalDue,
                  CarriedForward, CleanAtRisk, Healthy,
                  F1_NoOwnerAnywhere, F1b_NoInstanceOwner, F2_OwnerAbsent, F4_BranchStress)
    SELECT
        cb.ID, cb.Name,
        ISNULL(br.Instances, 0),
        COUNT(DISTINCT a.ComplianceInstanceID),
        ISNULL(sw.SchedCount, 0),
        COUNT(DISTINCT CASE WHEN a.DaysAway <= 30 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.DaysAway <= 60 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.DaysAway <= 90 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.Imprisonment = 1 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.RiskType = 3    THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.Segment = 'carried_forward' THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.Segment = 'clean_at_risk'   THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.Segment = 'healthy'         THEN a.ComplianceInstanceID END),
        ISNULL(SUM(CAST(a.F1_NoOwnerAnywhere    AS INT)), 0),
        ISNULL(SUM(CAST(a.F1b_NoInstanceOwner   AS INT)), 0),
        ISNULL(SUM(CAST(a.F2_OwnerAbsent  AS INT)), 0),
        ISNULL(SUM(CAST(a.F4_BranchStress AS INT)), 0)
    FROM CustomerBranch cb
    JOIN dbo.tvfInsightsEntityTree(@CustomerID) t ON t.BranchID = cb.ID
    LEFT JOIN #branchRate br ON br.BranchID = cb.ID
    LEFT JOIN #atrisk     a  ON a.BranchID  = cb.ID
    LEFT JOIN (SELECT BranchID, COUNT(*) AS SchedCount FROM #due GROUP BY BranchID) sw
           ON sw.BranchID = cb.ID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1
    GROUP BY cb.ID, cb.Name, br.Instances, sw.SchedCount;

    /*  CleanAtRiskPct is deliberately a share of CLEAN work, not of all work.
        Dividing by everything would let a large carried-forward backlog mask a
        serious preventable-risk problem, or vice versa.                      */
    UPDATE r
       SET CleanAtRiskPct    = CASE WHEN (r.CleanAtRisk + r.Healthy) = 0 THEN NULL
                                    ELSE 100.0 * r.CleanAtRisk / (r.CleanAtRisk + r.Healthy) END,
           CarriedForwardPct = CASE WHEN r.DueInWindow = 0 THEN NULL
                                    ELSE 100.0 * r.CarriedForward / r.DueInWindow END,
           BranchOverduePct  = ISNULL(br.OverduePct, 0)
    FROM #rows r LEFT JOIN #branchRate br ON br.BranchID = r.BranchID;

    /*  Rank only branches that actually have something due - "rank 1 of 1" on a
        single-member population is a vacuous claim (CLAUDE.md Sec.4).        */
    ;WITH rk AS (SELECT BranchID, RANK() OVER (ORDER BY CleanAtRiskPct DESC) AS n
                 FROM #rows WHERE (CleanAtRisk + Healthy) > 0)
    UPDATE #rows SET CleanAtRiskRank = rk.n FROM #rows JOIN rk ON rk.BranchID = #rows.BranchID;

    UPDATE #rows
       SET Flags = STUFF(
             CASE WHEN DueInWindow = 0                     THEN ',nothing_due_in_window' ELSE '' END +
             CASE WHEN F1_NoOwnerAnywhere > 0                    THEN ',unowned_work_due'      ELSE '' END +
         CASE WHEN F1b_NoInstanceOwner > 0                   THEN ',no_instance_owner'     ELSE '' END +
             CASE WHEN F2_OwnerAbsent > 0                  THEN ',owner_absent'          ELSE '' END +
             CASE WHEN ImprisonmentDue > 0 AND CleanAtRisk > 0
                                                           THEN ',liability_at_risk'     ELSE '' END +
             CASE WHEN CarriedForwardPct IS NOT NULL AND CarriedForwardPct >= 50.0
                                                           THEN ',backlog_dominated'     ELSE '' END +
             CASE WHEN CleanAtRiskPct IS NOT NULL AND CleanAtRiskPct >= 50.0
                                                           THEN ',majority_of_clean_at_risk' ELSE '' END
           , 1, 1, '');

    /*-- 6. CONTROL TOTALS + RECONCILIATION -------------------------------*/
    DECLARE @dueDistinct INT = (SELECT COUNT(DISTINCT ComplianceInstanceID) FROM #due);
    DECLARE @rowsDue     INT = (SELECT ISNULL(SUM(DueInWindow),0) FROM #rows);

    IF @dueDistinct <> @rowsDue
        THROW 51191, N'FORWARD PIPELINE RECONCILIATION FAILED - per-branch due counts do not tie to the distinct instances in the window. Refusing to publish.', 1;

    DECLARE @scoped INT = (SELECT COUNT(*) FROM #inst);

    SELECT 'control_totals' AS ResultSet,
        @scoped                                                      AS ScopedInstances,
        @HorizonDays                                                 AS HorizonDays,
        @dueDistinct                                                 AS DueInWindow,
        @rowsDue                                                     AS SumOfRowsDue,
        CAST(1 AS BIT)                                               AS Reconciled,
        (SELECT COUNT(*) FROM #due)                                  AS SchedulesInWindow,
        (SELECT COUNT(*) FROM #atrisk WHERE Segment='carried_forward') AS CarriedForward,
        (SELECT COUNT(*) FROM #atrisk WHERE Segment='clean_at_risk')   AS CleanAtRisk,
        (SELECT COUNT(*) FROM #atrisk WHERE Segment='healthy')         AS Healthy,
        (SELECT COUNT(*) FROM #atrisk WHERE Segment='clean_at_risk')   AS PredictedAtRisk,  -- sample-schema alias; prefer CleanAtRisk
        (SELECT COUNT(*) FROM #atrisk WHERE Imprisonment = 1
              AND Segment IN ('carried_forward','clean_at_risk'))      AS ImprisonmentNeedingAttention,
        @medianBranchOverduePct                                      AS TenantMedianBranchOverduePct,
        @stressThreshold                                             AS BranchStressThresholdPct,
        (SELECT COUNT(*) FROM #rows)                                 AS BranchesReported,
        (SELECT COUNT(*) FROM #rows WHERE DueInWindow = 0)           AS BranchesWithNothingDue,
        CASE WHEN @dueDistinct = 0 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS ForwardWindowEmpty,
        N'Segmented: carried_forward = already overdue (known problem recurring); clean_at_risk = no existing overdue but >=1 present risk factor (the actionable set); healthy = neither. NOT a forecast - a count of present facts. Computed index; label as such.' AS Method;

    /*-- 7. ROWS ---------------------------------------------------------*/
    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY CleanAtRisk DESC, CarriedForward DESC, DueInWindow DESC;

    /*-- 8. DETECTOR POLICY (CLAUDE.md Sec.4) ---------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    /*  [FIX] TWO eligible populations, because the flags measure two things.
        Flagged and Eligible MUST come from the same population - the .NET
        contract check rejects the report otherwise, and it is right to.

          @withDue   branches with ANY work due in the window. F1 and F2 count
                     factors across ALL due work, including carried-forward, so
                     this is their denominator.
          @withClean branches with CLEAN work (CleanAtRisk + Healthy > 0). Only
                     the clean-share detectors may use this.

        Using @withClean for unowned_work_due would let a branch whose due work
        is entirely carried-forward be FLAGGED but not ELIGIBLE - the same defect
        that made sql/05 report 120 of 99.                                      */
    DECLARE @withDue   INT = (SELECT COUNT(*) FROM #rows WHERE DueInWindow > 0);
    DECLARE @withClean INT = (SELECT COUNT(*) FROM #rows WHERE (CleanAtRisk + Healthy) > 0);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'unowned_work_due', @withDue,   (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%unowned_work_due%')
    UNION ALL SELECT 'owner_absent',      @withDue,   (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%owner_absent%')
    UNION ALL SELECT 'liability_at_risk', @withClean, (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%liability_at_risk%')
    UNION ALL SELECT 'majority_at_risk',  @withClean, (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%majority_at_risk%');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector SET EmitMode = CASE WHEN Flagged = 0       THEN 'none'
                                         WHEN FlaggedPct > 20.0 THEN 'aggregate'
                                         ELSE 'individual' END;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 9. TYPED ASSERTIONS - comparatives COMPUTED here ----------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(200),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(300) NULL);

    DECLARE @cleanTotal INT = (SELECT COUNT(*) FROM #atrisk WHERE Segment IN ('clean_at_risk','healthy'));
    DECLARE @tenantAtRiskPct DECIMAL(5,1) =
        CASE WHEN @cleanTotal = 0 THEN NULL
             ELSE 100.0 * (SELECT COUNT(*) FROM #atrisk WHERE Segment='clean_at_risk') / @cleanTotal END;

    INSERT #assert VALUES ('A-DUE','due_in_window',N'tenant',@dueDistinct,NULL,NULL,NULL,NULL,NULL,NULL);

    IF @dueDistinct > 0
        INSERT #assert VALUES ('A-RISK','clean_at_risk_pct',N'tenant',@tenantAtRiskPct,NULL,NULL,NULL,NULL,NULL,
            N'computed index: share of CLEAN upcoming work carrying a present risk factor. Not a forecast.');

    /*  Worst branch, only when there is something to compare against.      */
    DECLARE @rankable INT = @withClean;
    IF @rankable >= 2
        INSERT #assert
        SELECT TOP 1 'A-WORST','clean_at_risk_pct',BranchName,CleanAtRiskPct,CleanAtRiskRank,@rankable,
               @tenantAtRiskPct, CleanAtRiskPct - @tenantAtRiskPct, 'worse',
               N'computed index: share of CLEAN upcoming work carrying a present risk factor. Not a forecast.'
        FROM #rows WHERE CleanAtRiskPct IS NOT NULL
        ORDER BY CleanAtRiskPct DESC, DueInWindow DESC;

    IF (SELECT COUNT(*) FROM #atrisk WHERE Imprisonment = 1 AND Segment IN ('carried_forward','clean_at_risk')) > 0
        INSERT #assert
        SELECT 'A-LIAB','imprisonment_at_risk',N'tenant',
               (SELECT COUNT(*) FROM #atrisk WHERE Imprisonment = 1 AND Segment IN ('carried_forward','clean_at_risk')),
               NULL,@dueDistinct,NULL,NULL,NULL,NULL;

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS - each backed by assertion ids ---------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
                        Headline NVARCHAR(300), AssertionIds VARCHAR(400),
                        NarrativeGuard NVARCHAR(400) NULL);

    INSERT #find
    SELECT 'F-LIAB','high',
           CONCAT(N'', CAST(Value AS INT), N' obligations carrying personal liability fall due in the next ',
                  @HorizonDays, N' days and already carry risk factors'),
           AssertionId,
           N'State as items to review. Do NOT present as predicted failures - this is a factor count, not a forecast.'
    FROM #assert WHERE AssertionId = 'A-LIAB';

    IF (SELECT EmitMode FROM #detector WHERE Detector='unowned_work_due') = 'aggregate'
        INSERT #find
        SELECT 'F-UNOWNED-AGG','high',
               CONCAT(N'', Flagged, N' of ', Eligible, N' locations (', FlaggedPct,
                      N'%) have upcoming obligations with no assigned owner'),
               'A-DUE', NULL
        FROM #detector WHERE Detector='unowned_work_due';
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='unowned_work_due') = 'individual'
        INSERT #find
        SELECT TOP 5 'F-UNOWNED','high',
               CONCAT(N'', BranchName, N' has ', F1_NoOwnerAnywhere, N' upcoming obligations with no performer on either mechanism'),
               'A-DUE', NULL
        FROM #rows WHERE Flags LIKE '%unowned_work_due%' ORDER BY F1_NoOwnerAnywhere DESC;

    IF @rankable >= 2
        INSERT #find
        SELECT 'F-WORST','medium',
               CONCAT(N'', ScopeLabel, N' carries the highest share of preventable risk in clean upcoming work at ', Value, N'%'),
               'A-WORST,A-RISK',
               N'Computed index. Say "carries risk factors", never "will be missed".'
        FROM #assert WHERE AssertionId = 'A-WORST';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY - declared, never silent -----------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
        SELECT 'computed_index' AS Issue,
               N'Segments are derived from PRESENT FACTS, not a forecast: carried_forward = already overdue; clean_at_risk = ownerless, absent owner, or branch under stress. No accuracy claim. Label as a computed index wherever rendered.' AS Detail
        UNION ALL
        SELECT 'forward_window_empty',
               CONCAT(N'No obligations fall due in the next ', @HorizonDays,
                      N' days for this scope. This is a real state, not an error - but note that a frozen or stale database will also produce it.')
        WHERE @dueDistinct = 0
        UNION ALL
        SELECT 'branch_stress_unavailable',
               N'Tenant median branch overdue rate is 0, so the peer-relative branch-stress factor (F4) could not be evaluated and was not applied.'
        WHERE @medianBranchOverduePct = 0
        UNION ALL
        SELECT 'flow_metric_drift',
               N'Both the forward window and the already-behind factor read live data and move between runs.'
    ) q;

    DROP TABLE #ownership; DROP TABLE #inst; DROP TABLE #due; DROP TABLE #owned;
    DROP TABLE #behind; DROP TABLE #branchRate; DROP TABLE #atrisk;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Forward pipeline dimension installed.';
GO
