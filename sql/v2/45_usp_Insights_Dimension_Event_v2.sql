/*===========================================================================
  RegTrack Insights - v2 (2026-10-27): Event = RegTrack's "Event Based" tabs
  Object : dbo.usp_Insights_Dimension_Event
  Base   : rewrite of sql/14 (the definition production runs, last changed
           2026-10-05). Same name, same parameters, same error block.

  Why    : product decision 2026-10-27 - every Insights dimension counts what
  RegTrack's own Detailed Report (Kendo_DetailedReport_Pagination) counts. v1
  counted EVENT SET-UPS (EventInstance) whose StartDate fell inside the
  period - almost never any, so the report was usually empty, and no RegTrack
  report shows that figure. Found live on the production replica, q2 FY26-27:
  tenant 1300/user 22426 - v1 0 events, RegTrack's Event Based tabs 34 due
  dates, 28 overdue; tenant 1926/user 56762 - v1 0, tabs 3 due dates, 3 overdue.

  What it counts now - the Detailed Report's "Event Based" and "Event Based
  Checklist" tabs (Compliance.EventFlag = 1, ComplianceType 0 and 1), with the
  SAME rules every other dimension uses (sql/v2/36 tvfInsightsScopedInstances,
  except EventFlag = 1 instead of IS NULL):
    - the user's (branch, category) scope pairs; branch live and active
    - compliance and Act not deleted, compliance visible
    - an active performer (ComplianceAssignment RoleID 3, active user)
    - one row per ACTIVE due date in the period (IsActive, IsUpcomingNotDeleted)
    - overdue = latest status Open (dictionary OverdueEligible) and due before
      today; a due date with no transaction is not overdue
  Rows are event TYPES (ComplianceScheduleOn.EventScheduledOnID ->
  EventScheduleOn -> EventInstance -> Event). Due dates with no recorded event
  link go to one row, EventID 0 "Event not recorded", so rows still sum to the
  total. Side fact kept from v1: event types set up at the user's branches with
  no event activity in the last @DormancyMonths (may be tracked off-system).

  Performance: same pattern as sql/v2/37-44 - scope materialised and indexed
  first, latest transaction fetched once per INSTANCE (#lt), never
  tvfInsightsOverdueSchedules.

  Error block: unchanged (51120-51129). 51120 scope denied, 51121
  reconciliation, 51122 bad window.
===========================================================================*/
SET NOCOUNT ON;
GO
CREATE OR ALTER PROCEDURE dbo.usp_Insights_Dimension_Event
    @UserID          INT,
    @CustomerID      INT,
    @WindowStart     DATETIME,
    @WindowEnd       DATETIME,
    @AsOf            DATETIME = NULL,
    @DormancyMonths  INT      = 12      -- side fact only: event types with no event activity this long
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT --------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51120, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    IF @WindowStart IS NULL OR @WindowEnd IS NULL
        THROW 51122, N'EVENT DIMENSION - @WindowStart and @WindowEnd are required (resolve the period-picker choice to a concrete date range before calling).', 1;
    IF @WindowEnd <= @WindowStart
        THROW 51122, N'EVENT DIMENSION - @WindowEnd must be strictly after @WindowStart.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. SCOPED EVENT-BASED COMPLIANCE (sql/v2/36 rules, EventFlag = 1) -*/
    IF OBJECT_ID('tempdb..#pairs') IS NOT NULL DROP TABLE #pairs;
    SELECT DISTINCT sp.BranchID, sp.CategoryId
    INTO #pairs
    FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp;
    CREATE CLUSTERED INDEX IX_pairs ON #pairs (BranchID, CategoryId);

    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT i.ID AS ComplianceInstanceID, i.CustomerBranchID AS BranchID,
           c.ComplianceType, CAST(ISNULL(c.Imprisonment, 0) AS INT) AS Imprisonment
    INTO #inst
    FROM ComplianceInstance i
    JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID
    JOIN Compliance     c  ON c.ID  = i.ComplianceID
    JOIN Act            a  ON a.ID  = c.ActID
    JOIN #pairs         sp ON sp.BranchID = i.CustomerBranchID AND sp.CategoryId = a.ComplianceCategoryId
    WHERE cb.CustomerID = @CustomerID
      AND cb.IsDeleted = 0 AND cb.Status = 1
      AND i.IsDeleted  = 0
      AND c.IsDeleted  = 0
      AND a.IsDeleted  = 0
      AND (c.ComplinceVisible = 1 OR c.ComplinceVisible IS NULL)   -- [TRAP] column misspelled in the schema
      AND c.EventFlag = 1                                          -- RegTrack 'Event Based' / 'Event Based Checklist'
      AND EXISTS (SELECT 1 FROM ComplianceAssignment ca
                  JOIN [User] u ON u.ID = ca.UserID
                  WHERE ca.ComplianceInstanceID = i.ID AND ca.RoleID = 3
                    AND u.CustomerID = @CustomerID AND u.IsDeleted = 0 AND u.IsActive = 1);
    CREATE CLUSTERED INDEX IX_inst ON #inst (ComplianceInstanceID);

    /*-- 2. DUE DATES IN THE PERIOD (occurrence grain) -------------------*/
    IF OBJECT_ID('tempdb..#occ') IS NOT NULL DROP TABLE #occ;
    SELECT cso.ID AS ScheduleOnID, i.ComplianceInstanceID, i.BranchID, cso.ScheduleOn,
           i.ComplianceType, i.Imprisonment,
           ISNULL(CAST(ei.EventID AS BIGINT), 0) AS EventID
    INTO #occ
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN EventScheduleOn eso ON eso.ID = cso.EventScheduledOnID
    LEFT JOIN EventInstance   ei  ON ei.ID  = eso.EventInstanceID
    WHERE cso.ScheduleOn >= @WindowStart AND cso.ScheduleOn < @WindowEnd
      AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1;
    CREATE CLUSTERED INDEX IX_occ ON #occ (ScheduleOnID);
    CREATE NONCLUSTERED INDEX IX_occ_event ON #occ (EventID);

    DELETE i FROM #inst i
    WHERE NOT EXISTS (SELECT 1 FROM #occ o WHERE o.ComplianceInstanceID = i.ComplianceInstanceID);

    /*-- 3. OVERDUE - latest transaction per due date ---------------------*/
    IF OBJECT_ID('tempdb..#lt') IS NOT NULL DROP TABLE #lt;
    SELECT x.ComplianceScheduleOnID, x.StatusId
    INTO #lt
    FROM (
        SELECT t.ComplianceScheduleOnID, t.StatusId,
               ROW_NUMBER() OVER (PARTITION BY t.ComplianceScheduleOnID
                                  ORDER BY t.Dated DESC, t.ID DESC) AS rn
        FROM #inst i
        JOIN dbo.ComplianceTransaction t ON t.ComplianceInstanceId = i.ComplianceInstanceID
    ) x
    WHERE x.rn = 1;
    CREATE CLUSTERED INDEX IX_lt ON #lt (ComplianceScheduleOnID);

    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT o.ScheduleOnID
    INTO #ovd
    FROM #occ o
    JOIN #lt lt                       ON lt.ComplianceScheduleOnID = o.ScheduleOnID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = lt.StatusId
    WHERE d.OverdueEligible = 1
      AND o.ScheduleOn < CAST(@AsOf AS DATE);
    CREATE CLUSTERED INDEX IX_ovd ON #ovd (ScheduleOnID);

    /*-- 4. ROWS - one per event type (EventID 0 = no recorded event) -----*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        EventID               BIGINT         NOT NULL PRIMARY KEY,
        EventName             NVARCHAR(1000) NULL,
        Instances             INT            NOT NULL,
        Overdue               INT            NOT NULL,
        OverduePct            DECIMAL(5,1)   NULL,
        ChecklistInstances    INT            NOT NULL,
        ImprisonmentInstances INT            NOT NULL,
        ImprisonmentOverdue   INT            NOT NULL,
        BranchesCovered       INT            NOT NULL,
        VsTenantPP            DECIMAL(9,2)   NULL,
        Flags                 VARCHAR(200)   NULL
    );
    INSERT #rows (EventID, EventName, Instances, Overdue, ChecklistInstances,
                  ImprisonmentInstances, ImprisonmentOverdue, BranchesCovered)
    SELECT o.EventID,
           CASE WHEN o.EventID = 0 THEN N'Event not recorded' ELSE LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(MAX(ev.Name), CHAR(9), ' '), CHAR(13), ' '), CHAR(10), ' '))) END,
           COUNT(*),
           SUM(CASE WHEN v.ScheduleOnID IS NOT NULL THEN 1 ELSE 0 END),
           SUM(CASE WHEN o.ComplianceType = 1 THEN 1 ELSE 0 END),
           SUM(o.Imprisonment),
           SUM(CASE WHEN v.ScheduleOnID IS NOT NULL THEN o.Imprisonment ELSE 0 END),
           COUNT(DISTINCT o.BranchID)
    FROM #occ o
    LEFT JOIN #ovd v ON v.ScheduleOnID = o.ScheduleOnID
    LEFT JOIN Event ev ON ev.ID = o.EventID
    GROUP BY o.EventID;

    /*-- 5. RECONCILIATION ----------------------------------------------*/
    DECLARE @scopedDistinct INT = (SELECT COUNT(*) FROM #inst);
    DECLARE @occTotal       INT = (SELECT COUNT(*) FROM #occ);
    DECLARE @rowSum         INT = (SELECT ISNULL(SUM(Instances), 0) FROM #rows);
    DECLARE @ovdTotal       INT = (SELECT COUNT(*) FROM #ovd);
    DECLARE @ovdRowSum      INT = (SELECT ISNULL(SUM(Overdue), 0) FROM #rows);
    IF @rowSum <> @occTotal OR @ovdRowSum <> @ovdTotal
        THROW 51121, N'EVENT DIMENSION RECONCILIATION FAILED - per-event-type sums do not tie to the event-based due-date totals. Refusing to publish.', 1;

    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @occTotal = 0 THEN 0 ELSE 100.0 * @ovdTotal / @occTotal END;
    UPDATE #rows SET
        OverduePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END;
    UPDATE #rows SET
        VsTenantPP = CASE WHEN Instances = 0 THEN NULL ELSE OverduePct - @tenantOverduePct END;

    /*-- 6. SIDE FACT - event types set up but not triggered recently ----*/
    /*  Event set-ups at the user's branches (branch axis - an event belongs to a
        branch, not a category), and whether any of their event schedules moved
        (held, closed or started) in the last @DormancyMonths. Database fact only:
        events may be tracked off-system. */
    IF OBJECT_ID('tempdb..#etconf') IS NOT NULL DROP TABLE #etconf;
    CREATE TABLE #etconf (EventID BIGINT NOT NULL PRIMARY KEY, Recent INT NOT NULL);
    INSERT #etconf (EventID, Recent)
    SELECT ei.EventID,
           MAX(CASE WHEN es.ID IS NOT NULL THEN 1 ELSE 0 END)
    FROM EventInstance ei
    JOIN CustomerBranch cb ON cb.ID = ei.CustomerBranchID
    JOIN (SELECT DISTINCT BranchID FROM #pairs) b ON b.BranchID = ei.CustomerBranchID
    LEFT JOIN EventScheduleOn es
           ON es.EventInstanceID = ei.ID AND es.IsDeleted = 0
          AND (   es.HeldOn     >= DATEADD(MONTH, -@DormancyMonths, @AsOf)
               OR es.ClosedDate >= DATEADD(MONTH, -@DormancyMonths, @AsOf)
               OR es.StartDate  >= DATEADD(MONTH, -@DormancyMonths, @AsOf))
    WHERE ei.IsDeleted = 0 AND cb.IsDeleted = 0 AND cb.Status = 1 AND cb.CustomerID = @CustomerID
    GROUP BY ei.EventID;

    DECLARE @typesConfigured   INT = (SELECT COUNT(*) FROM #etconf);
    DECLARE @typesNotRecent    INT = (SELECT COUNT(*) FROM #etconf WHERE Recent = 0);
    DECLARE @branchesInScope   INT = (SELECT COUNT(DISTINCT BranchID) FROM #pairs);
    DECLARE @branchesWithWork  INT = (SELECT COUNT(DISTINCT BranchID) FROM #occ);
    DECLARE @unlinked          INT = (SELECT ISNULL(SUM(r.Instances), 0) FROM #rows r WHERE r.EventID = 0);

    /*-- 7. DETECTIONS ---------------------------------------------------*/
    /*  Same materiality rule as Nature (sql/v2/39): rank only event types with
        enough due dates; fall back to every type when fewer than 2 qualify.
        The "not recorded" bucket is never a member of a comparison. */
    DECLARE @materialityFloor INT = 20;
    DECLARE @materialMembers  INT = (SELECT COUNT(*) FROM #rows WHERE EventID <> 0 AND Instances >= @materialityFloor);
    DECLARE @rankFloor        INT = CASE WHEN @materialMembers < 2 THEN 1 ELSE @materialityFloor END;
    DECLARE @eligibleTypes    INT = (SELECT COUNT(*) FROM #rows WHERE EventID <> 0 AND Instances >= @rankFloor);

    UPDATE #rows SET Flags =
        CASE WHEN EventID <> 0 AND Instances >= @rankFloor AND @eligibleTypes >= 2
              AND OverduePct > @tenantOverduePct
             THEN 'worse_than_tenant' ELSE NULL END;
    UPDATE #rows SET Flags = 'event_not_recorded' WHERE EventID = 0;

    SELECT
        'control_totals'                 AS ResultSet,
        @scopedDistinct                  AS ScopedInstances,
        @rowSum                          AS SumOfRows,
        CAST(1 AS BIT)                   AS Reconciled,
        @ovdTotal                        AS OverdueInstances,
        @tenantOverduePct                AS TenantOverduePct,
        (SELECT ISNULL(SUM(ChecklistInstances), 0) FROM #rows)    AS ChecklistInstances,
        (SELECT ISNULL(SUM(ImprisonmentInstances), 0) FROM #rows) AS ImprisonmentInstances,
        (SELECT ISNULL(SUM(ImprisonmentOverdue), 0) FROM #rows)   AS ImprisonmentOverdue,
        (SELECT COUNT(*) FROM #rows WHERE EventID <> 0)           AS EventTypesReported,
        @unlinked                        AS UnlinkedInstances,
        @branchesInScope                 AS BranchesInScope,
        @branchesWithWork                AS BranchesWithEventWork,
        @branchesInScope - @branchesWithWork AS BranchesWithoutEventWork,
        @typesConfigured                 AS EventTypesConfigured,
        @typesNotRecent                  AS EventTypesNotTriggeredRecently,
        @DormancyMonths                  AS ActivityWindowMonths;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*-- 8. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'worse_than_tenant',
           CASE WHEN @eligibleTypes >= 2 THEN @eligibleTypes ELSE 0 END,
           (SELECT COUNT(*) FROM #rows r WHERE r.Flags = 'worse_than_tenant');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    /*  [ADDED 2026-09-10] DETECTOR CONTRACT - fail at source (shared code 51040). */
    IF EXISTS (SELECT 1 FROM #detector WHERE Flagged > Eligible)
        THROW 51040, N'DETECTOR CONTRACT VIOLATED - a detector flagged more rows than it declared eligible. Flagged and Eligible must come from the same population. Refusing to emit.', 1;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 9. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(1000),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    DECLARE @offSystem NVARCHAR(200) =
        N'may_be_tracked_off_system: absence of activity in this database is not evidence of absence in the business (open item O-8)';

    IF @occTotal > 0
    INSERT #assert VALUES ('A-TENANT', 'overdue_pct', N'tenant', @tenantOverduePct, NULL, @occTotal, NULL, NULL, NULL, NULL);

    IF @occTotal > 0 AND (SELECT ISNULL(SUM(ImprisonmentOverdue), 0) FROM #rows) > 0
    INSERT #assert
    SELECT 'A-IMP-OVD', 'imprisonment_overdue', N'tenant', SUM(r.ImprisonmentOverdue), NULL, @ovdTotal,
           NULL, NULL, NULL, NULL
    FROM #rows r;

    IF (SELECT EmitMode FROM #detector WHERE Detector = 'worse_than_tenant') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-WORST-' + CAST(ROW_NUMBER() OVER (ORDER BY r.OverduePct DESC, r.Instances DESC) AS VARCHAR(5)),
               'overdue_pct', r.EventName, r.OverduePct,
               CAST(ROW_NUMBER() OVER (ORDER BY r.OverduePct DESC, r.Instances DESC) AS INT), @eligibleTypes,
               @tenantOverduePct, r.VsTenantPP, 'worse', NULL
        FROM #rows r WHERE r.Flags = 'worse_than_tenant'
        ORDER BY r.OverduePct DESC, r.Instances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector = 'worse_than_tenant') = 'aggregate'
        INSERT #assert
        SELECT 'A-WORST-AGG', 'event_types_worse_than_tenant', N'tenant',
               d.Flagged, NULL, d.Eligible, NULL, d.FlaggedPct, 'worse', NULL
        FROM #detector d WHERE d.Detector = 'worse_than_tenant';

    IF @unlinked > 0
    INSERT #assert VALUES ('A-UNLINKED', 'due_dates_without_recorded_event', N'tenant', @unlinked, NULL, @occTotal, NULL, NULL, NULL, NULL);

    IF @typesConfigured > 0 AND @typesNotRecent > 0
    INSERT #assert VALUES ('A-NOTTRIG', 'event_types_not_triggered_recently', N'tenant', @typesNotRecent, NULL, @typesConfigured, NULL, NULL, NULL, @offSystem);

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS ----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-OVERDUE', CASE WHEN a.Value >= 50 THEN 'high' ELSE 'medium' END,
           CONCAT(N'', @ovdTotal, N' of ', @occTotal, N' event-based due dates in this period are overdue (', a.Value, N'%)'),
           'A-TENANT', NULL
    FROM #assert a WHERE a.AssertionId = 'A-TENANT' AND @ovdTotal > 0;

    INSERT #find
    SELECT 'F-WORST', 'medium',
           CONCAT(N'', a.ScopeLabel, N' is overdue on ', a.Value, N'% of its due dates, against ', a.ComparatorValue, N'% across all event-based work'),
           a.AssertionId, NULL
    FROM #assert a WHERE a.AssertionId LIKE 'A-WORST-[0-9]%';

    INSERT #find
    SELECT 'F-WORST-AGG', 'medium',
           CONCAT(N'', CAST(a.Value AS INT), N' of ', a.OfN, N' event types (', a.VsComparatorPP, N'%) run behind the overall event-based overdue rate'),
           a.AssertionId, NULL
    FROM #assert a WHERE a.AssertionId = 'A-WORST-AGG';

    INSERT #find
    SELECT 'F-NOTTRIG', 'info',
           CONCAT(N'', CAST(a.Value AS INT), N' of ', a.OfN, N' event types set up at your locations show no event activity in RegTrack in the last ', @DormancyMonths, N' months'),
           'A-NOTTRIG',
           N'Events may be tracked outside RegTrack. State the count plainly, then ask whether these events are handled elsewhere - never conclude they are unmanaged.'
    FROM #assert a WHERE a.AssertionId = 'A-NOTTRIG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY ------------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                WHEN 'window'                    THEN 'SumOfRows'
                WHEN 'flow_metric_drift'         THEN 'OverduePct'
                WHEN 'event_not_recorded'        THEN 'UnlinkedInstances'
                WHEN 'may_be_tracked_off_system' THEN 'EventTypesNotTriggeredRecently'
                ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'window' AS Issue,
               CONCAT(N'Counts every active event-based due date between ',
                      CONVERT(VARCHAR(10), @WindowStart, 23), N' and ', CONVERT(VARCHAR(10), DATEADD(DAY, -1, @WindowEnd), 23),
                      N' - the same rows as the Event Based and Event Based Checklist tabs of RegTrack''s Detailed Report, '
                    + N'one row per due date.') AS Detail
        UNION ALL
        SELECT 'flow_metric_drift',
               N'Overdue is a live figure and moves between runs.'
        UNION ALL
        SELECT 'event_not_recorded',
               CONCAT(N'', @unlinked, N' due date(s) have no event recorded against them in RegTrack, so they are shown under "Event not recorded" rather than an event type.')
        WHERE @unlinked > 0
        UNION ALL
        SELECT 'may_be_tracked_off_system',
               N'Event activity counts only what is recorded in RegTrack; events may be tracked elsewhere.'
        WHERE @typesNotRecent > 0
    ) q;

    DROP TABLE #pairs; DROP TABLE #inst; DROP TABLE #occ; DROP TABLE #lt; DROP TABLE #ovd;
    DROP TABLE #rows; DROP TABLE #etconf; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO
PRINT 'usp_Insights_Dimension_Event (v2, Event Based tabs 2026-10-27) installed.';
GO
