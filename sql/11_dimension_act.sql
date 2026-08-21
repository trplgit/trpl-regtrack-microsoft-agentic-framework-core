/*===========================================================================
  RegTrack Insights - Phase 1b, Step 6
  ACT DIMENSION

  Spec reference : docs/DIMENSION_SPECS.md section 6
  Pattern        : sql/05_dimension_location.sql. Emits SIX result sets.
  Error block    : 51090-51099

  -- THE HEADLINE PATTERN: ACT x GEOGRAPHY -----------------------------------
  The SAME Act performs wildly differently by state - the Factories Act ran
  11.7% overdue in one state against 60.6% in another, across 951 instances in
  three states. A national Act-level view hides that; the state cut localises
  accountability. Act.State is a column on the Act row, so one Act row is one
  state and "the same Act across states" means several Act rows sharing a name.
  That is what act_state_divergence groups on.

  The divergence threshold is peer-relative BY CONSTRUCTION: an Act is compared
  against ITSELF in other states, not against an absolute rate. A 60-point
  spread means the law is not the variable - execution is.

  -- [TRAP] UNLINKED IS NOT THE SAME AS ORPHANED ------------------------------
  The member list is derived from the instances themselves, so an instance with
  a NULL ActID joins no Act row and would make the per-Act sum fall short and
  THROW on correct data. That is a CONFIGURATION GAP: counted separately, added
  back for reconciliation, and DECLARED - the same treatment sql/09 gives an
  untagged nature and sql/10 a NULL DepartmentID. Never silently dropped, never
  invented into a fake "Unlinked" Act a narrator could name as if it existed.
  An instance whose ActID is NOT NULL but matches no Act row is a REFERENTIAL
  BREAK - a different thing entirely - and stays fatal.

  -- [OPEN] "EMERGING LAW" HAS NO FIELD --------------------------------------
  The spec expects emerging laws (DPDP, POSH, Apprentices) to show adoption lag.
  Nothing in the schema marks a law as emerging, so Act.StartDate is used as a
  proxy: an Act whose StartDate is recent relative to this tenant's own Act
  population. A hardcoded list of law names would be exactly the kind of literal
  this project bans. CONFIRM the proxy with the BA before narrating adoption lag
  to a customer.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_Act', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_Act;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Act
    @UserID      INT,
    @CustomerID  INT,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT --------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51090, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*-- 1. SCOPED INSTANCE BASE ----------------------------------------*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.Imprisonment,
        s.ActID,
        s.CategoryId
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;

    CREATE CLUSTERED INDEX IX_inst ON #inst (ActID, ComplianceInstanceID);

    /*-- 2. OVERDUE ------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;

    /*-- 3. MEMBER LIST = the Acts actually in scope ---------------------
       Unlike Location or Nature there is no bounded master worth walking -
       the Act table is national. The member list is the set of Acts this
       scope touches, which is why an Act with zero instances cannot exist
       here and no "unused member" declaration is needed.               */
    IF OBJECT_ID('tempdb..#act') IS NOT NULL DROP TABLE #act;
    SELECT DISTINCT a.ID AS ActID, a.Name AS ActName, a.State, a.StateID,
           a.RegulatorID, a.ComplianceCategoryId AS CategoryId, a.StartDate
    INTO #act
    FROM Act a
    WHERE EXISTS (SELECT 1 FROM #inst i WHERE i.ActID = a.ID);

    /*-- 4. ROWS ---------------------------------------------------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        ActID                 INT            NOT NULL PRIMARY KEY,
        ActName               NVARCHAR(500)  NULL,
        State                 NVARCHAR(200)  NULL,
        RegulatorID           INT            NULL,
        CategoryId            INT            NULL,
        Instances             INT            NOT NULL,
        Overdue               INT            NOT NULL,
        OverduePct            DECIMAL(5,1)   NULL,
        ImprisonmentInstances INT            NOT NULL,
        BranchesCovered       INT            NOT NULL,
        -- derived
        StartDate             DATETIME       NULL,
        OverdueRank           INT            NULL,
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (ActID, ActName, State, RegulatorID, CategoryId, Instances, Overdue,
                  ImprisonmentInstances, BranchesCovered, StartDate)
    SELECT
        a.ActID, a.ActName, a.State, a.RegulatorID, a.CategoryId,
        COUNT(i.ComplianceInstanceID),
        SUM(CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END),
        COUNT(DISTINCT i.BranchID),
        a.StartDate
    FROM #act a
    LEFT JOIN #inst i ON i.ActID = a.ActID
    LEFT JOIN #ovd  o ON o.ComplianceInstanceID = i.ComplianceInstanceID
    GROUP BY a.ActID, a.ActName, a.State, a.RegulatorID, a.CategoryId, a.StartDate;

    /*-- 5. RECONCILIATION, with the unlinked bucket counted back --------*/
    DECLARE @rowSum      INT = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #inst);
    DECLARE @unlinked    INT = (SELECT COUNT(*) FROM #inst WHERE ActID IS NULL);
    DECLARE @orphan      INT = (SELECT COUNT(*) FROM #inst i
                                WHERE i.ActID IS NOT NULL
                                  AND NOT EXISTS (SELECT 1 FROM #act a WHERE a.ActID = i.ActID));

    IF @orphan > 0
        THROW 51091, N'ACT DIMENSION RECONCILIATION FAILED - an instance carries an ActID absent from the Act master. This is a referential break, not a linkage gap. Refusing to publish.', 1;

    IF @rowSum + @unlinked <> @scopedTotal
        THROW 51091, N'ACT DIMENSION RECONCILIATION FAILED - per-Act sums plus the unlinked bucket do not tie to the scoped instance total. Refusing to publish.', 1;

    DECLARE @hasAnyObligations BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * (SELECT COUNT(*) FROM #ovd) / @scopedTotal END;

    UPDATE #rows SET
        OverduePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END;

    DECLARE @materialityFloor INT = 50;
    DECLARE @materialMembers  INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @materialityFloor);
    DECLARE @rankDegraded     BIT = CASE WHEN @materialMembers < 2 THEN 1 ELSE 0 END;
    DECLARE @rankFloor        INT = CASE WHEN @rankDegraded = 1 THEN 1 ELSE @materialityFloor END;

    ;WITH r AS (SELECT ActID, RANK() OVER (ORDER BY OverduePct DESC) AS rk
                FROM #rows WHERE Instances >= @rankFloor)
    UPDATE #rows SET OverdueRank = r.rk FROM #rows JOIN r ON r.ActID = #rows.ActID;

    /*-- 6. ACT x STATE DIVERGENCE --------------------------------------
       Group Act rows by NAME. Where one law appears in two or more states
       and both cuts are material, the spread between its best and worst
       state is the finding. The act is its own peer, so this is inherently
       peer-relative - no absolute rate is asserted.                     */
    IF OBJECT_ID('tempdb..#spread') IS NOT NULL DROP TABLE #spread;
    SELECT r.ActName,
           COUNT(*)                AS StatesCovered,
           SUM(r.Instances)        AS Instances,
           MIN(r.OverduePct)       AS MinOverduePct,
           MAX(r.OverduePct)       AS MaxOverduePct,
           MAX(r.OverduePct) - MIN(r.OverduePct) AS SpreadPP
    INTO #spread
    FROM #rows r
    WHERE r.Instances >= @rankFloor AND r.State IS NOT NULL
    GROUP BY r.ActName
    HAVING COUNT(*) >= 2;

    /*-- 7. DETECTIONS ---------------------------------------------------*/
    DECLARE @medianStart DATETIME =
        (SELECT TOP 1 PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY CAST(StartDate AS FLOAT)) OVER ()
         FROM #rows WHERE StartDate IS NOT NULL);

    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyObligations = 1 AND Instances >= @rankFloor
                  AND EXISTS (SELECT 1 FROM #spread s WHERE s.ActName = #rows.ActName AND s.SpreadPP >= 20.0)
                 THEN ',act_state_divergence' ELSE '' END +
            CASE WHEN @hasAnyObligations = 1 AND Instances >= @rankFloor
                  AND @medianStart IS NOT NULL AND StartDate > @medianStart
                  AND OverduePct > @tenantOverduePct
                 THEN ',emerging_law_adoption_lag' ELSE '' END
        , 1, 1, '');

    /*  Regulator concentration is a TENANT-level fact, not a per-Act flag:
        the share of the estate governed by the single largest regulator. */
    DECLARE @topRegulatorShare DECIMAL(5,1) = NULL;
    DECLARE @topRegulatorId INT = NULL;
    IF @hasAnyObligations = 1
        SELECT TOP 1 @topRegulatorId = RegulatorID,
                     @topRegulatorShare = CAST(100.0 * SUM(Instances) / @scopedTotal AS DECIMAL(5,1))
        FROM #rows WHERE RegulatorID IS NOT NULL
        GROUP BY RegulatorID ORDER BY SUM(Instances) DESC;

    SELECT
        'control_totals'             AS ResultSet,
        @scopedTotal                 AS ScopedInstances,
        @rowSum                      AS SumOfRows,
        CAST(1 AS BIT)               AS Reconciled,
        (SELECT COUNT(*) FROM #ovd)  AS OverdueInstances,
        @tenantOverduePct            AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows) AS ActsReported,
        (SELECT COUNT(DISTINCT ActName) FROM #rows) AS DistinctActNames,
        (SELECT COUNT(DISTINCT State) FROM #rows WHERE State IS NOT NULL) AS StatesCovered,
        (SELECT COUNT(*) FROM #spread) AS ActsSpanningMultipleStates,
        @unlinked                    AS UnlinkedInstances,
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * @unlinked / @scopedTotal END AS UnlinkedPct,
        @topRegulatorId              AS LargestRegulatorId,
        @topRegulatorShare           AS LargestRegulatorSharePct;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*-- 8. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @material INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'act_state_divergence', @material,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%act_state_divergence%')
    UNION ALL SELECT 'emerging_law_adoption_lag', @material,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%emerging_law_adoption_lag%');

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

    DECLARE @rankable  INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor);
    DECLARE @tiedAtTop INT = (SELECT COUNT(*) FROM #rows WHERE Instances >= @rankFloor AND OverdueRank = 1);

    IF @rankable >= 2
    INSERT #assert
    SELECT TOP 1 'A-WORST-ACT','overdue_pct',
           CONCAT(ActName, CASE WHEN State IS NULL THEN N'' ELSE CONCAT(N' (', State, N')') END),
           OverduePct,OverdueRank,@rankable,
           @tenantOverduePct, OverduePct - @tenantOverduePct,
           CASE WHEN OverduePct > @tenantOverduePct THEN 'worse' ELSE 'better' END,
           NULLIF(CONCAT(
               CASE WHEN @rankDegraded = 1
                    THEN CONCAT(N'degraded_ranking_sample: no Act reaches the ', @materialityFloor,
                                N'-instance materiality floor. ') ELSE N'' END,
               CASE WHEN @tiedAtTop > 1
                    THEN CONCAT(N'tied_at_top: ', @tiedAtTop, N' Acts share this rate - not uniquely the highest. ')
                    ELSE N'' END), N'')
    FROM #rows WHERE Instances >= @rankFloor ORDER BY OverduePct DESC, Instances DESC;

    /*  The state-divergence assertion carries the SPREAD, not a rate, because
        the finding is that the same law is executed differently - the law is
        not the variable. */
    IF (SELECT EmitMode FROM #detector WHERE Detector='act_state_divergence') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-DIV-' + CAST(ROW_NUMBER() OVER (ORDER BY s.Instances DESC) AS VARCHAR(5)),
               'overdue_spread_pp', s.ActName, s.SpreadPP, NULL, s.StatesCovered,
               s.MinOverduePct, s.MaxOverduePct - s.MinOverduePct, 'worse',
               N'act_state_divergence: the same law, executed differently by state - the law is not the variable'
        FROM #spread s WHERE s.SpreadPP >= 20.0 ORDER BY s.Instances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='act_state_divergence') = 'aggregate'
        INSERT #assert
        SELECT 'A-DIV-AGG','acts_diverging_by_state',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - execution varies by state across many laws, not a handful of exceptions'
        FROM #detector WHERE Detector='act_state_divergence';

    IF (SELECT EmitMode FROM #detector WHERE Detector='emerging_law_adoption_lag') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-LAG-' + CAST(ROW_NUMBER() OVER (ORDER BY Instances DESC) AS VARCHAR(5)),
               'overdue_pct', ActName, OverduePct, NULL, NULL,
               @tenantOverduePct, OverduePct - @tenantOverduePct, 'worse',
               N'emerging_law_adoption_lag: recency inferred from Act.StartDate, NOT from a confirmed emerging-law list - state as a hypothesis'
        FROM #rows WHERE Flags LIKE '%emerging_law_adoption_lag%' ORDER BY Instances DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='emerging_law_adoption_lag') = 'aggregate'
        INSERT #assert
        SELECT 'A-LAG-AGG','recent_laws_with_adoption_lag',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - recency inferred from Act.StartDate, not a confirmed emerging-law list'
        FROM #detector WHERE Detector='emerging_law_adoption_lag';

    IF @topRegulatorShare IS NOT NULL
    INSERT #assert
    VALUES ('A-REG','regulator_share_pct',
            CONCAT(N'regulator ', @topRegulatorId), @topRegulatorShare, NULL, NULL, NULL, NULL, NULL, NULL);

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS ----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-WORST-ACT','high',
           CONCAT(N'', ScopeLabel, N' has the highest overdue rate at ', Value, N'%'),
           'A-WORST-ACT,A-TENANT', NULL
    FROM #assert WHERE AssertionId = 'A-WORST-ACT' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-DIV','high',
           CONCAT(N'', ScopeLabel, N' varies by ', Value, N' points across ', OfN, N' states'),
           AssertionId,
           N'The law is identical in each state - the variation is execution, not regulation. Do not attribute it to the law being harder somewhere.'
    FROM #assert WHERE AssertionId LIKE 'A-DIV-[0-9]%';

    INSERT #find
    SELECT 'F-DIV-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' Acts (', VsComparatorPP,
                  N'%) are executed materially differently across states'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-DIV-AGG';

    INSERT #find
    SELECT 'F-LAG','medium',
           CONCAT(N'', ScopeLabel, N' runs at ', Value, N'% overdue, above the tenant average'),
           AssertionId,
           N'Recency is inferred from Act.StartDate, not a confirmed emerging-law list. Present as a hypothesis to verify, never as an established adoption problem.'
    FROM #assert WHERE AssertionId LIKE 'A-LAG-[0-9]%';

    INSERT #find
    SELECT 'F-LAG-AGG','info',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' recently-started Acts (', VsComparatorPP,
                  N'%) run above the tenant overdue rate'),
           AssertionId,
           N'Recency inferred from Act.StartDate. Present as a hypothesis, not a conclusion.'
    FROM #assert WHERE AssertionId = 'A-LAG-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY ------------------------------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'emerging_law_proxy',
               N'"Emerging law" is not a field in the schema. Adoption-lag detection uses Act.StartDate '
             + N'relative to this tenant''s own Act population as a proxy. Any adoption-lag statement is a '
             + N'hypothesis to confirm with the BA, not an established finding. (Open item.)'
        WHERE EXISTS (SELECT 1 FROM #rows WHERE Flags LIKE '%emerging_law_adoption_lag%')
        UNION ALL
        SELECT 'acts_without_state',
               CONCAT(N'', COUNT(*), N' Act(s) in this scope carry no State value, so they are excluded '
                    + N'from the Act-by-state comparison.')
        FROM #rows WHERE State IS NULL HAVING COUNT(*) > 0
        UNION ALL
        SELECT 'instances_not_linked_to_an_act',
               CONCAT(N'', @unlinked, N' obligation(s) in this scope are not linked to any Act. They are '
                    + N'counted in the tenant total but appear in NO Act row, so every per-Act figure '
                    + N'below excludes them and the rows do not sum to the scoped total. A configuration '
                    + N'gap, not a defect.')
        WHERE @unlinked > 0
    ) q;

    DROP TABLE #inst; DROP TABLE #ovd; DROP TABLE #act; DROP TABLE #spread;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Act dimension installed.';
GO
