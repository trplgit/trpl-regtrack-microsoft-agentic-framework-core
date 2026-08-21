/*===========================================================================
  RegTrack Insights - Phase 1b, Step 3
  RISK DIMENSION

  Spec reference : docs/DIMENSION_SPECS.md section 5
  Pattern        : sql/05_dimension_location.sql is the reference implementation.

  -- CONTRACT ---------------------------------------------------------------
  Emits SIX result sets: control_totals, rows, detector_policy, assertions,
  findings, data_quality.

  -- WHAT THIS DIMENSION IS FOR ---------------------------------------------
  The spec names no detector list for Risk. It names TWO findings the narrative
  must be able to express, and one trap. Everything below exists to serve those
  three statements and nothing else:

    1. "Critical items are the BEST-managed" - on the reference tenant Critical
       ran 20.8% overdue against a ~29% tenant average. The comparative must be
       COMPUTED with a direction, or a narrator reading only the volume will
       report the largest tier as the biggest problem. Direction can be 'better'.

    2. "The coverage gap hides in the MIDDLE tiers" - High 31 + Medium 43
       ownerless against Critical's 9. Attention follows severity; ownership
       does not.

    [TRAP] Critical and imprisonment are ~95% the SAME population (1,419 of
    1,424). They are NOT independent axes. Presenting them as two findings tells
    the reader the same thing twice and inflates the apparent problem count. The
    overlap is therefore emitted as an assertion plus a narrative_guard, which
    is the mechanism this project uses to bind a narrator: "narrative_guard is an
    instruction, not a note".

  -- THE MEMBER LIST IS THE DICTIONARY --------------------------------------
  Rows are built from InsightsEnumPolarity, not from the instances, so a risk
  level carrying zero obligations still produces a row - and a level the
  dictionary does not know about cannot silently absorb instances. If an
  instance carries a RiskType absent from the dictionary it lands in no row,
  the per-level sum falls short of the scoped total, and reconciliation THROWs.
  That is deliberate: fail closed and loudly, never bucket an unknown enum.

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_Risk', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_Risk;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Risk
    @UserID      INT,
    @CustomerID  INT,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*=====================================================================
      0. PRE-FLIGHT - fail closed before computing anything
    =====================================================================*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51060, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- THROWs on dictionary gap

    /*  Every risk value comes from the dictionary. A literal here would be an
        enum literal in a WHERE clause - non-negotiable #4 - and would make this
        proc agree with a wrong seed by construction. */
    IF OBJECT_ID('tempdb..#risk') IS NOT NULL DROP TABLE #risk;
    SELECT TRY_CAST(p.RawValue AS INT) AS RiskType,
           p.Meaning                   AS RiskLabel
    INTO #risk
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'RiskType' AND TRY_CAST(p.RawValue AS INT) IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM #risk)
        THROW 51062, N'DICTIONARY GAP - no RiskType values are mapped in InsightsEnumPolarity. Refusing to compute a risk dimension.', 1;

    DECLARE @criticalRisk INT = (SELECT TOP 1 RiskType FROM #risk WHERE RiskLabel LIKE N'Critical%');

    IF @criticalRisk IS NULL
        THROW 51062, N'DICTIONARY GAP - no RiskType value is mapped to Critical in InsightsEnumPolarity. Refusing to compute.', 1;

    /*=====================================================================
      1. SCOPED INSTANCE BASE - both scope axes
    =====================================================================*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        s.RiskType,
        s.Imprisonment
    INTO #inst
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s;

    CREATE CLUSTERED INDEX IX_inst ON #inst (RiskType, ComplianceInstanceID);

    /*=====================================================================
      2. OVERDUE (dictionary-driven, affirmative form)
    =====================================================================*/
    IF OBJECT_ID('tempdb..#ovd') IS NOT NULL DROP TABLE #ovd;
    SELECT DISTINCT o.ComplianceInstanceID
    INTO #ovd
    FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
    JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID;   -- scope-constrained

    /*=====================================================================
      3. OWNERSHIP - an instance with no performer is ownerless
    =====================================================================*/
    IF OBJECT_ID('tempdb..#owned') IS NOT NULL DROP TABLE #owned;
    SELECT DISTINCT ca.ComplianceInstanceID
    INTO #owned
    FROM ComplianceAssignment ca
    JOIN #inst i ON i.ComplianceInstanceID = ca.ComplianceInstanceID
    WHERE ca.RoleID = 3 AND ca.UserID > 0;      -- RoleID 3 = performer

    /*=====================================================================
      4. PER-LEVEL ROWS - from the dictionary member list, not the facts

      [TRAP] Declare the table explicitly with ALL columns, base and derived.
      SELECT ... INTO then ALTER TABLE ADD then referencing the new column in
      the same procedure body fails: T-SQL resolves names for the whole batch
      up front and a procedure cannot contain a GO.
    =====================================================================*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        RiskType              INT            NOT NULL PRIMARY KEY,
        RiskLabel             NVARCHAR(200)  NULL,
        Instances             INT            NOT NULL,
        Overdue               INT            NOT NULL,
        OverduePct            DECIMAL(5,1)   NULL,
        Ownerless             INT            NOT NULL,
        ImprisonmentInstances INT            NOT NULL,
        ImprisonmentOverdue   INT            NOT NULL,
        BranchesCovered       INT            NOT NULL,
        -- derived
        VsTenantPP            DECIMAL(9,2)   NULL,
        Flags                 VARCHAR(200)   NULL
    );

    INSERT #rows (RiskType, RiskLabel, Instances, Overdue, Ownerless,
                  ImprisonmentInstances, ImprisonmentOverdue, BranchesCovered)
    SELECT
        r.RiskType,
        r.RiskLabel,
        COUNT(i.ComplianceInstanceID),
        SUM(CASE WHEN o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.ComplianceInstanceID IS NOT NULL
                  AND w.ComplianceInstanceID IS NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 THEN 1 ELSE 0 END),
        SUM(CASE WHEN i.Imprisonment = 1 AND o.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END),
        COUNT(DISTINCT i.BranchID)
    FROM #risk r
    LEFT JOIN #inst  i ON i.RiskType = r.RiskType
    LEFT JOIN #ovd   o ON o.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN #owned w ON w.ComplianceInstanceID = i.ComplianceInstanceID
    GROUP BY r.RiskType, r.RiskLabel;

    /*=====================================================================
      5. CONTROL TOTALS + MANDATORY RECONCILIATION

      An instance whose RiskType is absent from the dictionary joins no row, so
      the per-level sum falls short and this THROWs. That is the intended
      behaviour - an unknown enum must never be silently bucketed.
    =====================================================================*/
    DECLARE @rowSum      INT = (SELECT ISNULL(SUM(Instances),0) FROM #rows);
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #inst);

    IF @rowSum <> @scopedTotal
        THROW 51061, N'RISK DIMENSION RECONCILIATION FAILED - per-level sums do not tie to the scoped instance total. An instance carries a RiskType absent from InsightsEnumPolarity. Refusing to publish.', 1;

    DECLARE @tenantOverduePct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT COUNT(*) FROM #ovd) / @scopedTotal END;

    DECLARE @hasAnyObligations BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;

    UPDATE #rows SET
        OverduePct = CASE WHEN Instances = 0 THEN 0 ELSE 100.0 * Overdue / Instances END;

    UPDATE #rows SET
        VsTenantPP = CASE WHEN Instances = 0 THEN NULL ELSE OverduePct - @tenantOverduePct END;

    /*  The imprisonment overlap. The spec measured 1,419 of 1,424 = 99.6% of
        imprisonment-bearing instances sitting on the Critical tier, which is why
        Critical and imprisonment must not be narrated as two separate findings. */
    DECLARE @impTotal INT = (SELECT COUNT(*) FROM #inst WHERE Imprisonment = 1);
    DECLARE @impOnCritical INT = (SELECT COUNT(*) FROM #inst WHERE Imprisonment = 1 AND RiskType = @criticalRisk);
    DECLARE @impOverlapPct DECIMAL(5,1) =
        CASE WHEN @impTotal = 0 THEN NULL ELSE 100.0 * @impOnCritical / @impTotal END;

    SELECT
        'control_totals'                  AS ResultSet,
        @scopedTotal                      AS ScopedInstances,
        @rowSum                           AS SumOfRows,
        CAST(1 AS BIT)                    AS Reconciled,
        (SELECT COUNT(*) FROM #ovd)       AS OverdueInstances,
        @tenantOverduePct                 AS TenantOverduePct,
        (SELECT COUNT(*) FROM #rows)      AS RiskLevelsReported,
        (SELECT COUNT(*) FROM #rows WHERE Instances > 0) AS RiskLevelsWithObligations,
        @criticalRisk                     AS CriticalRiskType,
        @impTotal                         AS ImprisonmentInstances,
        @impOverlapPct                    AS ImprisonmentOnCriticalPct;

    /*=====================================================================
      6. ROWS
    =====================================================================*/
    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Instances DESC;

    /*=====================================================================
      7. DETECTIONS

      The spec names no detector list for this dimension. The one detection it
      DOES require is finding 2 - the coverage gap hiding in the middle tiers -
      so that is what is detected: a non-Critical tier carrying MORE ownerless
      obligations than the Critical tier does. Ownership is not following
      severity.

      Guarded on @hasAnyObligations: with nothing in scope every tier holds
      zero, "more than Critical" is 0 > 0 which is false, but the guard makes
      the intent explicit rather than relying on that.
    =====================================================================*/
    DECLARE @criticalOwnerless INT =
        (SELECT ISNULL(MAX(Ownerless),0) FROM #rows WHERE RiskType = @criticalRisk);

    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyObligations = 1
                  AND Instances > 0
                  AND RiskType <> @criticalRisk
                  AND Ownerless > @criticalOwnerless
                 THEN ',ownership_gap_below_critical' ELSE '' END
        , 1, 1, '');

    /*=====================================================================
      8. DETECTOR EMISSION POLICY

      Eligible and Flagged are drawn from the same population - tiers holding
      obligations - so the rate cannot exceed 100%.

      Note this dimension has at most four members, so flooding is structurally
      impossible and the 20% aggregate threshold will almost always tip to
      'aggregate' (1 of 4 = 25%). That is harmless here: with four members an
      aggregate statement and four individual ones carry the same information.
    =====================================================================*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector      VARCHAR(40) PRIMARY KEY,
        Eligible      INT,
        Flagged       INT,
        FlaggedPct    DECIMAL(5,1),
        EmitMode      VARCHAR(12)
    );

    DECLARE @withObl INT = (SELECT COUNT(*) FROM #rows WHERE Instances > 0);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'ownership_gap_below_critical', @withObl,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%ownership_gap_below_critical%');

    UPDATE #detector
       SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;

    /*  [TRAP] AGGREGATING A SMALL MEMBER SET DESTROYS THE FINDING.
        The aggregate mode exists to stop a detector emitting 117 findings on an
        819-branch tenant. Individual findings are already capped at the top 5 by
        materiality, so when the eligible set is 5 or fewer the cap ALREADY bounds
        the output and aggregating cannot reduce it - it only replaces named members
        with a percentage. Measured on the Risk dimension, whose grain is fixed at
        four levels: 3 of 4 tipped to 'aggregate' and the finding became "3 of 4 risk
        levels (75%)", losing the tier names that ARE the finding the spec requires
        ("the coverage gap hides in the MIDDLE tiers - High 31 + Medium 43 against
        Critical's 9"). Below the cap, always name the members.                     */
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*=====================================================================
      9. TYPED ASSERTIONS - comparatives COMPUTED here
    =====================================================================*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId  VARCHAR(20),
        Metric       VARCHAR(60),
        ScopeLabel   NVARCHAR(500),
        Value        DECIMAL(18,2),
        Rank_        INT NULL,
        OfN          INT NULL,
        ComparatorValue DECIMAL(18,2) NULL,
        VsComparatorPP  DECIMAL(9,2) NULL,
        Direction    VARCHAR(10) NULL,
        Caveat       NVARCHAR(500) NULL
    );

    INSERT #assert VALUES ('A-TENANT','overdue_pct',N'tenant',@tenantOverduePct,NULL,NULL,NULL,NULL,NULL,NULL);

    /*  FINDING 1. The direction is the whole point. On the reference tenant
        Critical ran 20.8% against a 29% average - BETTER than average, because
        the organisation triages correctly. A narrator given only the volume
        would report the largest tier as the biggest problem, which inverts the
        finding. The comparative is computed here so it cannot be vibed.        */
    IF @hasAnyObligations = 1
    INSERT #assert
    SELECT 'A-CRIT','overdue_pct',RiskLabel,OverduePct,NULL,NULL,
           @tenantOverduePct, VsTenantPP,
           CASE WHEN OverduePct > @tenantOverduePct THEN 'worse' ELSE 'better' END,
           NULL
    FROM #rows WHERE RiskType = @criticalRisk AND Instances > 0;

    /*  THE TRAP, as an assertion. Critical and imprisonment are ~95% the same
        population; this states the overlap so a narrator can see it rather than
        treating the two as independent axes.                                   */
    IF @impTotal > 0
    INSERT #assert
    VALUES ('A-IMP-OVERLAP','imprisonment_on_critical_pct',N'tenant',@impOverlapPct,
            NULL,@impTotal,NULL,NULL,NULL,
            N'critical_and_imprisonment_overlap: these are largely the SAME obligations, not two independent exposures');

    /*  FINDING 2. Ownership not following severity. Policy-gated. */
    IF (SELECT EmitMode FROM #detector WHERE Detector='ownership_gap_below_critical') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-OWNGAP-' + CAST(ROW_NUMBER() OVER (ORDER BY Ownerless DESC) AS VARCHAR(5)),
               'ownerless', RiskLabel, Ownerless, NULL, NULL,
               @criticalOwnerless, Ownerless - @criticalOwnerless, 'worse',
               N'ownership_gap_below_critical: attention follows severity, ownership does not'
        FROM #rows WHERE Flags LIKE '%ownership_gap_below_critical%' ORDER BY Ownerless DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='ownership_gap_below_critical') = 'aggregate'
        /*  ComparatorValue stays NULL. Value here is a COUNT OF RISK LEVELS; putting
            Critical's ownerless COUNT beside it puts two different units in one
            assertion, and a narrator reading 3 against 3 could write "equal to the
            Critical tier", which is meaningless. Aggregates in sql/05 leave it NULL
            for the same reason.                                                    */
        INSERT #assert
        SELECT 'A-OWNGAP-AGG','risk_levels_with_ownership_gap',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - unassigned ownership sits below the Critical tier across several levels'
        FROM #detector WHERE Detector='ownership_gap_below_critical';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*=====================================================================
      10. FINDINGS - every one backed by assertion ids

      [TRAP] Anchor individual findings on [0-9] so the aggregate assertion
      cannot also produce an individual-shaped finding.
    =====================================================================*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (
        FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400),
        NarrativeGuard NVARCHAR(500) NULL
    );

    /*  Critical BETTER than average - the counter-intuitive case. The guard is
        load-bearing: without it a narrator reports the largest, scariest-sounding
        tier as the problem. */
    INSERT #find
    SELECT 'F-CRIT-BETTER','info',
           CONCAT(N'', ScopeLabel, N' obligations run at ', Value, N'% overdue, ',
                  ABS(VsComparatorPP), N' points BELOW the tenant average'),
           'A-CRIT,A-TENANT',
           N'MUST NOT be presented as a failure. This tier is better managed than the tenant average - the organisation triages correctly. Do not narrate Critical volume as a problem.'
    FROM #assert WHERE AssertionId = 'A-CRIT' AND Direction = 'better';

    INSERT #find
    SELECT 'F-CRIT-WORSE','high',
           CONCAT(N'', ScopeLabel, N' obligations run at ', Value, N'% overdue, ',
                  VsComparatorPP, N' points above the tenant average'),
           'A-CRIT,A-TENANT',
           N'Do not also raise imprisonment exposure as a separate finding - see A-IMP-OVERLAP, they are largely the same obligations.'
    FROM #assert WHERE AssertionId = 'A-CRIT' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-OWNGAP','high',
           CONCAT(N'', ScopeLabel, N' carries ', CAST(Value AS INT),
                  N' obligations with no assigned owner, more than the Critical tier'),
           AssertionId,
           N'Attention follows severity; ownership does not. The coverage gap is in the middle tiers, not the top one.'
    FROM #assert WHERE AssertionId LIKE 'A-OWNGAP-[0-9]%';

    INSERT #find
    SELECT 'F-OWNGAP-AGG','high',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' risk levels (', VsComparatorPP,
                  N'%) carry more unassigned obligations than the Critical tier'),
           AssertionId,
           N'Attention follows severity; ownership does not.'
    FROM #assert WHERE AssertionId = 'A-OWNGAP-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*=====================================================================
      11. DATA QUALITY - declared, never silent
    =====================================================================*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
        SELECT 'flow_metric_drift' AS Issue,
               N'Overdue is a live figure and moves between runs; stock metrics are stable.' AS Detail
        UNION ALL
        SELECT 'critical_imprisonment_overlap',
               CONCAT(N'', @impOverlapPct, N'% of imprisonment-bearing obligations sit on the ',
                      N'Critical tier. Critical risk and personal liability are largely the SAME ',
                      N'population, not independent axes. Do not present them as two findings.')
        WHERE @impTotal > 0
        UNION ALL
        SELECT 'risk_levels_unused',
               CONCAT(N'', COUNT(*), N' mapped risk level(s) carry no obligations in this scope ',
                      N'and are reported with zero counts rather than omitted.')
        FROM #rows WHERE Instances = 0 HAVING COUNT(*) > 0
    ) q;

    DROP TABLE #risk; DROP TABLE #inst; DROP TABLE #ovd; DROP TABLE #owned;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Risk dimension installed.';
GO

