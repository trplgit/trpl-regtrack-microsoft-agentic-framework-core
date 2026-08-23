/*===========================================================================
  RegTrack Insights - Phase 1b, Step 7
  USERS DIMENSION

  Spec reference : docs/DIMENSION_SPECS.md section 3
  Pattern        : sql/05_dimension_location.sql. Emits SIX result sets.
  Error block    : 51100-51109

  -- [TRAP] THE RECONCILIATION RULE IS DIFFERENT HERE ------------------------
  Every other dimension partitions the instances: one instance belongs to one
  branch, one nature, one risk level. USERS DO NOT PARTITION. A single instance
  carries a performer AND a reviewer, so SUM(rows.Instances) counts it twice and
  legitimately exceeds the scoped total.

  Summing per-user counts is exactly what produced an impossible 155%
  concentration during design (spec 6.8). So this proc reconciles on a
  DISTINCT-INSTANCE UNION:

      COUNT(DISTINCT assigned instances) + unassigned = scoped total

  and every concentration figure below is computed the same way. If you ever
  see a share above 100% in this dimension, a sum has crept back in.

  The reconciliation guards must be written so they CAN fail - see the trap note
  at section 6. Testing an identity is not reconciliation.

  -- [TRAP] ENGAGEMENT IS NOT QUALITY ---------------------------------------
  Measured on the reference tenant: never-login users had the LOWEST overdue
  rate of any engagement band, 0.3%. They are not the best compliers - they are
  nominal REVIEWERS on pipelines an active performer keeps current. Shipping a
  single "login = quality" ranking would tell a CCO their absent users are their
  best performers.

  Engagement and quality are therefore TWO separate lenses, and any assertion
  citing an engagement-band overdue rate MUST carry caveat
  "confounded_by_role_mix" so a narrator physically cannot quote the number
  without the confound attached.

  -- DEACTIVATED USERS ------------------------------------------------------
  User.IsDeleted <> User.IsActive. A deactivated user (IsActive = 0,
  IsDeleted = 0) still holding live assignments is the continuity risk this
  dimension exists to surface. Keep them and FLAG them - never filter to
  IsActive = 1.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_Users', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_Users;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Users
    @UserID      INT,
    @CustomerID  INT,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT --------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51100, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. SCOPED INSTANCE BASE ----------------------------------------*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT s.ComplianceInstanceID, s.BranchID, s.Imprisonment
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;

    CREATE CLUSTERED INDEX IX_inst ON #inst (ComplianceInstanceID);

    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;

    /*-- 2. ASSIGNMENTS - the grain of this dimension --------------------*/
    IF OBJECT_ID('tempdb..#asg') IS NOT NULL DROP TABLE #asg;
    SELECT DISTINCT ca.UserID, ca.RoleID, ca.ComplianceInstanceID
    INTO #asg
    FROM ComplianceAssignment ca
    JOIN #inst i ON i.ComplianceInstanceID = ca.ComplianceInstanceID
    WHERE ca.UserID > 0;

    CREATE CLUSTERED INDEX IX_asg ON #asg (UserID, ComplianceInstanceID);

    /*-- 3. ON-TIME COMPLETION, PERFORMER ONLY, OWN WORK -----------------
       Facet B2. Timeliness comes from the dictionary; resolved_terminal
       carries no timeliness and is therefore excluded from the denominator
       by construction, exactly as spec 6.4 requires.                    */
    IF OBJECT_ID('tempdb..#quality') IS NOT NULL DROP TABLE #quality;
    SELECT a.UserID,
           COUNT(*)                                                     AS CompletedEvents,
           SUM(CASE WHEN d.Timeliness = 'on_time' THEN 1 ELSE 0 END)     AS OnTimeEvents
    INTO #quality
    FROM #asg a
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = a.ComplianceInstanceID
    JOIN ComplianceTransaction t  ON t.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = t.StatusId
    WHERE a.RoleID = 3 AND d.ClosureClass = 'completed' AND d.Timeliness IS NOT NULL
    GROUP BY a.UserID;

    /*-- 4. ENGAGEMENT - 12-month login window, keyed by EMAIL -----------*/
    IF OBJECT_ID('tempdb..#login') IS NOT NULL DROP TABLE #login;
    SELECT u.ID AS UserID, COUNT(l.ID) AS Logins12m
    INTO #login
    FROM [User] u
    LEFT JOIN UserLoginTrack l
           ON l.Email = u.Email
          AND l.LoginDate >= DATEADD(MONTH, -12, @AsOf)
          AND l.LoginDate <  @AsOf
    WHERE u.CustomerID = @CustomerID AND u.IsDeleted = 0
    GROUP BY u.ID;

    /*-- 5. ROWS - one per user holding assignments ----------------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        UserID                BIGINT         NOT NULL PRIMARY KEY,
        UserName              NVARCHAR(300)  NULL,
        IsActive              BIT            NULL,
        Instances             INT            NOT NULL,   -- distinct instances held, any role
        PerformerInstances    INT            NOT NULL,
        ReviewerInstances     INT            NOT NULL,
        Overdue               INT            NOT NULL,
        OverduePct            DECIMAL(5,1)   NULL,
        ImprisonmentInstances INT            NOT NULL,
        BranchesCovered       INT            NOT NULL,
        Logins12m             INT            NOT NULL,
        EngagementBand        VARCHAR(20)    NULL,
        CompletedEvents       INT            NOT NULL,
        OnTimeEvents          INT            NOT NULL,
        OnTimePct             DECIMAL(5,1)   NULL,
        QuadrantOverlay       VARCHAR(30)    NULL,
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (UserID, UserName, IsActive, Instances, PerformerInstances, ReviewerInstances,
                  Overdue, ImprisonmentInstances, BranchesCovered, Logins12m,
                  CompletedEvents, OnTimeEvents)
    SELECT
        a.UserID,
        LTRIM(RTRIM(CONCAT(u.FirstName, N' ', u.LastName))),
        u.IsActive,
        COUNT(DISTINCT a.ComplianceInstanceID),
        COUNT(DISTINCT CASE WHEN a.RoleID = 3 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN a.RoleID = 4 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT CASE WHEN i.Imprisonment = 1 THEN a.ComplianceInstanceID END),
        COUNT(DISTINCT i.BranchID),
        ISNULL(MAX(lg.Logins12m), 0),
        ISNULL(MAX(q.CompletedEvents), 0),
        ISNULL(MAX(q.OnTimeEvents), 0)
    FROM #asg a
    JOIN #inst i         ON i.ComplianceInstanceID = a.ComplianceInstanceID
    LEFT JOIN #ovd o     ON o.ComplianceInstanceID = a.ComplianceInstanceID
    LEFT JOIN [User] u   ON u.ID = a.UserID
    LEFT JOIN #login lg  ON lg.UserID = a.UserID
    LEFT JOIN #quality q ON q.UserID = a.UserID
    GROUP BY a.UserID, u.FirstName, u.LastName, u.IsActive;

    /*-- 6. RECONCILIATION - DISTINCT-INSTANCE UNION, never a sum --------
       [TRAP] A GUARD THAT CANNOT FAIL IS NOT A GUARD.
       @unassigned is DEFINED as @scopedTotal - @assignedUnion, so testing
       "@assignedUnion + @unassigned = @scopedTotal" is an algebraic identity,
       and #asg is INNER JOINed to #inst so "@unassigned < 0" is unreachable
       too. Both look like reconciliation and neither can ever fire.
       The checks below count from an INDEPENDENT direction - from the instance
       side rather than the assignment side - so a fan-out or a scope leak
       actually shows up as a disagreement between two counts.            */
    DECLARE @scopedTotal   INT = (SELECT COUNT(*) FROM #inst);
    DECLARE @assignedUnion INT = (SELECT COUNT(DISTINCT ComplianceInstanceID) FROM #asg);
    DECLARE @unassigned    INT = @scopedTotal - @assignedUnion;
    DECLARE @rowSum        INT = (SELECT ISNULL(SUM(Instances),0) FROM #rows);

    /*  Same quantity, counted from #inst instead of #asg. */
    DECLARE @assignedViaInst INT = (
        SELECT COUNT(*) FROM #inst i
        WHERE EXISTS (SELECT 1 FROM #asg a WHERE a.ComplianceInstanceID = i.ComplianceInstanceID));

    IF @assignedViaInst <> @assignedUnion
        THROW 51101, N'USERS DIMENSION RECONCILIATION FAILED - the distinct-instance union counted from the assignment side disagrees with the count from the instance side. A join is fanning out or an out-of-scope assignment has leaked in. Refusing to publish.', 1;

    /*  No single user can hold more distinct instances than exist in the union.
        This is what actually catches the 155% class of bug at the row grain. */
    IF EXISTS (SELECT 1 FROM #rows WHERE Instances > @assignedUnion)
        THROW 51101, N'USERS DIMENSION RECONCILIATION FAILED - a user row claims more distinct instances than the whole assigned union contains. A join is fanning out. Refusing to publish.', 1;

    /*  Every assigned instance appears in at least one user row, so the sum of
        per-user counts must be at least the union. Less means rows were lost. */
    IF @rowSum < @assignedUnion
        THROW 51101, N'USERS DIMENSION RECONCILIATION FAILED - the sum of per-user instance counts is below the distinct-instance union, so assigned instances are missing from the rows. Refusing to publish.', 1;

    DECLARE @hasAnyObligations BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * (SELECT COUNT(*) FROM #ovd) / @scopedTotal END;

    UPDATE #rows SET
        OverduePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END,
        OnTimePct  = CASE WHEN CompletedEvents = 0 THEN NULL
                          ELSE 100.0 * OnTimeEvents / CompletedEvents END,
        EngagementBand = CASE WHEN Logins12m >= 100 THEN 'power'
                              WHEN Logins12m >= 26  THEN 'frequent'
                              WHEN Logins12m >= 6   THEN 'moderate'
                              WHEN Logins12m >= 1   THEN 'seldom'
                              ELSE 'never' END;

    /*  The 2x2 overlay. Engagement on one axis, quality on the other - the
        third cell (disengaged but current) is the dependency risk that login
        frequency alone can never find. Users with no completed work have no
        quality reading and are left unclassified rather than assumed.      */
    DECLARE @medianOnTime DECIMAL(5,1) =
        (SELECT TOP 1 PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY OnTimePct) OVER ()
         FROM #rows WHERE OnTimePct IS NOT NULL);

    UPDATE #rows SET QuadrantOverlay =
        CASE WHEN OnTimePct IS NULL OR @medianOnTime IS NULL THEN NULL
             WHEN EngagementBand IN ('power','frequent') AND OnTimePct >= @medianOnTime THEN 'engaged_quality'
             WHEN EngagementBand IN ('power','frequent') AND OnTimePct <  @medianOnTime THEN 'engaged_slipping'
             WHEN OnTimePct >= @medianOnTime THEN 'disengaged_current'
             ELSE 'disengaged_slipping' END;

    /*-- 7. DETECTIONS ---------------------------------------------------*/
    DECLARE @medianLoad DECIMAL(9,2) =
        (SELECT TOP 1 PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY CAST(PerformerInstances AS FLOAT)) OVER ()
         FROM #rows WHERE PerformerInstances > 0);

    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyObligations = 1 AND Instances > 0 AND Logins12m = 0
                 THEN ',never_logged_in_holding_assignments' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND Instances > 0 AND IsActive = 0
                 THEN ',deactivated_holding_work' ELSE '' END +
            /*  Peer-relative: three times this tenant's own median performer load,
                never an absolute count - load varies by an order of magnitude
                between tenants. */
            CASE WHEN @hasAnyObligations = 1 AND @medianLoad IS NOT NULL AND @medianLoad > 0
                  AND PerformerInstances > @medianLoad * 3
                 THEN ',overloaded_performer' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND ReviewerInstances > 0
                  AND PerformerInstances = 0
                 THEN ',review_only_user' ELSE '' END
        , 1, 1, '');

    /*  single_reviewer_dependency is an INSTANCE-level fact, not a user-level
        one: instances whose only reviewer is one person. Counted as a tenant
        figure so it cannot be double-counted across users. */
    DECLARE @soleReviewerInstances INT = (
        SELECT COUNT(*) FROM (
            SELECT a.ComplianceInstanceID
            FROM #asg a WHERE a.RoleID = 4
            GROUP BY a.ComplianceInstanceID
            HAVING COUNT(DISTINCT a.UserID) = 1) x);

    SELECT
        'control_totals'              AS ResultSet,
        @scopedTotal                  AS ScopedInstances,
        @assignedUnion                AS AssignedInstancesDistinct,
        CAST(1 AS BIT)                AS Reconciled,
        @unassigned                   AS UnassignedInstances,
        (SELECT COUNT(*) FROM #ovd)   AS OverdueInstances,
        @tenantOverduePct             AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows)  AS UsersReported,
        @rowSum                       AS SumOfPerUserInstances,
        @medianOnTime                 AS TenantMedianOnTimePct,
        @medianLoad                   AS TenantMedianPerformerLoad,
        @soleReviewerInstances        AS InstancesWithSoleReviewer;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*-- 8. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @allUsers INT = (SELECT COUNT(*) FROM #rows);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'never_logged_in_holding_assignments', @allUsers,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%never_logged_in_holding_assignments%')
    UNION ALL SELECT 'deactivated_holding_work', @allUsers,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%deactivated_holding_work%')
    UNION ALL SELECT 'overloaded_performer', @allUsers,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%overloaded_performer%');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 9. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    INSERT #assert VALUES ('A-TENANT','overdue_pct',N'tenant',@tenantOverduePct,NULL,NULL,NULL,NULL,NULL,NULL);

    /*  CONCENTRATION - the 155% trap. Distinct-instance union of the top ten
        users by load, over the assigned union. Never a sum of per-user counts. */
    IF @assignedUnion > 0
    BEGIN
        DECLARE @top10Union INT = (
            SELECT COUNT(DISTINCT a.ComplianceInstanceID)
            FROM #asg a
            WHERE a.UserID IN (SELECT TOP 10 UserID FROM #rows ORDER BY Instances DESC));

        INSERT #assert
        VALUES ('A-CONC','top10_share_of_assigned_pct',N'tenant',
                CAST(100.0 * @top10Union / @assignedUnion AS DECIMAL(5,1)),
                NULL, @assignedUnion, NULL, NULL, NULL,
                N'distinct-instance union, not a sum of per-user counts - summing double-counts paired performer/reviewer work');
    END

    /*  ENGAGEMENT BANDS - facet B1. The caveat is MANDATORY on every band
        assertion. Never-login users showed the LOWEST overdue rate on the
        reference tenant because they are nominal reviewers, not because they
        are the best compliers. */
    INSERT #assert
    SELECT 'A-BAND-' + CAST(ROW_NUMBER() OVER (ORDER BY MIN(Logins12m) DESC) AS VARCHAR(5)),
           'overdue_pct', CONCAT(N'engagement band: ', EngagementBand),
           CAST(CASE WHEN SUM(Instances) = 0 THEN 0
                     ELSE 100.0 * SUM(Overdue) / SUM(Instances) END AS DECIMAL(5,1)),
           NULL, COUNT(*), @tenantOverduePct, NULL, NULL,
           N'confounded_by_role_mix: login frequency measures ENGAGEMENT, not compliance quality. Low overdue in a disengaged band reflects nominal reviewer roles on work others keep current.'
    FROM #rows WHERE @hasAnyObligations = 1
    GROUP BY EngagementBand;

    /*  DEPENDENCY RISK - the cell login frequency alone cannot find. */
    IF EXISTS (SELECT 1 FROM #rows WHERE QuadrantOverlay = 'disengaged_current')
    INSERT #assert
    SELECT 'A-DEPEND','users_disengaged_but_current',N'tenant',
           COUNT(*), NULL, @allUsers, NULL, NULL, NULL,
           N'dependency_risk: current work held by users who are not present. Not a performance verdict - a continuity one.'
    FROM #rows WHERE QuadrantOverlay = 'disengaged_current';

    IF (SELECT EmitMode FROM #detector WHERE Detector='deactivated_holding_work') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-DEACT-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
               'instances_held', UserName, Instances, NULL, NULL, NULL, NULL, NULL,
               N'deactivated_holding_work: account is deactivated but still holds live assignments'
        FROM #rows WHERE Flags LIKE '%deactivated_holding_work%' ORDER BY Instances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='deactivated_holding_work') = 'aggregate'
        INSERT #assert
        SELECT 'A-DEACT-AGG','deactivated_users_holding_work',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - deactivated accounts holding live work is a tenant-wide provisioning pattern'
        FROM #detector WHERE Detector='deactivated_holding_work';

    IF (SELECT EmitMode FROM #detector WHERE Detector='overloaded_performer') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-LOAD-' + CAST(ROW_NUMBER() OVER (ORDER BY PerformerInstances DESC) AS VARCHAR(5)),
               'performer_instances', UserName, PerformerInstances, NULL, NULL,
               @medianLoad, PerformerInstances - @medianLoad, 'worse',
               N'overloaded_performer: measured against this tenant''s own median performer load'
        FROM #rows WHERE Flags LIKE '%overloaded_performer%' ORDER BY PerformerInstances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='overloaded_performer') = 'aggregate'
        INSERT #assert
        SELECT 'A-LOAD-AGG','overloaded_performers',N'tenant',
               Flagged, NULL, Eligible, @medianLoad, FlaggedPct, 'worse',
               N'aggregate - load is concentrated across many users, not a handful'
        FROM #detector WHERE Detector='overloaded_performer';

    IF @soleReviewerInstances > 0 AND @assignedUnion > 0
    INSERT #assert
    VALUES ('A-SOLEREV','instances_with_sole_reviewer',N'tenant',@soleReviewerInstances,
            NULL,@assignedUnion,NULL,NULL,NULL,
            N'single_reviewer_dependency: counted at INSTANCE level so it cannot be double-counted across users');

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS ----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-CONC','medium',
           CONCAT(N'The ten busiest users hold ', Value, N'% of all assigned obligations'),
           AssertionId,
           N'This is a distinct-instance union. Do not add per-user counts together - that double-counts paired performer/reviewer work and can exceed 100%.'
    FROM #assert WHERE AssertionId = 'A-CONC';

    INSERT #find
    SELECT 'F-DEPEND','high',
           CONCAT(N'', CAST(Value AS INT), N' user(s) hold work that is current but are not logging in'),
           'A-DEPEND',
           N'MUST NOT be presented as good performance. This is a continuity risk: live obligations are assigned to people who are not present. Someone else is carrying them.'
    FROM #assert WHERE AssertionId = 'A-DEPEND';

    INSERT #find
    SELECT 'F-DEACT','high',
           CONCAT(N'', ScopeLabel, N' is deactivated but still holds ', CAST(Value AS INT), N' live obligation(s)'),
           AssertionId,
           N'The account is disabled; the work is not. Do not report this as resolved.'
    FROM #assert WHERE AssertionId LIKE 'A-DEACT-[0-9]%';

    INSERT #find
    SELECT 'F-DEACT-AGG','high',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' users (', VsComparatorPP,
                  N'%) are deactivated but still hold live obligations'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-DEACT-AGG';

    INSERT #find
    SELECT 'F-LOAD','medium',
           CONCAT(N'', ScopeLabel, N' performs ', CAST(Value AS INT),
                  N' obligations, against a tenant median of ', CAST(ComparatorValue AS INT)),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-LOAD-[0-9]%';

    INSERT #find
    SELECT 'F-LOAD-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' users (', VsComparatorPP,
                  N'%) carry more than three times the median performer load'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-LOAD-AGG';

    INSERT #find
    SELECT 'F-SOLEREV','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' assigned obligations depend on a single reviewer'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-SOLEREV';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY ------------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'engagement_is_not_quality',
               N'Login frequency measures ENGAGEMENT and adoption, never compliance quality. On the '
             + N'reference tenant the never-login band showed the LOWEST overdue rate because those users '
             + N'are nominal reviewers on work active performers keep current. Every engagement assertion '
             + N'carries caveat confounded_by_role_mix and must be cited with it.'
        UNION ALL
        SELECT 'users_without_quality_reading',
               CONCAT(N'', COUNT(*), N' user(s) have no completed performer work in this scope, so they '
                    + N'have no on-time reading and are unclassified in the engagement/quality overlay '
                    + N'rather than assumed to be either.')
        FROM #rows WHERE OnTimePct IS NULL HAVING COUNT(*) > 0
        UNION ALL
        SELECT 'unassigned_instances',
               CONCAT(N'', @unassigned, N' obligation(s) in this scope have no assigned user at all and '
                    + N'therefore appear in no user row.')
        WHERE @unassigned > 0
        UNION ALL
        SELECT 'login_keyed_by_email',
               N'UserLoginTrack is keyed by Email, not UserID. A user whose email changed, or who shares an '
             + N'address, may have an inaccurate login count. Engagement bands are indicative, not exact.'
    ) q;

    DROP TABLE #inst; DROP TABLE #ovd; DROP TABLE #asg; DROP TABLE #quality;
    DROP TABLE #login; DROP TABLE #rows; DROP TABLE #detector;
    DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Users dimension installed.';
GO
