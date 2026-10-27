/*===========================================================================
  RegTrack Insights - v3 (2026-10-27): occurrence-level Internal
  Object : dbo.usp_Insights_Dimension_Internal
  Base   : sql/v2/34 (v2 + the 2026-10-06 real-count columns), which is what the
           repo holds; production still runs v2 WITHOUT those two columns.

  Change 1 - occurrence grain, same as sql/v2/37-43 (2026-10-01). Internal was
  the one shared-scope dimension the occurrence conversion skipped: its
  StatutoryInstances/StatutoryOverdue/InternalInstances/InternalOverdue (and
  SumOfRows/SumOfInternalRows/StatutoryOverdueInstances/InternalOverdueInstances)
  counted one row per distinct obligation, while every other dimension - and
  RegTrack's own Detailed Report - counts one row per real due date in the
  window. Found live 2026-10-27, tenant 1926: Internal SumOfRows 812 vs Risk
  SumOfRows 1,897 for the same user and window. ScopedInstances and
  InternalInstances stay DISTINCT obligation counts (their names promise that);
  SumOfRows/SumOfInternalRows are now the occurrence totals the rows sum to.
  Ownership (NoInstanceOwner) stays instance-level - it describes the
  obligation, not each due date - same reasoning as Location/Risk v3.

  Change 2 - internal overdue now uses the LATEST transaction of each internal
  due date (newest Dated, then highest ID). v2 joined EVERY transaction, so a
  due date that was Open once and has since been closed still counted as
  overdue. The statutory side already used the latest status
  (tvfInsightsOverdueSchedules).

  Change 3 - the internal window gate now counts only active due dates
  (IsActive = 1 AND IsUpcomingNotDeleted = 1), the same rule the statutory gate
  and the internal overdue check already used. v2 let a switched-off internal
  due date pull its obligation into the window.

  Carried over unchanged from v2: the 2026-09-30 window-occurrence overdue fix,
  the 2026-10-05 NVARCHAR(600) widening, the 2026-10-06 real-count columns
  (StatutoryNoInstanceOwnerInstances / InternalNoInstanceOwnerInstances).

  Error block: unchanged (51110-51119).
===========================================================================*/
SET NOCOUNT ON;
GO
IF OBJECT_ID('dbo.usp_Insights_Dimension_Internal', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Internal;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Internal
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
        THROW 51110, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. THE TWO POPULATIONS -----------------------------------------*/
    IF OBJECT_ID('tempdb..#branch') IS NOT NULL DROP TABLE #branch;
    SELECT DISTINCT sp.BranchID
    INTO #branch
    FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp;

    IF OBJECT_ID('tempdb..#stat') IS NOT NULL DROP TABLE #stat;
    SELECT s.ComplianceInstanceID, s.BranchID
    INTO #stat
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;

    CREATE CLUSTERED INDEX IX_stat ON #stat (ComplianceInstanceID);

    /*  [ADDED 2026-09-25] HARD WINDOW GATE, STATUTORY SIDE - caller-supplied period,
        no FY default. #stat carries no per-row date - real due dates live on
        ComplianceScheduleOn, one row per recurring occurrence. Same proven pattern
        as sql/23 TimelinessFY and sql/11 Act: #stat is materialised and indexed
        above, so find which of those instances had >=1 scheduled occurrence in the
        window, then narrow #stat itself down to just those. Every downstream step
        (statutory overdue, ownership, per-branch rollup, reconciliation) already
        reads from #stat, so it inherits the window for free.
        Do NOT reintroduce a join hint or skip the materialise-first step - the same
        shape of query against ComplianceScheduleOn's 29.4M rows without it measured
        183,502 ms on a real tenant versus 157 ms scoped-first (sql/23's own numbers).
        @WindowStart/@WindowEnd are validated once here and reused for the internal
        side's own gate below - they govern the whole dimension, not just #stat.      */
    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51112, N'INTERNAL DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51112, N'INTERNAL DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    /*  [v3 2026-10-27] #statOcc = one row per active statutory DUE DATE in the window
        (occurrence grain, RegTrack parity). #stat stays the DISTINCT obligation list. */
    IF OBJECT_ID('tempdb..#statOcc') IS NOT NULL DROP TABLE #statOcc;
    SELECT cso.ID AS ScheduleOnID, s.ComplianceInstanceID, s.BranchID, cso.ScheduleOn
    INTO #statOcc
    FROM #stat s
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = s.ComplianceInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd
      AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1;   -- [v2] switched-off due dates do not count (RegTrack parity)
    CREATE CLUSTERED INDEX IX_statOcc ON #statOcc (ScheduleOnID);
    CREATE NONCLUSTERED INDEX IX_statOcc_inst ON #statOcc (ComplianceInstanceID);

    DELETE s FROM #stat s
    WHERE NOT EXISTS (SELECT 1 FROM #statOcc a WHERE a.ComplianceInstanceID = s.ComplianceInstanceID);

    /*  [v3 2026-10-27] Overdue DUE DATES, not overdue obligations: only an in-window
        occurrence whose own latest status is overdue counts.
        [OPTIMIZED, same fix as Location/Departments/Act/Entity/Users sql/v2/37-43,
        2026-10-06] No tvfInsightsOverdueSchedules: it re-derives scope and active-
        performer membership for the WHOLE tenant before being narrowed to #statOcc
        (measured 132 s vs 0.4 s on tenant 1926 when joined on the schedule id).
        Latest transaction per in-scope schedule, fetched once per INSTANCE from
        #stat. Semantics unchanged: a schedule with NO transaction is not overdue. */
    IF OBJECT_ID('tempdb..#lt') IS NOT NULL DROP TABLE #lt;
    SELECT x.ComplianceScheduleOnID, x.StatusId, x.ID
    INTO #lt
    FROM (
        SELECT t.ComplianceScheduleOnID, t.StatusId, t.ID,
               ROW_NUMBER() OVER (PARTITION BY t.ComplianceScheduleOnID
                                  ORDER BY t.Dated DESC, t.ID DESC) AS rn
        FROM #stat s
        JOIN dbo.ComplianceTransaction t
              ON t.ComplianceInstanceId = s.ComplianceInstanceID
    ) x
    WHERE x.rn = 1;
    CREATE CLUSTERED INDEX IX_lt ON #lt (ComplianceScheduleOnID);

    IF OBJECT_ID('tempdb..#statOvd') IS NOT NULL DROP TABLE #statOvd;
    SELECT o.ScheduleOnID
    INTO #statOvd
    FROM #statOcc o
    JOIN #lt lt                       ON lt.ComplianceScheduleOnID = o.ScheduleOnID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = lt.StatusId
    WHERE d.OverdueEligible = 1
      AND o.ScheduleOn < CAST(@AsOf AS DATE);
    CREATE CLUSTERED INDEX IX_statOvd ON #statOvd (ScheduleOnID);

    IF OBJECT_ID('tempdb..#statOwn') IS NOT NULL DROP TABLE #statOwn;
    /*  [CORRECTED 2026-09-05] Statutory ownership has TWO mechanisms - see sql/01.
        #statOwn keeps its original meaning (instance-level) so the rest of this
        proc is unchanged; #statOwnership carries the full picture.

        NOTE: InternalComplianceScheduleOn has NO performer column, so internal
        compliance has only ONE mechanism and #intOwn below is already correct.
        The two populations are therefore NOT measured the same way - that is a
        schema fact, and it is declared in data_quality below.                  */
    IF OBJECT_ID('tempdb..#statOwnership') IS NOT NULL DROP TABLE #statOwnership;
    SELECT o.ComplianceInstanceID, o.NoInstanceOwner, o.NoOwnerAnywhere
    INTO #statOwnership
    FROM dbo.tvfInsightsOwnership(@UserID, @CustomerID) o;
    CREATE CLUSTERED INDEX IX_statOwnership ON #statOwnership (ComplianceInstanceID);

    SELECT ComplianceInstanceID INTO #statOwn
    FROM #statOwnership WHERE NoInstanceOwner = 0;

    /*  Internal - branch-axis scoping only, see the header. */
    IF OBJECT_ID('tempdb..#int') IS NOT NULL DROP TABLE #int;
    SELECT ii.ID AS InternalInstanceID, ii.CustomerBranchID AS BranchID
    INTO #int
    FROM InternalComplianceInstance ii
    JOIN CustomerBranch cb ON cb.ID = ii.CustomerBranchID
    JOIN #branch b ON b.BranchID = ii.CustomerBranchID
    WHERE ii.IsDeleted = 0 AND cb.IsDeleted = 0 AND cb.Status = 1 AND cb.CustomerID = @CustomerID
      -- [v2 2026-09-29] RegTrack parity: an internal obligation needs an active performer
      AND EXISTS (SELECT 1 FROM InternalComplianceAssignment ica JOIN [User] u ON u.ID = ica.UserID
                  WHERE ica.InternalComplianceInstanceID = ii.ID AND ica.RoleID = 3
                    AND u.CustomerID = @CustomerID AND u.IsDeleted = 0 AND u.IsActive = 1);

    CREATE CLUSTERED INDEX IX_int ON #int (InternalInstanceID);

    /*  [ADDED 2026-09-25] HARD WINDOW GATE, INTERNAL SIDE - caller-supplied period,
        no FY default. #int carries no per-row date either - real due dates live on
        InternalComplianceScheduledOn (a SEPARATE schema family from the statutory
        side's ComplianceScheduleOn, see the header note), one row per recurring
        occurrence. #int is materialised and indexed above, so find which of those
        instances had >=1 scheduled occurrence in the window, then narrow #int
        itself down to just those. Every downstream step (internal overdue,
        ownership, per-branch rollup, reconciliation) already reads from #int, so
        it inherits the window for free. @WindowStart/@WindowEnd were already
        validated NOT NULL / not inverted on the statutory side above - not
        re-checked here, same params govern both populations.                      */
    /*  [v3 2026-10-27] #intOcc = one row per active internal DUE DATE in the window.
        Active only (IsActive/IsUpcomingNotDeleted) - v2 let a switched-off due date
        pull its obligation into the window. */
    IF OBJECT_ID('tempdb..#intOcc') IS NOT NULL DROP TABLE #intOcc;
    SELECT iso.ID AS ScheduledOnID, i.InternalInstanceID, i.BranchID, iso.ScheduledOn
    INTO #intOcc
    FROM #int i
    JOIN InternalComplianceScheduledOn iso ON iso.InternalComplianceInstanceID = i.InternalInstanceID
    WHERE iso.ScheduledOn >= @WindowStart AND iso.ScheduledOn < @WindowEnd
      AND iso.IsActive = 1 AND iso.IsUpcomingNotDeleted = 1;
    CREATE CLUSTERED INDEX IX_intOcc ON #intOcc (ScheduledOnID);
    CREATE NONCLUSTERED INDEX IX_intOcc_inst ON #intOcc (InternalInstanceID);

    DELETE i FROM #int i
    WHERE NOT EXISTS (SELECT 1 FROM #intOcc a WHERE a.InternalInstanceID = i.InternalInstanceID);

    /*  Internal overdue, using the SAME dictionary - verified, see header.
        The INNER JOIN is deliberate: a status the dictionary does not know
        yields no row and is counted separately as an unmapped gap.        */
    /*  [v3 2026-10-27] LATEST transaction per internal due date (newest Dated, then
        highest ID - seeks IX_ICT_ICSO_Dated_ID). v2 joined every transaction, so a due
        date that was Open once and later closed still counted as overdue. A due date
        with no transaction at all is not overdue (RegTrack parity, same as statutory). */
    IF OBJECT_ID('tempdb..#intOvd') IS NOT NULL DROP TABLE #intOvd;
    SELECT o.ScheduledOnID
    INTO #intOvd
    FROM #intOcc o
    CROSS APPLY (SELECT TOP 1 it.StatusId
                 FROM InternalComplianceTransaction it
                 WHERE it.InternalComplianceScheduledOnID = o.ScheduledOnID
                 ORDER BY it.Dated DESC, it.ID DESC) lt
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = lt.StatusId
    WHERE o.ScheduledOn < CAST(@AsOf AS DATE)   -- [v2] due before today
      AND d.OverdueEligible = 1;                -- [v2] Open only (dictionary v2)
    CREATE CLUSTERED INDEX IX_intOvd ON #intOvd (ScheduledOnID);

    DECLARE @intUnmapped INT = (
        SELECT COUNT(*)
        FROM #int i
        JOIN InternalComplianceScheduledOn iso ON iso.InternalComplianceInstanceID = i.InternalInstanceID
        JOIN InternalComplianceTransaction it  ON it.InternalComplianceScheduledOnID = iso.ID
        WHERE NOT EXISTS (SELECT 1 FROM dbo.vInsightsStatusCurrent d WHERE d.StatusId = it.StatusId));

    IF OBJECT_ID('tempdb..#intOwn') IS NOT NULL DROP TABLE #intOwn;
    SELECT DISTINCT ia.InternalComplianceInstanceID AS InternalInstanceID
    INTO #intOwn
    FROM InternalComplianceAssignment ia
    JOIN #int i ON i.InternalInstanceID = ia.InternalComplianceInstanceID
    WHERE ia.RoleID = 3 AND ia.UserID > 0;

    /*-- 2. ROWS - one per branch, both populations side by side ---------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        BranchID            INT            NOT NULL PRIMARY KEY,
        BranchName          NVARCHAR(600)  NULL,
        ApexName            NVARCHAR(600)  NULL,
        StatutoryInstances  INT            NOT NULL,
        StatutoryOverdue    INT            NOT NULL,
        StatutoryNoInstanceOwner  INT            NOT NULL,
        InternalInstances   INT            NOT NULL,
        InternalOverdue     INT            NOT NULL,
        InternalNoInstanceOwner   INT            NOT NULL,
        -- derived
        StatutoryNoInstanceOwnerPct DECIMAL(5,1) NULL,
        InternalNoInstanceOwnerPct  DECIMAL(5,1) NULL,
        Flags               VARCHAR(200)   NULL
    );

    INSERT #rows (BranchID, BranchName, ApexName, StatutoryInstances, StatutoryOverdue,
                  StatutoryNoInstanceOwner, InternalInstances, InternalOverdue, InternalNoInstanceOwner)
    SELECT
        t.BranchID, t.BranchName, t.ApexName,
        ISNULL(s.Inst,0), ISNULL(s.Ovd,0), ISNULL(so.Own,0),
        ISNULL(n.Inst,0), ISNULL(n.Ovd,0), ISNULL(no.Own,0)
    FROM dbo.tvfInsightsEntityTree(@CustomerID) t
    /*  [v3 2026-10-27] Inst/Ovd count DUE DATES (occurrence grain); Own stays
        instance-level, so it comes from its own per-branch aggregate. */
    LEFT JOIN (
        SELECT x.BranchID,
               COUNT(*) AS Inst,
               SUM(CASE WHEN o.ScheduleOnID IS NOT NULL THEN 1 ELSE 0 END) AS Ovd
        FROM #statOcc x
        LEFT JOIN #statOvd o ON o.ScheduleOnID = x.ScheduleOnID
        GROUP BY x.BranchID) s ON s.BranchID = t.BranchID
    LEFT JOIN (
        SELECT st.BranchID,
               SUM(CASE WHEN w.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END) AS Own
        FROM #stat st
        LEFT JOIN #statOwn w ON w.ComplianceInstanceID = st.ComplianceInstanceID
        GROUP BY st.BranchID) so ON so.BranchID = t.BranchID
    LEFT JOIN (
        SELECT x.BranchID,
               COUNT(*) AS Inst,
               SUM(CASE WHEN o.ScheduledOnID IS NOT NULL THEN 1 ELSE 0 END) AS Ovd
        FROM #intOcc x
        LEFT JOIN #intOvd o ON o.ScheduledOnID = x.ScheduledOnID
        GROUP BY x.BranchID) n ON n.BranchID = t.BranchID
    LEFT JOIN (
        SELECT it.BranchID,
               SUM(CASE WHEN w.InternalInstanceID IS NULL THEN 1 ELSE 0 END) AS Own
        FROM #int it
        LEFT JOIN #intOwn w ON w.InternalInstanceID = it.InternalInstanceID
        GROUP BY it.BranchID) no ON no.BranchID = t.BranchID;

    /*-- 3. RECONCILIATION - both populations, independently -------------*/
    /*  [v3 2026-10-27] @statTotal/@intTotal stay DISTINCT obligation counts
        (ScopedInstances/InternalInstances, ownership denominators); the rows now sum
        to the OCCURRENCE totals, so reconciliation ties them to #statOcc/#intOcc. */
    DECLARE @statTotal    INT = (SELECT COUNT(*) FROM #stat);
    DECLARE @statOccTotal INT = (SELECT COUNT(*) FROM #statOcc);
    DECLARE @statRowSum   INT = (SELECT ISNULL(SUM(StatutoryInstances),0) FROM #rows);
    DECLARE @intTotal     INT = (SELECT COUNT(*) FROM #int);
    DECLARE @intOccTotal  INT = (SELECT COUNT(*) FROM #intOcc);
    DECLARE @intRowSum    INT = (SELECT ISNULL(SUM(InternalInstances),0) FROM #rows);

    IF @statRowSum <> @statOccTotal
        THROW 51111, N'INTERNAL DIMENSION RECONCILIATION FAILED - per-branch statutory sums do not tie to the scoped statutory total. Refusing to publish.', 1;
    IF @intRowSum <> @intOccTotal
        THROW 51111, N'INTERNAL DIMENSION RECONCILIATION FAILED - per-branch internal sums do not tie to the scoped internal total. Refusing to publish.', 1;

    DECLARE @hasAnyObligations BIT = CASE WHEN @statTotal > 0 OR @intTotal > 0 THEN 1 ELSE 0 END;

    UPDATE #rows SET
        StatutoryNoInstanceOwnerPct = CASE WHEN StatutoryInstances = 0 THEN NULL
                                     ELSE 100.0 * StatutoryNoInstanceOwner / StatutoryInstances END,
        InternalNoInstanceOwnerPct  = CASE WHEN InternalInstances  = 0 THEN NULL
                                     ELSE 100.0 * InternalNoInstanceOwner  / InternalInstances  END;

    /*-- 4. DETECTIONS ---------------------------------------------------*/
    -- [FOUND LIVE 2026-10-06, same class as Act's LargestRegulatorInstances fix] Real counts behind
    -- these two percentages, exposed below so the render agent cites them directly instead of
    -- deriving them from the percentage and the relevant total - a derived number is not a value
    -- from any real field and gets the whole report refused by the untraceable-number gate
    -- (CLAUDE.md non-negotiable #5).
    DECLARE @statOwnInstances INT = (SELECT ISNULL(SUM(StatutoryNoInstanceOwner),0) FROM #rows);
    DECLARE @intOwnInstances  INT = (SELECT ISNULL(SUM(InternalNoInstanceOwner),0) FROM #rows);
    DECLARE @statOwnPct DECIMAL(5,1) =
        CASE WHEN @statTotal = 0 THEN NULL ELSE 100.0 * @statOwnInstances / @statTotal END;
    DECLARE @intOwnPct DECIMAL(5,1) =
        CASE WHEN @intTotal = 0 THEN NULL ELSE 100.0 * @intOwnInstances / @intTotal END;

    UPDATE #rows SET Flags =
        STUFF(
            /*  The structural finding: a branch running statutory compliance with
                no internal governance configured at all. */
            CASE WHEN @hasAnyObligations = 1 AND StatutoryInstances > 0 AND InternalInstances = 0
                 THEN ',internal_coverage_gap' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND InternalInstances > 0
                  AND @statOwnPct IS NOT NULL AND InternalNoInstanceOwnerPct > @statOwnPct
                 THEN ',internal_ownerless_rate' ELSE '' END
        , 1, 1, '');

    DECLARE @internalAbsent BIT = CASE WHEN @statTotal > 0 AND @intTotal = 0 THEN 1 ELSE 0 END;

    SELECT
        'control_totals'                AS ResultSet,
        @statTotal                      AS ScopedInstances,
        @statRowSum                     AS SumOfRows,
        CAST(1 AS BIT)                  AS Reconciled,
        @intTotal                       AS InternalInstances,
        @intRowSum                      AS SumOfInternalRows,
        (SELECT COUNT(*) FROM #statOvd) AS StatutoryOverdueInstances,
        (SELECT COUNT(*) FROM #intOvd)  AS InternalOverdueInstances,
        @statOwnInstances               AS StatutoryNoInstanceOwnerInstances,
        @statOwnPct                     AS StatutoryNoInstanceOwnerPct,
        @intOwnInstances                AS InternalNoInstanceOwnerInstances,
        @intOwnPct                      AS InternalNoInstanceOwnerPct,
        (SELECT COUNT(*) FROM #rows WHERE StatutoryInstances > 0) AS BranchesWithStatutory,
        (SELECT COUNT(*) FROM #rows WHERE InternalInstances  > 0) AS BranchesWithInternal,
        @internalAbsent                 AS InternalAbsentEntirely,
        @intUnmapped                    AS InternalUnmappedStatusRows;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY StatutoryInstances DESC;

    /*-- 5. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @withStat INT = (SELECT COUNT(*) FROM #rows WHERE StatutoryInstances > 0);
    DECLARE @withInt  INT = (SELECT COUNT(*) FROM #rows WHERE InternalInstances  > 0);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'internal_coverage_gap', @withStat,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%internal_coverage_gap%')
    UNION ALL SELECT 'internal_ownerless_rate', @withInt,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%internal_ownerless_rate%');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    /*  [ADDED 2026-09-10] DETECTOR CONTRACT - fail at source.
        Flagged and Eligible MUST come from the same population. sql/05 once
        emitted 120 flagged of 99 eligible (121.2%) because a flag had no
        Instances > 0 guard; only the .NET layer caught it, three layers
        downstream. A percentage above 100 reaching a narrative writer is
        indefensible - the writer cannot tell it is impossible, and rendering
        it faithfully produces a false statement.
        Shared code 51040 across all dimensions: same failure class, and the
        message names the offending detector.                                 */
    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51040, N'DETECTOR CONTRACT VIOLATED - a detector flagged more rows than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 6. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    IF @statOwnPct IS NOT NULL
    INSERT #assert VALUES ('A-STAT-OWN','no_instance_owner_pct',N'statutory',@statOwnPct,NULL,@statTotal,NULL,NULL,NULL,NULL);

    IF @intOwnPct IS NOT NULL
    INSERT #assert VALUES ('A-INT-OWN','no_instance_owner_pct',N'internal',@intOwnPct,NULL,@intTotal,
                           @statOwnPct, @intOwnPct - @statOwnPct,
                           CASE WHEN @intOwnPct > @statOwnPct THEN 'worse' ELSE 'better' END, NULL);

    /*  Coverage: the structural contrast. Branch counts, not instance counts -
        the finding is WHERE internal governance runs, not how much of it. */
    INSERT #assert
    VALUES ('A-COVER','branches_with_internal',N'tenant',@withInt,NULL,@withStat,NULL,NULL,NULL,
            N'branch counts, not instance counts - the finding is WHERE internal governance runs');

    IF @internalAbsent = 1
    INSERT #assert
    VALUES ('A-ABSENT','internal_instances',N'tenant',0,NULL,@statTotal,NULL,NULL,NULL,
            N'internal_absent_entirely: statutory compliance is running with no internal governance configured anywhere in this scope');

    IF (SELECT EmitMode FROM #detector WHERE Detector='internal_coverage_gap') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-GAP-' + CAST(ROW_NUMBER() OVER (ORDER BY StatutoryInstances DESC) AS VARCHAR(5)),
               'statutory_instances_without_internal', BranchName, StatutoryInstances, NULL, NULL,
               NULL, NULL, NULL,
               N'internal_coverage_gap: statutory due dates tracked here, no internal governance configured'
        FROM #rows WHERE Flags LIKE '%internal_coverage_gap%' ORDER BY StatutoryInstances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='internal_coverage_gap') = 'aggregate'
        INSERT #assert
        SELECT 'A-GAP-AGG','branches_without_internal',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL,
               N'aggregate - internal governance is configured on a minority of the estate'
        FROM #detector WHERE Detector='internal_coverage_gap';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 7. FINDINGS -----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-ABSENT','high',
           N'No internal compliance is configured anywhere in this scope',
           'A-ABSENT',
           N'State it as a governance-maturity observation, not a breach. Internal compliance is self-imposed - its absence is a choice the tenant may have made deliberately. Confirm before treating it as a gap.'
    FROM #assert WHERE AssertionId = 'A-ABSENT';

    INSERT #find
    SELECT 'F-COVER','high',
           CONCAT(N'Internal governance runs at ', CAST(Value AS INT), N' of ', OfN,
                  N' locations that carry statutory obligations'),
           'A-COVER',
           N'The finding is structural, not quantitative: WHICH parts of the estate have internal governance and which do not. Do not reduce it to a volume comparison.'
    FROM #assert WHERE AssertionId = 'A-COVER' AND OfN > 0 AND Value < OfN;

    INSERT #find
    SELECT 'F-INT-OWN','high',
           CONCAT(N'Internal obligations are unassigned at ', Value, N'%, against ',
                  ComparatorValue, N'% for statutory'),
           'A-INT-OWN,A-STAT-OWN',
           N'Do not present internal and statutory as one population - they are separately configured and separately owned.'
    FROM #assert WHERE AssertionId = 'A-INT-OWN' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-GAP','medium',
           CONCAT(N'', ScopeLabel, N' carries ', CAST(Value AS INT),
                  N' statutory due date(s) in this period with no internal governance configured'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-GAP-[0-9]%';

    INSERT #find
    SELECT 'F-GAP-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' locations (', VsComparatorPP,
                  N'%) run statutory compliance with no internal governance'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-GAP-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 8. DATA QUALITY -------------------------------------------------*/
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'window'                               THEN 'ScopedInstances'
                   WHEN 'ownership_has_two_mechanisms'   THEN 'StatutoryNoInstanceOwnerPct'
                   WHEN 'flow_metric_drift'                    THEN 'OverduePct'
                   WHEN 'internal_scope_is_branch_only'        THEN 'InternalInstances'
                   WHEN 'internal_unmapped_status'             THEN 'InternalUnmappedStatusRows'
                   WHEN 'internal_absent'                      THEN 'InternalAbsentEntirely'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Counts every active due date between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), @WindowEnd, 23),
                      N' (one row per due date, as in RegTrack''s Detailed Report). Applies to BOTH populations - '
                    + N'statutory due dates come from ComplianceScheduleOn, internal ones from '
                    + N'InternalComplianceScheduledOn. ScopedInstances and InternalInstances are the distinct '
                    + N'obligations behind those due dates; an obligation with no due date in this window is '
                    + N'excluded entirely.') AS Detail
        UNION ALL
        SELECT 'ownership_has_two_mechanisms' AS Issue,
               N'RegTrack assigns a performer by TWO mechanisms: ComplianceAssignment (on the '
             + N'obligation) and ComplianceScheduleOn.Performerid (on each occurrence, 99.8% '
             + N'populated). This metric counts only the FIRST. Most obligations it counts DO '
             + N'have someone named per occurrence - what is missing is accountability for the '
             + N'obligation itself. NEVER present it as "nobody is doing this". NoOwnerAnywhere '
             + N'is the stricter measure.' AS Detail
        UNION ALL
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'internal_scope_is_branch_only',
               N'InternalComplianceInstance carries no compliance category, so the internal population is '
             + N'constrained on the BRANCH axis only while the statutory population is constrained on both '
             + N'branch AND category. The internal figures are therefore a wider cut than the statutory '
             + N'ones and the two are not strictly like-for-like.'
        UNION ALL
        SELECT 'internal_unmapped_status',
               CONCAT(N'', @intUnmapped, N' internal transaction(s) carry a status absent from the '
                    + N'classification dictionary and are excluded from the internal overdue figure. '
                    + N'Declared, not silently dropped.')
        WHERE @intUnmapped > 0
        UNION ALL
        SELECT 'internal_absent',
               N'No internal compliance instances exist in this scope, so every internal figure is zero '
             + N'by absence rather than by measurement. Nothing about internal governance quality can be '
             + N'inferred from this report.'
        WHERE @intTotal = 0
    ) q;

    DROP TABLE #branch; DROP TABLE #stat; DROP TABLE #statOcc; DROP TABLE #lt; DROP TABLE #statOvd; DROP TABLE #statOwn;
    DROP TABLE #int; DROP TABLE #intOcc; DROP TABLE #intOvd; DROP TABLE #intOwn;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Internal (v3, occurrence-level 2026-10-27) installed.';
GO