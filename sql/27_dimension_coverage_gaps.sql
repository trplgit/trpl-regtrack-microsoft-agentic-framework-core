/*===========================================================================
  RegTrack Insights - Phase 1b (extension)
  COVERAGE GAPS DIMENSION - peer comparison within state x establishment class

  Pattern     : sql/05_dimension_location.sql. Emits SIX result sets.
  [RENUMBERED 2026-09-04] from sql/22 - that number is taken by the deployed
  BacklogAging. Error block moved from 5117x (shared by sql/22-25) to 5120x.
  Error block : 51200-51209 (x0 scope | x1-x4 reconciliation | x5-x9 dictionary)

  -- WHAT THIS FINDS THAT NOTHING ELSE CAN --------------------------------
  An obligation that was never configured for a store is INVISIBLE to RegTrack:
  no schedule, no reminder, no overdue alert, nothing on any report. A store can
  look perfectly compliant while carrying an untracked statutory obligation. The
  only way to surface it is INFERENCE - compare a store against its peers.

  -- THE LEGAL PREMISE, AND WHAT IT RESTS ON ------------------------------
  "Two establishments of the same class, of the same company, in the same
  state, should carry substantially the same LABOUR obligations."

  Tested against primary statute for every state in the reference footprint
  (docs/LEGAL_BRIEF_research_addendum.md). It holds, with two qualifications
  that this proc implements rather than ignores:

    1. HEADCOUNT is the dominant lawful within-state variation (ESI/POSH at 10,
       factory 20/40, creche 50, canteen 100, welfare officer 250). Headcount is
       NOT available - the customer does not supply it. So:
         a) establishment CLASS (CustomerBranch.Type via NodeType) is the proxy,
         b) threshold-bearing obligations above a typical store are EXCLUDED
            from comparison rather than flagged falsely.

    2. Shops & Establishments Acts apply BY SCHEDULED AREA in the older-Act
       states (UP 1962, Bihar 1953, Jharkhand, Karnataka 1961, Delhi 1954,
       MP/CG 1958) and STATEWIDE only where a new Act exists (Maharashtra 2017,
       Gujarat 2019). A store in an unscheduled town may lawfully lack S&E
       obligations. Central obligations (ESI, EPF, POSH, gratuity, wages, bonus)
       are unaffected. So S&E-derived gaps are emitted at REDUCED confidence.

  -- WHY STATE x CLASS, NOT STATE ALONE ------------------------------------
  Measured on the reference retailer: Warehouses carry ~10 distinct Labour
  compliances and Head Offices ~17, against ~44 for a store. Under a state-only
  peer set every one of them would be flagged for ~30 lawful "gaps". Branch(2)
  and Store(5) carry IDENTICAL profiles (75.6 vs 76.0 avg obligations) and are
  ONE class; 77% of all production branches are the generic "Branch".

  -- [TRAP] Labour is Act.ComplianceCategoryId = 2. NOT ComplianceInstance
     .IsAvantis, which is set on 97.6% of instances and discriminates nothing.
     An earlier "613 gaps" figure was produced with IsAvantis and does not
     reproduce; the category join gives 380 on the same tenant.

  -- [TRAP] Peer set = leaf, Status=1, class-eligible, carrying >=1 Labour
     obligation. Half-built shells with zero Labour would otherwise drag every
     coverage ratio below threshold and hide real gaps.

  -- FRAMING - NON-NEGOTIABLE -----------------------------------------------
  Every finding is a REVIEW CANDIDATE, never a violation. No applicability
  rules table exists; a genuine exemption can explain any single gap. The
  narrative_guard on every finding says so.

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_CoverageGaps', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_CoverageGaps;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_CoverageGaps
    @UserID          INT,
    @CustomerID      INT,
    @AsOf            DATETIME     = NULL,
    @CoverageThreshold DECIMAL(5,4) = 0.95,   -- peers carrying it, as a share
    @MinPeers        INT          = 10,       -- smallest peer set worth comparing
    @LabourCategory  INT          = 2
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT - fail closed ---------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51200, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;

    /*  Establishment class comes from the DICTIONARY, never a literal, so the
        BA can move Warehouse into retail (or out) without touching code.     */
    IF OBJECT_ID('tempdb..#class') IS NOT NULL DROP TABLE #class;
    SELECT TRY_CAST(p.RawValue AS INT) AS NodeTypeId, p.Meaning AS Class
    INTO #class
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'CustomerBranch.Type' AND TRY_CAST(p.RawValue AS INT) IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM #class)
        THROW 51205, N'DICTIONARY GAP - no CustomerBranch.Type values are classified in InsightsEnumPolarity. Seed the NodeType classification (retail / warehouse / plant / office / nonphysical) and re-run.', 1;

    /*-- 1. THE PEER POPULATION ------------------------------------------
        Leaf, operating, in scope, with a known class. Unknown NodeType values
        (17 exist in production, none in NodeType) are classed 'unknown' and
        excluded - never compared, always declared.                          */
    IF OBJECT_ID('tempdb..#branch') IS NOT NULL DROP TABLE #branch;
    CREATE TABLE #branch (
        BranchID    INT           NOT NULL PRIMARY KEY,
        BranchName  NVARCHAR(300) NULL,
        StateID     INT           NULL,
        NodeTypeId  INT           NULL,
        Class       VARCHAR(20)   NOT NULL,
        Comparable  BIT           NOT NULL    -- retail | warehouse | plant only
    );
    INSERT #branch (BranchID, BranchName, StateID, NodeTypeId, Class, Comparable)
    SELECT DISTINCT cb.ID, cb.Name, cb.StateID, cb.Type,
           ISNULL(c.Class, 'unknown'),
           CASE WHEN ISNULL(c.Class,'unknown') IN ('retail','warehouse','plant') THEN 1 ELSE 0 END
    FROM CustomerBranch cb
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp ON sp.BranchID = cb.ID
    LEFT JOIN #class c ON c.NodeTypeId = cb.Type
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1
      AND NOT EXISTS (SELECT 1 FROM CustomerBranch ch
                      WHERE ch.ParentID = cb.ID AND ch.IsDeleted = 0 AND ch.Status = 1);

    /*-- 2. LABOUR OBLIGATIONS PER BRANCH, and the S&E / threshold tags ------
        [TRAP] Threshold-bearing obligations are identified by ACT NAME pattern
        because no applicability-rules table exists. This is a declared
        approximation, not a precise filter. Extend the patterns as BAs rule.  */
    IF OBJECT_ID('tempdb..#obl') IS NOT NULL DROP TABLE #obl;
    CREATE TABLE #obl (
        BranchID     INT NOT NULL,
        ComplianceID INT NOT NULL,
        ActID        INT NULL,
        ActName      NVARCHAR(500) NULL,
        IsShopsAct   BIT NOT NULL,      -- S&E-derived -> reduced confidence
        IsThresholdObligation BIT NOT NULL,   -- excluded from comparison
        PRIMARY KEY (BranchID, ComplianceID)
    );
    INSERT #obl (BranchID, ComplianceID, ActID, ActName, IsShopsAct, IsThresholdObligation)
    SELECT DISTINCT b.BranchID, i.ComplianceID, a.ID, a.Name,
        CASE WHEN a.Name LIKE '%Shops%' OR a.Name LIKE '%Dookan%' OR a.Name LIKE '%Commercial Establishment%'
             THEN 1 ELSE 0 END,
        CASE WHEN c.ShortDescription LIKE '%creche%'  OR c.ShortDescription LIKE '%cr' + NCHAR(232) + 'che%'
               OR c.ShortDescription LIKE '%canteen%'
               OR c.ShortDescription LIKE '%welfare officer%'
               OR a.Name LIKE '%Contract Labour%'
             THEN 1 ELSE 0 END
    FROM #branch b
    JOIN ComplianceInstance i ON i.CustomerBranchID = b.BranchID AND i.IsDeleted = 0
    JOIN Compliance c ON c.ID = i.ComplianceID AND c.IsDeleted = 0
    JOIN Act a ON a.ID = c.ActID
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp
         ON sp.BranchID = b.BranchID AND sp.CategoryId = a.ComplianceCategoryId
    WHERE a.ComplianceCategoryId = @LabourCategory;

    /*  Peer set = comparable branches that carry at least one Labour obligation. */
    IF OBJECT_ID('tempdb..#peer') IS NOT NULL DROP TABLE #peer;
    SELECT b.BranchID, b.BranchName, b.StateID, b.Class
    INTO #peer
    FROM #branch b
    WHERE b.Comparable = 1 AND b.StateID IS NOT NULL
      AND EXISTS (SELECT 1 FROM #obl o WHERE o.BranchID = b.BranchID);

    IF OBJECT_ID('tempdb..#group') IS NOT NULL DROP TABLE #group;
    SELECT StateID, Class, COUNT(*) AS Peers
    INTO #group
    FROM #peer GROUP BY StateID, Class;

    /*-- 3. NEAR-UNIVERSAL OBLIGATIONS per (state, class) ------------------*/
    IF OBJECT_ID('tempdb..#nu') IS NOT NULL DROP TABLE #nu;
    SELECT o.ComplianceID, p.StateID, p.Class,
           MAX(o.ActName) AS ActName,
           MAX(CAST(o.IsShopsAct AS INT)) AS IsShopsAct,
           COUNT(DISTINCT p.BranchID) AS PeersHaving,
           g.Peers,
           CAST(1.0 * COUNT(DISTINCT p.BranchID) / g.Peers AS DECIMAL(5,4)) AS Coverage
    INTO #nu
    FROM #peer p
    JOIN #obl o ON o.BranchID = p.BranchID AND o.IsThresholdObligation = 0
    JOIN #group g ON g.StateID = p.StateID AND g.Class = p.Class
    WHERE g.Peers >= @MinPeers
    GROUP BY o.ComplianceID, p.StateID, p.Class, g.Peers
    HAVING 1.0 * COUNT(DISTINCT p.BranchID) / g.Peers >= @CoverageThreshold
       AND COUNT(DISTINCT p.BranchID) < g.Peers;     -- someone is missing it

    /*-- 4. THE GAPS: (peer, near-universal obligation it lacks) -----------*/
    IF OBJECT_ID('tempdb..#gap') IS NOT NULL DROP TABLE #gap;
    SELECT p.BranchID, p.BranchName, p.StateID, p.Class,
           nu.ComplianceID, nu.ActName, nu.IsShopsAct, nu.PeersHaving, nu.Peers, nu.Coverage,
           CASE WHEN nu.IsShopsAct = 1 THEN 'reduced' ELSE 'full' END AS Confidence
    INTO #gap
    FROM #nu nu
    JOIN #peer p ON p.StateID = nu.StateID AND p.Class = nu.Class
    WHERE NOT EXISTS (SELECT 1 FROM #obl o
                      WHERE o.BranchID = p.BranchID AND o.ComplianceID = nu.ComplianceID);

    /*-- 5. UNDER-CONFIGURED: materially below the peer-set MEDIAN ---------
        Peer-relative (CLAUDE.md Sec.4): below 60% of the (state, class) median
        distinct-Labour-obligation count. An absolute floor would flag every
        small-format store on every tenant.                                   */
    IF OBJECT_ID('tempdb..#load') IS NOT NULL DROP TABLE #load;
    SELECT p.BranchID, p.StateID, p.Class, COUNT(DISTINCT o.ComplianceID) AS LabourCount
    INTO #load
    FROM #peer p JOIN #obl o ON o.BranchID = p.BranchID
    GROUP BY p.BranchID, p.StateID, p.Class;

    IF OBJECT_ID('tempdb..#median') IS NOT NULL DROP TABLE #median;
    SELECT DISTINCT StateID, Class,
           PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY LabourCount)
               OVER (PARTITION BY StateID, Class) AS MedianCount
    INTO #median FROM #load;

    /*-- 6. PER-BRANCH ROWS - from the BRANCH list, not the gap set ---------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        BranchID              INT           NOT NULL PRIMARY KEY,
        BranchName            NVARCHAR(300) NULL,
        StateID               INT           NULL,
        NodeTypeId            INT           NULL,
        Class                 VARCHAR(20)   NOT NULL,
        InPeerSet             BIT           NOT NULL,
        PeerSetSize           INT           NULL,
        LabourObligations     INT           NOT NULL,
        PeerMedianObligations DECIMAL(9,1)  NULL,
        PctOfPeerMedian       DECIMAL(6,1)  NULL,
        Gaps                  INT           NOT NULL,
        GapsFullConfidence    INT           NOT NULL,
        GapsReducedConfidence INT           NOT NULL,
        UnderConfigured       BIT           NOT NULL,
        GapRank               INT           NULL,
        Flags                 VARCHAR(200)  NULL
    );
    INSERT #rows (BranchID, BranchName, StateID, NodeTypeId, Class, InPeerSet, PeerSetSize,
                  LabourObligations, PeerMedianObligations, PctOfPeerMedian,
                  Gaps, GapsFullConfidence, GapsReducedConfidence, UnderConfigured)
    SELECT b.BranchID, b.BranchName, b.StateID, b.NodeTypeId, b.Class,
           CASE WHEN p.BranchID IS NOT NULL THEN 1 ELSE 0 END,
           g.Peers,
           ISNULL(l.LabourCount, 0),
           m.MedianCount,
           CASE WHEN m.MedianCount > 0 THEN 100.0 * ISNULL(l.LabourCount,0) / m.MedianCount END,
           ISNULL(gp.n, 0), ISNULL(gp.nfull, 0), ISNULL(gp.nred, 0),
           CASE WHEN p.BranchID IS NOT NULL AND g.Peers >= @MinPeers
                 AND m.MedianCount > 0 AND ISNULL(l.LabourCount,0) < 0.60 * m.MedianCount
                THEN 1 ELSE 0 END
    FROM #branch b
    LEFT JOIN #peer   p  ON p.BranchID = b.BranchID
    LEFT JOIN #group  g  ON g.StateID = b.StateID AND g.Class = b.Class
    LEFT JOIN #load   l  ON l.BranchID = b.BranchID
    LEFT JOIN #median m  ON m.StateID = b.StateID AND m.Class = b.Class
    LEFT JOIN (SELECT BranchID, COUNT(*) AS n,
                      SUM(CASE WHEN Confidence='full' THEN 1 ELSE 0 END) AS nfull,
                      SUM(CASE WHEN Confidence='reduced' THEN 1 ELSE 0 END) AS nred
               FROM #gap GROUP BY BranchID) gp ON gp.BranchID = b.BranchID;

    ;WITH rk AS (SELECT BranchID, RANK() OVER (ORDER BY Gaps DESC) AS n FROM #rows WHERE Gaps > 0)
    UPDATE #rows SET GapRank = rk.n FROM #rows JOIN rk ON rk.BranchID = #rows.BranchID;

    UPDATE r SET Flags = STUFF(
          CASE WHEN r.Class = 'unknown'                                  THEN ',unknown_node_type'     ELSE '' END
        + CASE WHEN r.Class IN ('office','nonphysical')                  THEN ',not_comparable_class'  ELSE '' END
        + CASE WHEN r.InPeerSet = 1 AND ISNULL(r.PeerSetSize,0) < @MinPeers THEN ',peer_set_too_small' ELSE '' END
        + CASE WHEN r.Gaps > 0                                           THEN ',coverage_gap'          ELSE '' END
        + CASE WHEN r.UnderConfigured = 1                                THEN ',under_configured'      ELSE '' END
        + CASE WHEN r.InPeerSet = 0 AND b.Comparable = 1                 THEN ',no_labour_obligations' ELSE '' END
        , 1, 1, '')
    FROM #rows r JOIN #branch b ON b.BranchID = r.BranchID;

    /*-- 7. CONTROL TOTALS + RECONCILIATION -------------------------------*/
    DECLARE @gapTotal INT = (SELECT COUNT(*) FROM #gap);
    DECLARE @gapRows  INT = (SELECT ISNULL(SUM(Gaps),0) FROM #rows);
    IF @gapTotal <> @gapRows
        THROW 51201, N'COVERAGE GAPS RECONCILIATION FAILED - per-branch gap counts do not tie to the gap set. Refusing to publish.', 1;

    DECLARE @branchTotal INT = (SELECT COUNT(*) FROM #branch);
    DECLARE @rowTotal    INT = (SELECT COUNT(*) FROM #rows);
    IF @branchTotal <> @rowTotal
        THROW 51202, N'COVERAGE GAPS RECONCILIATION FAILED - rows do not cover every in-scope leaf branch.', 1;

    SELECT 'control_totals' AS ResultSet,
        @branchTotal                                              AS LeafBranchesInScope,
        (SELECT COUNT(*) FROM #peer)                              AS PeerSetBranches,
        (SELECT COUNT(*) FROM #group WHERE Peers >= @MinPeers)    AS PeerGroupsQualifying,
        (SELECT COUNT(*) FROM #group WHERE Peers <  @MinPeers)    AS PeerGroupsTooSmall,
        (SELECT COUNT(*) FROM #nu)                                AS NearUniversalObligations,
        @gapTotal                                                 AS Gaps,
        @gapRows                                                  AS SumOfRowGaps,
        CAST(1 AS BIT)                                            AS Reconciled,
        (SELECT COUNT(*) FROM #gap WHERE Confidence='full')       AS GapsFullConfidence,
        (SELECT COUNT(*) FROM #gap WHERE Confidence='reduced')    AS GapsReducedConfidence,
        (SELECT COUNT(DISTINCT BranchID) FROM #gap)               AS BranchesWithGaps,
        (SELECT COUNT(*) FROM #rows WHERE UnderConfigured = 1)    AS UnderConfiguredBranches,
        (SELECT COUNT(*) FROM #branch WHERE Class = 'unknown')    AS UnknownNodeType,
        (SELECT COUNT(DISTINCT ComplianceID) FROM #obl WHERE IsThresholdObligation = 1) AS ThresholdObligationsExcluded,
        @CoverageThreshold                                        AS CoverageThreshold,
        @MinPeers                                                 AS MinPeers,
        N'Peer key = state x establishment class (CustomerBranch.Type via NodeType). Labour = Act.ComplianceCategoryId. REVIEW CANDIDATES, not violations.' AS Method;

    /*-- 8. ROWS ------------------------------------------------------------*/
    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY Gaps DESC, LabourObligations DESC;

    /*-- 9. DETECTOR POLICY (CLAUDE.md Sec.4) ------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
                            FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));
    DECLARE @eligible INT = (SELECT COUNT(*) FROM #rows WHERE InPeerSet = 1);
    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'coverage_gap',     @eligible, (SELECT COUNT(*) FROM #rows WHERE Gaps > 0)
    UNION ALL SELECT 'under_configured', @eligible, (SELECT COUNT(*) FROM #rows WHERE UnderConfigured = 1);
    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible=0 THEN 0 ELSE 100.0*Flagged/Eligible END;
    UPDATE #detector SET EmitMode = CASE WHEN Flagged=0 THEN 'none' WHEN FlaggedPct>20.0 THEN 'aggregate' ELSE 'individual' END;
    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 10. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(300),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL, ComparatorValue DECIMAL(18,2) NULL,
        VsComparatorPP DECIMAL(9,2) NULL, Direction VARCHAR(10) NULL, Caveat NVARCHAR(400) NULL);

    INSERT #assert VALUES ('A-GAPS','coverage_gaps',N'tenant',@gapTotal,NULL,NULL,NULL,NULL,NULL,
        N'review candidates - no applicability-rules table exists; a genuine exemption can explain any single gap');

    IF (SELECT EmitMode FROM #detector WHERE Detector='coverage_gap') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-GAP-' + CAST(ROW_NUMBER() OVER (ORDER BY Gaps DESC) AS VARCHAR(5)),
               'coverage_gaps', BranchName, Gaps, GapRank,
               (SELECT COUNT(*) FROM #rows WHERE Gaps > 0), NULL, NULL, NULL,
               CASE WHEN GapsReducedConfidence > 0
                    THEN CONCAT(N'', GapsReducedConfidence, N' of these are S&E-Act obligations - reduced confidence: a store in an unscheduled town may lawfully lack them')
                    ELSE N'review candidates' END
        FROM #rows WHERE Gaps > 0 ORDER BY Gaps DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='coverage_gap') = 'aggregate'
        INSERT #assert
        SELECT 'A-GAP-AGG','branches_with_gaps',N'tenant',Flagged,NULL,Eligible,NULL,FlaggedPct,NULL,
               N'aggregate - gaps are a tenant-wide pattern; check whether a whole obligation set was never rolled out'
        FROM #detector WHERE Detector='coverage_gap';

    /*  The single most useful row: which obligation is missing from the most stores. */
    INSERT #assert
    SELECT TOP 1 'A-TOPOBL','stores_missing_obligation', ActName,
           COUNT(*), NULL, NULL, NULL, NULL, NULL,
           CASE WHEN MAX(IsShopsAct)=1 THEN N'S&E-Act obligation - reduced confidence' ELSE NULL END
    FROM #gap GROUP BY ActName ORDER BY COUNT(*) DESC;

    IF (SELECT EmitMode FROM #detector WHERE Detector='under_configured') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-UNDER-' + CAST(ROW_NUMBER() OVER (ORDER BY PctOfPeerMedian) AS VARCHAR(5)),
               'pct_of_peer_median', BranchName, PctOfPeerMedian, NULL, NULL,
               PeerMedianObligations, PctOfPeerMedian - 100.0, 'worse',
               N'headcount is unavailable - a small-format store may lawfully carry fewer obligations'
        FROM #rows WHERE UnderConfigured = 1 ORDER BY PctOfPeerMedian;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='under_configured') = 'aggregate'
        INSERT #assert
        SELECT 'A-UNDER-AGG','under_configured_branches',N'tenant',Flagged,NULL,Eligible,NULL,FlaggedPct,NULL,
               N'aggregate - likely a format or headcount pattern rather than per-store misses'
        FROM #detector WHERE Detector='under_configured';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 11. FINDINGS - every one carries the review-candidate guard ---------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10), Headline NVARCHAR(400),
                        AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(400) NULL);
    DECLARE @guard NVARCHAR(400) =
        N'Present as items to REVIEW, never as violations or missing compliances. No applicability rules table exists; a lawful exemption can explain any gap.';

    INSERT #find
    SELECT 'F-GAP','high',
           CONCAT(N'', CAST(Value AS INT), N' obligations carried by nearly all peers are absent from individual locations'),
           'A-GAPS', @guard
    FROM #assert WHERE AssertionId='A-GAPS' AND Value > 0;

    INSERT #find
    SELECT 'F-TOPOBL','high',
           CONCAT(N'"', ScopeLabel, N'" is configured on nearly every peer but missing from ', CAST(Value AS INT), N' location(s)'),
           'A-TOPOBL', @guard
    FROM #assert WHERE AssertionId='A-TOPOBL';

    INSERT #find
    SELECT 'F-GAP-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' peer-comparable locations (', VsComparatorPP, N'%) show coverage gaps'),
           'A-GAP-AGG', @guard
    FROM #assert WHERE AssertionId='A-GAP-AGG';

    INSERT #find
    SELECT 'F-UNDER-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' locations (', VsComparatorPP, N'%) carry materially fewer obligations than their peers'),
           'A-UNDER-AGG',
           N'Headcount is unavailable; small-format locations may lawfully carry fewer obligations. Present as a configuration review, not a shortfall.'
    FROM #assert WHERE AssertionId='A-UNDER-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 12. DATA QUALITY - the declared limits of the method ---------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
        SELECT 'review_candidates_only' AS Issue,
               N'Every gap is an inference from peer configuration, not a confirmed obligation. No statutory-applicability rules table exists.' AS Detail
        UNION ALL
        SELECT 'headcount_unavailable',
               N'Employee headcount is not supplied by the customer. Establishment class (NodeType) is the proxy; threshold-bearing obligations (creche, canteen, contract labour, welfare officer) are excluded rather than flagged.'
        UNION ALL
        SELECT 'shops_act_area_mechanism',
               N'Shops & Establishments Acts apply by scheduled area in UP, Bihar, Jharkhand, Karnataka, Delhi and MP/CG, and statewide only in Maharashtra and Gujarat. Gaps in S&E-derived obligations are emitted at reduced confidence.'
        UNION ALL
        SELECT 'unknown_node_types',
               CONCAT(N'', (SELECT COUNT(*) FROM #branch WHERE Class='unknown'),
                      N' leaf branch(es) carry a Type absent from NodeType and were excluded from comparison.')
        WHERE EXISTS (SELECT 1 FROM #branch WHERE Class='unknown')
        UNION ALL
        SELECT 'peer_groups_too_small',
               CONCAT(N'', (SELECT COUNT(*) FROM #group WHERE Peers < @MinPeers),
                      N' (state, class) group(s) have fewer than ', @MinPeers, N' peers and were not compared.')
        WHERE EXISTS (SELECT 1 FROM #group WHERE Peers < @MinPeers)
        UNION ALL
        SELECT 'recent_state_amendments',
               N'Gujarat and Delhi restricted their S&E Acts to 20+ employees in 2026. Configured absences there may be recent and lawful.'
        UNION ALL
        SELECT 'branch_store_labelling',
               N'NodeType Branch and Store are used interchangeably in reference data and are treated as one class.'
    ) q;

    DROP TABLE #class; DROP TABLE #branch; DROP TABLE #obl; DROP TABLE #peer; DROP TABLE #group;
    DROP TABLE #nu; DROP TABLE #gap; DROP TABLE #load; DROP TABLE #median; DROP TABLE #rows;
    DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Coverage gaps dimension installed.';
GO
