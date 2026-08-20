/*===========================================================================
  RegTrack Insights - Phase 1b, Step 9
  TASK / EVENT DIMENSION

  Spec reference : docs/DIMENSION_SPECS.md section 8
  Pattern        : sql/05_dimension_location.sql. Emits SIX result sets.
  Error block    : 51120-51129

  -- [TRAP] CONFIGURED IS NOT OPERATIONAL ------------------------------------
  On the reference tenant nearly every event instance carried a single bulk-
  configuration date - roughly 8 per event type, about one per branch - with
  NOTHING logged in over a year. That is template scaffolding, not live
  tracking. For a manufacturer with physical risk, a dormant event module means
  injury and death filing deadlines are unmanaged and invisible.

  -- [OPEN ITEM O-8] EMIT DORMANCY AS A QUESTION, NOT AN ACCUSATION ----------
  Events may legitimately be tracked off-system. The absence of activity in this
  database is evidence about this database, not about the tenant's operations.
  Every dormancy assertion below therefore carries a caveat, and every finding
  carries a narrative_guard requiring it to be put as a question. Telling a
  customer their injury-reporting is unmanaged when they track it on paper would
  destroy trust in every other number in the report.

  -- SCOPE: EVENTS HAVE NO CATEGORY AXIS ------------------------------------
  EventInstance carries a branch but no compliance category, so this population
  is constrained on the BRANCH axis only. Declared in data_quality.

  -- FRAME: COMPLIANCE-MODE COVERAGE ----------------------------------------
  The question this answers is not "how are events performing" but "which
  compliance MODES are actually live" - periodic, event-triggered, internal.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_Event', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_Event;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Event
    @UserID          INT,
    @CustomerID      INT,
    @AsOf            DATETIME = NULL,
    @DormancyMonths  INT      = 12      -- activity window; 12 months per the spec's observation
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT --------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51120, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    /*-- 1. SCOPED EVENT INSTANCES - branch axis only --------------------*/
    IF OBJECT_ID('tempdb..#branch') IS NOT NULL DROP TABLE #branch;
    SELECT DISTINCT sp.BranchID
    INTO #branch
    FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp;

    IF OBJECT_ID('tempdb..#ei') IS NOT NULL DROP TABLE #ei;
    SELECT ei.ID AS EventInstanceID, ei.EventID, ei.CustomerBranchID AS BranchID, ei.StartDate
    INTO #ei
    FROM EventInstance ei
    JOIN CustomerBranch cb ON cb.ID = ei.CustomerBranchID
    JOIN #branch b ON b.BranchID = ei.CustomerBranchID
    WHERE ei.IsDeleted = 0 AND cb.IsDeleted = 0 AND cb.CustomerID = @CustomerID;

    CREATE CLUSTERED INDEX IX_ei ON #ei (EventID, EventInstanceID);

    /*  Recent activity means a schedule actually MOVED - held, closed, or
        started - inside the window. A configuration date is not activity. */
    IF OBJECT_ID('tempdb..#recent') IS NOT NULL DROP TABLE #recent;
    SELECT DISTINCT e.EventInstanceID
    INTO #recent
    FROM #ei e
    JOIN EventScheduleOn es ON es.EventInstanceID = e.EventInstanceID
    WHERE es.IsDeleted = 0
      AND (   es.HeldOn     >= DATEADD(MONTH, -@DormancyMonths, @AsOf)
           OR es.ClosedDate >= DATEADD(MONTH, -@DormancyMonths, @AsOf)
           OR es.StartDate  >= DATEADD(MONTH, -@DormancyMonths, @AsOf));

    /*-- 2. MEMBER LIST = the event types actually configured ------------*/
    IF OBJECT_ID('tempdb..#etype') IS NOT NULL DROP TABLE #etype;
    SELECT DISTINCT ev.ID AS EventID, ev.Name AS EventName
    INTO #etype
    FROM Event ev
    WHERE EXISTS (SELECT 1 FROM #ei e WHERE e.EventID = ev.ID);

    /*-- 3. ROWS - one per event type -----------------------------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        EventID              BIGINT         NOT NULL PRIMARY KEY,
        EventName            NVARCHAR(500)  NULL,
        InstanceCount        INT            NOT NULL,
        BranchesCovered      INT            NOT NULL,
        EarliestStart        DATETIME       NULL,
        LatestStart          DATETIME       NULL,
        InstancesSinceCutoff INT            NOT NULL,
        -- derived
        DistinctStartDates   INT            NOT NULL,
        Flags                VARCHAR(200)   NULL
    );

    INSERT #rows (EventID, EventName, InstanceCount, BranchesCovered,
                  EarliestStart, LatestStart, InstancesSinceCutoff, DistinctStartDates)
    SELECT
        t.EventID, t.EventName,
        COUNT(e.EventInstanceID),
        COUNT(DISTINCT e.BranchID),
        MIN(e.StartDate),
        MAX(e.StartDate),
        SUM(CASE WHEN r.EventInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        COUNT(DISTINCT CAST(e.StartDate AS DATE))
    FROM #etype t
    LEFT JOIN #ei     e ON e.EventID = t.EventID
    LEFT JOIN #recent r ON r.EventInstanceID = e.EventInstanceID
    GROUP BY t.EventID, t.EventName;

    /*-- 4. RECONCILIATION ----------------------------------------------*/
    DECLARE @eventTotal INT = (SELECT COUNT(*) FROM #ei);
    DECLARE @rowSum     INT = (SELECT ISNULL(SUM(InstanceCount),0) FROM #rows);

    IF @rowSum <> @eventTotal
        THROW 51121, N'EVENT DIMENSION RECONCILIATION FAILED - per-event-type sums do not tie to the scoped event instance total. Refusing to publish.', 1;

    DECLARE @hasAnyEvents  BIT = CASE WHEN @eventTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @recentTotal   INT = (SELECT COUNT(*) FROM #recent);
    DECLARE @branchesInScope INT = (SELECT COUNT(*) FROM #branch);

    /*-- 5. DETECTIONS ---------------------------------------------------*/
    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyEvents = 1 AND InstanceCount > 0 AND InstancesSinceCutoff = 0
                 THEN ',event_types_never_triggered' ELSE '' END +
            /*  Bulk configuration: every instance of this type shares one or two
                start dates AND nothing has moved since. That is the scaffolding
                signature described in the spec, not a judgement about the tenant. */
            CASE WHEN @hasAnyEvents = 1 AND InstanceCount > 1
                  AND DistinctStartDates <= 2 AND InstancesSinceCutoff = 0
                 THEN ',bulk_configured_never_used' ELSE '' END
        , 1, 1, '');

    /*  Module dormancy is a TENANT-level observation, never a per-type flag. */
    DECLARE @moduleDormant BIT =
        CASE WHEN @hasAnyEvents = 1 AND @recentTotal = 0 THEN 1 ELSE 0 END;

    DECLARE @branchesWithEvents INT = (SELECT COUNT(DISTINCT BranchID) FROM #ei);
    DECLARE @branchesWithout    INT = @branchesInScope - @branchesWithEvents;

    SELECT
        'control_totals'                 AS ResultSet,
        @eventTotal                      AS ScopedInstances,
        @rowSum                          AS SumOfRows,
        CAST(1 AS BIT)                   AS Reconciled,
        (SELECT COUNT(*) FROM #rows)     AS EventTypesReported,
        @recentTotal                     AS InstancesActiveInWindow,
        @DormancyMonths                  AS ActivityWindowMonths,
        @branchesInScope                 AS BranchesInScope,
        @branchesWithEvents              AS BranchesWithEventCoverage,
        @branchesWithout                 AS BranchesWithoutEventCoverage,
        @moduleDormant                   AS EventModuleDormant;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY InstanceCount DESC;

    /*-- 6. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @typesWithInstances INT = (SELECT COUNT(*) FROM #rows WHERE InstanceCount > 0);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'event_types_never_triggered', @typesWithInstances,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%event_types_never_triggered%')
    UNION ALL SELECT 'bulk_configured_never_used', @typesWithInstances,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%bulk_configured_never_used%');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 7. ASSERTIONS - every one carries the off-system caveat ---------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    DECLARE @offSystem NVARCHAR(200) =
        N'may_be_tracked_off_system: absence of activity in this database is not evidence of absence in the business (open item O-8)';

    IF @hasAnyEvents = 1
    INSERT #assert
    VALUES ('A-ACTIVE','event_instances_active_in_window',N'tenant',@recentTotal,
            NULL,@eventTotal,NULL,NULL,NULL,@offSystem);

    IF @moduleDormant = 1
    INSERT #assert
    VALUES ('A-DORMANT','event_instances_active_in_window',N'tenant',0,
            NULL,@eventTotal,NULL,NULL,NULL,@offSystem);

    IF @branchesWithout > 0 AND @branchesInScope > 0
    INSERT #assert
    VALUES ('A-NOCOVER','branches_without_event_coverage',N'tenant',@branchesWithout,
            NULL,@branchesInScope,NULL,NULL,NULL,@offSystem);

    IF (SELECT EmitMode FROM #detector WHERE Detector='event_types_never_triggered') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-NEVER-' + CAST(ROW_NUMBER() OVER (ORDER BY InstanceCount DESC) AS VARCHAR(5)),
               'instances_active_in_window', EventName, 0, NULL, InstanceCount,
               NULL, NULL, NULL, @offSystem
        FROM #rows WHERE Flags LIKE '%event_types_never_triggered%' ORDER BY InstanceCount DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='event_types_never_triggered') = 'aggregate'
        INSERT #assert
        SELECT 'A-NEVER-AGG','event_types_never_triggered',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL, @offSystem
        FROM #detector WHERE Detector='event_types_never_triggered';

    IF (SELECT EmitMode FROM #detector WHERE Detector='bulk_configured_never_used') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-BULK-' + CAST(ROW_NUMBER() OVER (ORDER BY InstanceCount DESC) AS VARCHAR(5)),
               'distinct_start_dates', EventName, DistinctStartDates, NULL, InstanceCount,
               NULL, NULL, NULL, @offSystem
        FROM #rows WHERE Flags LIKE '%bulk_configured_never_used%' ORDER BY InstanceCount DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='bulk_configured_never_used') = 'aggregate'
        INSERT #assert
        SELECT 'A-BULK-AGG','event_types_bulk_configured',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, NULL, @offSystem
        FROM #detector WHERE Detector='bulk_configured_never_used';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 8. FINDINGS - all phrased as questions --------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    DECLARE @askGuard NVARCHAR(300) =
        N'MUST be put as a question, never an accusation. Events may be tracked off-system; this shows only what the platform holds. Ask whether these are managed elsewhere before treating it as a gap.';

    INSERT #find
    SELECT 'F-DORMANT','high',
           CONCAT(N'No event activity has been recorded in the platform in ', @DormancyMonths,
                  N' months, across ', OfN, N' configured event instance(s)'),
           'A-DORMANT', @askGuard
    FROM #assert WHERE AssertionId = 'A-DORMANT';

    INSERT #find
    SELECT 'F-NOCOVER','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN,
                  N' locations have no event-triggered obligations configured'),
           'A-NOCOVER', @askGuard
    FROM #assert WHERE AssertionId = 'A-NOCOVER';

    INSERT #find
    SELECT 'F-NEVER','medium',
           CONCAT(N'', ScopeLabel, N' has ', OfN, N' configured instance(s) and no recorded activity'),
           AssertionId, @askGuard
    FROM #assert WHERE AssertionId LIKE 'A-NEVER-[0-9]%';

    INSERT #find
    SELECT 'F-NEVER-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' event types (', VsComparatorPP,
                  N'%) have no recorded activity in the window'),
           AssertionId, @askGuard
    FROM #assert WHERE AssertionId = 'A-NEVER-AGG';

    INSERT #find
    SELECT 'F-BULK','info',
           CONCAT(N'', ScopeLabel, N' was configured on ', CAST(Value AS INT),
                  N' date(s) across ', OfN, N' instances, with no activity since'),
           AssertionId,
           N'This is the signature of bulk template configuration rather than live tracking. Put it as a question about how these are managed, not as a failure.'
    FROM #assert WHERE AssertionId LIKE 'A-BULK-[0-9]%';

    INSERT #find
    SELECT 'F-BULK-AGG','info',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' event types (', VsComparatorPP,
                  N'%) show the bulk-configuration signature'),
           AssertionId, @askGuard
    FROM #assert WHERE AssertionId = 'A-BULK-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 9. DATA QUALITY -------------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
        SELECT 'event_scope_is_branch_only' AS Issue,
               N'EventInstance carries no compliance category, so this population is constrained on the '
             + N'BRANCH axis only while every statutory cut is constrained on branch AND category. The '
             + N'two are not like-for-like.' AS Detail
        UNION ALL
        SELECT 'events_may_be_tracked_off_system',
               N'Open item O-8. Event-triggered obligations may legitimately be managed outside this '
             + N'platform. Every dormancy figure here describes the DATABASE, not the business. Verify '
             + N'with the tenant''s operations team before asserting dormancy.'
        WHERE @hasAnyEvents = 1
        UNION ALL
        SELECT 'no_events_configured',
               N'No event instances exist in this scope at all, so nothing can be said about '
             + N'event-triggered compliance either way. This is absence of data, not a finding.'
        WHERE @hasAnyEvents = 0
    ) q;

    DROP TABLE #branch; DROP TABLE #ei; DROP TABLE #recent; DROP TABLE #etype;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Task / Event dimension installed.';
GO
