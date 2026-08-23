/*===========================================================================
  RegTrack Insights - Phase 1a, Step 1
  CLASSIFICATION DICTIONARY: schema, seed, and helpers

  Spec reference : RegTrack_Insights_System_Design_v1.md Sec.6.1 - Sec.6.5
  Purpose        : Single versioned source of truth for ALL status/enum/polarity
                   semantics. Every stored procedure and the free-tier aggregator
                   read from here. No WHERE-clause literals anywhere else.

  WHY THIS EXISTS (do not delete this comment):
    Six semantic traps were found empirically during design, several of which
    produced silently wrong numbers on first attempt:
      - RiskType is 3=Critical, 0=High, 1=Medium, 2=Low  (not 1..4)
      - ProductMapping.IsActive is INVERTED (0=enabled, 1=disabled)
      - ComplianceStatus has duplicate names AND whitespace variants
      - "Not Applicable" is TWO statuses (15 reviewer-final, 18 performer-proposed)
      - EntitiesAssignment scope is 2-D (branch x category)
      - Compliance instances live on INTERMEDIATE entity nodes, not just leaves
    A procedure library maintained across years and 600 tenants will hit more.

  IDEMPOTENT: safe to re-run.
  Target     : SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

/*---------------------------------------------------------------------------
  1. VERSION REGISTRY
     Every generated report pins the dictionary version it used, so any number
     can be explained after the fact (spec Sec.7.2 provenance.dictionary_version).
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.InsightsDictionaryVersion', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsDictionaryVersion (
        VersionId       INT           NOT NULL PRIMARY KEY,
        VersionLabel    VARCHAR(20)   NOT NULL,
        EffectiveFrom   DATETIME2(0)  NOT NULL CONSTRAINT DF_IDV_From DEFAULT (SYSUTCDATETIME()),
        IsCurrent       BIT           NOT NULL CONSTRAINT DF_IDV_Cur  DEFAULT (0),
        Notes           NVARCHAR(1000) NULL
    );
END
GO

/*---------------------------------------------------------------------------
  2. STATUS CLASSIFICATION  (spec Sec.6.2)

  THREE FACETS per status - they are independent and all three are needed:
    overdue_eligible : is a PAST-DUE item in this status counted as overdue?
    closure_class    : open | completed | resolved_terminal
    timeliness       : on_time | delayed | NULL

  [TRAP] Status 9 is CLOSED but LATE. It is NOT overdue (work is done) but it
         IS a delayed completion. Carrying only one facet would either count a
         completed item as overdue, or lose the fact that it was late.

  [TRAP] BUCKET BY ID ONLY. Never parse or filter on the display name:
           - "Approved"      = IDs 7 AND 9  (different timeliness)
           - "Rejected"      = IDs 6 AND 8
           - "Not Applicable"= IDs 15 AND 18 (double-space variant)
           - "Not Complied"  = IDs 16 AND 17 (double-space variant)
         A query filtering Name='Not Applicable' matches ID 15 and SILENTLY
         DROPS ID 18's ~67,558 schedules.

  THE GOVERNING RULE that makes the whole table coherent:
         performer-marked = proposed  => OPEN
         reviewer-marked  = finalised => CLOSED
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.InsightsStatusClassification', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsStatusClassification (
        StatusId          INT           NOT NULL,
        VersionId         INT           NOT NULL,
        RealMeaning       NVARCHAR(200) NOT NULL,   -- BA-authoritative
        DisplayNameNote   NVARCHAR(200) NULL,       -- reference only; NEVER parse
        OverdueEligible   BIT           NOT NULL,
        ClosureClass      VARCHAR(20)   NOT NULL,   -- open|completed|resolved_terminal
        Timeliness        VARCHAR(10)   NULL,       -- on_time|delayed|NULL
        IsDeprecated      BIT           NOT NULL CONSTRAINT DF_ISC_Dep DEFAULT (0),
        Notes             NVARCHAR(400) NULL,
        CONSTRAINT PK_InsightsStatusClassification PRIMARY KEY (VersionId, StatusId),
        CONSTRAINT FK_ISC_Version FOREIGN KEY (VersionId)
            REFERENCES dbo.InsightsDictionaryVersion (VersionId),
        CONSTRAINT CK_ISC_ClosureClass CHECK (ClosureClass IN ('open','completed','resolved_terminal')),
        CONSTRAINT CK_ISC_Timeliness   CHECK (Timeliness IS NULL OR Timeliness IN ('on_time','delayed')),
        -- an item cannot be both overdue-eligible and a completed closure
        CONSTRAINT CK_ISC_Coherent CHECK (
            (OverdueEligible = 1 AND ClosureClass = 'open')
         OR (OverdueEligible = 0 AND ClosureClass IN ('completed','resolved_terminal'))
        )
    );
END
GO

/*---------------------------------------------------------------------------
  3. ENUM & POLARITY  (spec Sec.6.3)
     Non-status semantics: risk levels, inverted flags, structural rules.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.InsightsEnumPolarity', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsEnumPolarity (
        VersionId    INT           NOT NULL,
        Semantic     VARCHAR(60)   NOT NULL,   -- e.g. 'RiskType'
        RawValue     VARCHAR(30)   NOT NULL,   -- e.g. '3'
        Meaning      NVARCHAR(200) NOT NULL,   -- e.g. 'Critical'
        AppliesTo    NVARCHAR(120) NOT NULL,
        Notes        NVARCHAR(400) NULL,
        CONSTRAINT PK_InsightsEnumPolarity PRIMARY KEY (VersionId, Semantic, RawValue),
        CONSTRAINT FK_IEP_Version FOREIGN KEY (VersionId)
            REFERENCES dbo.InsightsDictionaryVersion (VersionId)
    );
END
GO

/*---------------------------------------------------------------------------
  4. SEED - DICTIONARY v1.0  (BA-signed-off)
---------------------------------------------------------------------------*/
IF NOT EXISTS (SELECT 1 FROM dbo.InsightsDictionaryVersion WHERE VersionId = 1)
    INSERT dbo.InsightsDictionaryVersion (VersionId, VersionLabel, IsCurrent, Notes)
    VALUES (1, '1.0', 1,
            N'Initial BA-signed-off dictionary. Status facets + enum/polarity. '
          + N'Statuses 8 and 19 provisionally mapped, deprecated pending deletion.');
GO

DELETE FROM dbo.InsightsStatusClassification WHERE VersionId = 1;
GO

INSERT dbo.InsightsStatusClassification
    (StatusId, VersionId, RealMeaning, DisplayNameNote, OverdueEligible, ClosureClass, Timeliness, IsDeprecated, Notes)
VALUES
 ( 1,1,N'Open',                                         N'Open',                              1,'open',             NULL,0,NULL),
 ( 2,1,N'Complied (on time), pending reviewer sign-off',N'Complied but pending review',       1,'open',             NULL,0,N'BA: work not final until reviewed. ~180k schedules system-wide.'),
 ( 3,1,N'Complied (late), pending reviewer sign-off',   N'Complied Delayed but pending review',1,'open',            NULL,0,N'BA: still open until reviewed.'),
 ( 4,1,N'Reviewer-closed, on time',                     N'Closed-Timely',                     0,'completed',        'on_time',0,NULL),
 ( 5,1,N'Reviewer-closed, late',                        N'Closed-Delayed',                    0,'completed',        'delayed',0,NULL),
 ( 6,1,N'Rejected - back to performer',                 N'Rejected (dup name with ID 8)',     1,'open',             NULL,0,NULL),
 ( 7,1,N'Approved - closed BEFORE due date',            N'Approved (dup name with ID 9)',     0,'completed',        'on_time',0,N'BA-confirmed. Reviewer-finalised completion.'),
 ( 8,1,N'Rejected (legacy)',                            N'Rejected (dup name with ID 6)',     1,'open',             NULL,1,N'DEPRECATED pending deletion. Provisional bucket - keep mapped while live rows exist.'),
 ( 9,1,N'Approved - closed AFTER due date',             N'Approved (dup name with ID 7)',     0,'completed',        'delayed',0,N'BA-confirmed. CLOSED but LATE - feeds delayed metric, not overdue.'),
 (10,1,N'In Progress',                                  N'In Progress',                       1,'open',             NULL,0,NULL),
 (11,1,N'Sent back for revision',                       N'Revise Compliance',                 1,'open',             NULL,0,NULL),
 (12,1,N'Submitted for interim review',                 N'Submitted For Interim Review',      1,'open',             NULL,0,NULL),
 (13,1,N'Interim review approved - STILL OPEN',         N'Interim Review Approved',           1,'open',             NULL,0,N'BA-confirmed. Interim != final.'),
 (14,1,N'Interim rejected',                             N'Interim Rejected',                  1,'open',             NULL,0,NULL),
 (15,1,N'Not Applicable - set by REVIEWER (final)',     N'Not Applicable (single space)',     0,'resolved_terminal',NULL,0,N'Reviewer-finalised. EXCLUDED from completion AND timeliness. ~1.1M schedules - would massively inflate on-time% if counted as a completion.'),
 (16,1,N'Not Complied - set by PERFORMER (proposed)',   N'Not  Complied (DOUBLE space)',      1,'open',             NULL,0,N'Performer-proposed => still open.'),
 (17,1,N'Not Complied - set by REVIEWER (final)',       N'Not Complied (single space)',       0,'resolved_terminal',NULL,0,N'Reviewer-finalised MISS. Not a completion - excluded from completion/timeliness.'),
 (18,1,N'Not Applicable - set by PERFORMER (proposed)', N'Not  Applicable (DOUBLE space)',    1,'open',             NULL,0,N'Performer-proposed => still open. ~67.5k schedules a name-filter would silently drop.'),
 (19,1,N'Complied, document pending (legacy)',          N'Complied But Document Pending',     1,'open',             NULL,1,N'DEPRECATED pending deletion. Provisional bucket.'),
 (20,1,N'Pending performer action',                     N'Pending for Performer Action',      1,'open',             NULL,0,NULL),
 (21,1,N'Deviation (extension) APPLIED',                N'Deviation Applied',                 1,'open',             NULL,0,N'Extension requested, not granted.'),
 (22,1,N'Deviation rejected',                           N'Deviation Rejected',                1,'open',             NULL,0,NULL),
 (23,1,N'Deviation APPROVED (extension) - still open',  N'Deviation Approved',                1,'open',             NULL,0,N'BA-confirmed. Extension granted; obligation still open.');
GO

DELETE FROM dbo.InsightsEnumPolarity WHERE VersionId = 1;
GO

INSERT dbo.InsightsEnumPolarity (VersionId, Semantic, RawValue, Meaning, AppliesTo, Notes)
VALUES
 (1,'RiskType','3',N'Critical',N'Compliance.RiskType',N'Empirically confirmed: 1,419 of 1,424 imprisonment-bearing instances are RiskType 3.'),
 (1,'RiskType','0',N'High',    N'Compliance.RiskType',N'BA-confirmed. NOT intuitive - 0 is High, not Low.'),
 (1,'RiskType','1',N'Medium',  N'Compliance.RiskType',NULL),
 (1,'RiskType','2',N'Low',     N'Compliance.RiskType',N'BA-confirmed. NOT intuitive - 2 is Low, not High.'),
 (1,'ProductMapping.IsActive','0',N'ENABLED (subscription active)', N'ProductMapping',N'INVERTED. Confirmed: core Compliance product has ~1,865 customers mapped with only 7 at IsActive=1 (i.e. 7 disabled).'),
 (1,'ProductMapping.IsActive','1',N'DISABLED (subscription stopped)',N'ProductMapping',N'INVERTED - see above.'),
 (1,'IsDeleted','0',N'Active',                        N'User / Customer / CustomerBranch',NULL),
 (1,'IsDeleted','1',N'Inactive (soft-deleted)',       N'User / Customer / CustomerBranch',NULL),
 (1,'User.IsActive','0',N'Deactivated but EXISTS - keep and FLAG, never hide',N'User',N'Distinct from IsDeleted. Filtering to IsActive=1 would hide the "deactivated user still holding live assignments" finding.'),
 (1,'EntityApex','ParentID IS NULL',N'Top-level entity of a tenant (with IsDeleted=0)',N'CustomerBranch',NULL),
 (1,'ScopeSource','EntitiesAssignment',N'2-D scope: (BranchID x ComplianceCatagoryID). Column is misspelled "Catagory".',N'EntitiesAssignment',N'Chosen over ComplianceCategoryMgmtUser: CM is a strict subset (0 exceptions across 2 tenants) that excludes configured-but-dormant sites, which are themselves a key finding.'),
 (1,'CategoryJoin','Act.ComplianceCategoryId',N'The ONLY category join path: ComplianceInstance -> Compliance -> Act',N'Act',N'Compliance, ComplianceInstance and ComplianceSubType have no category column.');
GO

/*---------------------------------------------------------------------------
  5. HELPER VIEW - current dictionary
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.vInsightsStatusCurrent', 'V') IS NOT NULL DROP VIEW dbo.vInsightsStatusCurrent;
GO
CREATE VIEW dbo.vInsightsStatusCurrent
AS
    SELECT sc.*
    FROM dbo.InsightsStatusClassification sc
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = sc.VersionId
    WHERE v.IsCurrent = 1;
GO

/*---------------------------------------------------------------------------
  6. FAIL-CLOSED COVERAGE CHECK  (spec Sec.6.5)

  Any ComplianceStatus present in the system but ABSENT from the dictionary
  must cause a loud failure, never a silent default. Run this:
    - in CI, on every dictionary change
    - as a pre-flight assertion inside the insights orchestration
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_AssertStatusCoverage', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_AssertStatusCoverage;
GO
CREATE PROCEDURE dbo.usp_Insights_AssertStatusCoverage
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @unmapped NVARCHAR(MAX);

    SELECT @unmapped = STRING_AGG(CAST(cs.ID AS VARCHAR(10)) + N'=' + cs.Name, N', ')
    FROM ComplianceStatus cs
    WHERE NOT EXISTS (SELECT 1 FROM dbo.vInsightsStatusCurrent d WHERE d.StatusId = cs.ID);

    /*  CONTRACT: this procedure returns NO RESULT SET.

        [FIX - found in UAT] It is called as a pre-flight by every dimension
        procedure. When it emitted a grid, that grid became result set #1 of
        the CALLER, shifting the documented dimension contract
        (1=control_totals, 2=rows, 3=detector_policy, 4=assertions,
        5=findings, 6=data_quality) by one - and only for the dimensions that
        call it, so a .NET QueryMultiple reader needed a DIFFERENT offset per
        dimension.

        Success is silence. Failure is a THROW.                             */

    IF @unmapped IS NOT NULL
        THROW 51001, N'INSIGHTS DICTIONARY GAP - unmapped ComplianceStatus values found. Refusing to compute. Add them to InsightsStatusClassification before proceeding.', 1;
END
GO

/*---------------------------------------------------------------------------
  7. CANONICAL OVERDUE PREDICATE  (spec Sec.6.4, Appendix A.5)

  [TRAP] The OLD exclusionary form  `status NOT IN (4,5,15,18)`  is WRONG:
     (a) it omits 7 and 9, so COMPLETED items past their due date were counted
         as overdue - measured at 784 such schedules in one production tenant,
         overstating its overdue by 26% (3,558 vs the correct 2,820);
     (b) it defaults any UNKNOWN/new status to overdue - the exact silent drift
         the dictionary exists to prevent.

  Use the AFFIRMATIVE form below. The INNER JOIN to the dictionary is
  deliberate: an unmapped status yields no row, so row-count reconciliation
  fails loudly instead of mis-bucketing.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.tvfInsightsOverdueSchedules', 'IF') IS NOT NULL
    DROP FUNCTION dbo.tvfInsightsOverdueSchedules;
GO
CREATE FUNCTION dbo.tvfInsightsOverdueSchedules (@CustomerID INT, @AsOf DATETIME)
RETURNS TABLE
AS
RETURN
(
    SELECT
        i.ID                        AS ComplianceInstanceID,
        cso.ID                      AS ComplianceScheduleOnID,
        i.CustomerBranchID,
        a.ComplianceCategoryId      AS CategoryId,
        rct.ComplianceStatusID      AS StatusId,
        cso.ScheduleOn
    FROM ComplianceScheduleOn cso
    JOIN ComplianceInstance   i   ON i.ID  = cso.ComplianceInstanceID
    JOIN CustomerBranch       cb  ON cb.ID = i.CustomerBranchID
    JOIN Compliance           c   ON c.ID  = i.ComplianceID
    JOIN Act                  a   ON a.ID  = c.ActID
    JOIN RecentComplianceTransactionView rct ON rct.ComplianceScheduleOnID = cso.ID
    JOIN dbo.vInsightsStatusCurrent d        ON d.StatusId = rct.ComplianceStatusID  -- INNER = fail closed
    WHERE cb.CustomerID = @CustomerID
      AND cb.IsDeleted  = 0
      AND i.IsDeleted   = 0
      AND c.IsDeleted   = 0
      AND cso.IsActive  = 1
      AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn <= @AsOf
      AND d.OverdueEligible = 1
);
GO

/*---------------------------------------------------------------------------
  6b. STATUS DATA-QUALITY PROBE  (NULL / unmapped statuses)

  -- WHY THIS EXISTS --------------------------------------------------------
  Production contains schedules whose ComplianceStatusID is NULL - 10 rows
  system-wide at the time of writing, 9 of them PAST DUE and active, spread
  across 5 tenants (one had 4).

  The canonical overdue function INNER JOINs the dictionary, so a NULL status
  produces no row: it is excluded from overdue. That is safe (it cannot
  over-count) but it is SILENT, which violates "fail closed AND LOUDLY".

  Refusing an entire report because of 1 unknown row in 650,000 would be
  over-strict and would train people to bypass the gate. So the rule is
  PROPORTIONATE, mirroring the labelled-placeholder decision (spec Sec.11.4):

      - ALWAYS surface the count as a data_quality entry on the report
      - RAISE only when the gap exceeds a threshold (absolute or proportional)

  Never silently incomplete; always declared.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_StatusDataQuality', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_StatusDataQuality;
GO
CREATE PROCEDURE dbo.usp_Insights_StatusDataQuality
    @CustomerID        INT,
    @MaxAbsoluteGap    INT           = 100,     -- raise above this many unknown rows
    @MaxProportionPct  DECIMAL(5,2)  = 0.10     -- ...or above this share of past-due
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @pastdue INT, @nullStatus INT, @unmapped INT;

    SELECT
        @pastdue    = COUNT(*),
        @nullStatus = SUM(CASE WHEN rct.ComplianceStatusID IS NULL THEN 1 ELSE 0 END),
        @unmapped   = SUM(CASE WHEN rct.ComplianceStatusID IS NOT NULL
                                AND d.StatusId IS NULL THEN 1 ELSE 0 END)
    FROM ComplianceScheduleOn cso
    JOIN ComplianceInstance i ON i.ID = cso.ComplianceInstanceID
    JOIN CustomerBranch cb    ON cb.ID = i.CustomerBranchID
    JOIN RecentComplianceTransactionView rct ON rct.ComplianceScheduleOnID = cso.ID
    -- [TRAP - CLAUDE.md Sec.3] LEFT JOIN + IS NULL, never SUM(CASE ... NOT EXISTS(...)):
    -- SQL Server rejects an aggregate over a subquery (Msg 130), so the NOT EXISTS form
    -- fails at CREATE time. Semantically identical - the view is filtered to IsCurrent = 1
    -- and keyed (VersionId, StatusId), so there is at most one row per status and no fan-out.
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = rct.ComplianceStatusID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND i.IsDeleted = 0
      AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn <= GETDATE();

    DECLARE @gap INT = ISNULL(@nullStatus,0) + ISNULL(@unmapped,0);
    DECLARE @pct DECIMAL(9,4) = CASE WHEN ISNULL(@pastdue,0) = 0 THEN 0
                                     ELSE @gap * 100.0 / @pastdue END;

    SELECT
        @CustomerID AS CustomerID,
        @pastdue    AS PastDueSchedules,
        @nullStatus AS NullStatusRows,
        @unmapped   AS UnmappedStatusRows,
        @gap        AS TotalUnknown,
        @pct        AS UnknownPct,
        CASE WHEN @gap = 0 THEN 'clean'
             WHEN @gap > @MaxAbsoluteGap OR @pct > @MaxProportionPct THEN 'RAISE'
             ELSE 'declare_in_data_quality' END AS Verdict,
        CASE WHEN @gap = 0 THEN NULL
             ELSE CONCAT(N'', @gap, N' past-due schedule(s) have an unknown status and are '
                       + N'excluded from overdue. Declare this on the report.') END AS DataQualityNote;

    -- Unmapped (non-NULL) statuses are a DICTIONARY GAP and always raise:
    -- they mean the dictionary is out of date with the system.
    IF ISNULL(@unmapped,0) > 0
        THROW 51003, N'INSIGHTS DICTIONARY GAP - past-due schedules reference status values absent from the dictionary. Update InsightsStatusClassification before computing.', 1;

    /*  RISK-MAPPING SANITY  (was golden invariant G-6 - demoted to a WARNING)

        In PRODUCTION, 98.7% of imprisonment-bearing instances carry RiskType 3,
        measured across 528 tenants - strong corroboration of the locked mapping
        3=Critical. But it is a property of the DATA, not the code: a test
        environment with arbitrary values inverted it completely (96.8% on
        RiskType 0). As a THROWing invariant it turned the whole golden suite
        red forever, so it informs here rather than blocking.                */
    DECLARE @impTot INT, @impR3 INT;
    SELECT @impTot = COUNT(*), @impR3 = SUM(CASE WHEN c.RiskType = 3 THEN 1 ELSE 0 END)
    FROM ComplianceInstance i
    JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID
    JOIN Compliance c      ON c.ID = i.ComplianceID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0
      AND i.IsDeleted = 0 AND c.IsDeleted = 0 AND c.Imprisonment = 1;

    DECLARE @impPct DECIMAL(5,1) = CASE WHEN ISNULL(@impTot,0)=0 THEN NULL
                                        ELSE @impR3*100.0/@impTot END;

    SELECT @CustomerID AS CustomerID,
           @impTot     AS ImprisonmentInstances,
           @impR3      AS ImprisonmentOnRiskType3,
           @impPct     AS ImprisonmentOnRiskType3Pct,
           CASE WHEN ISNULL(@impTot,0) = 0 THEN 'no_imprisonment_data'
                WHEN @impPct >= 90.0 THEN 'consistent_with_production'
                ELSE 'WARNING_risk_mapping_atypical' END AS RiskMappingVerdict,
           CASE WHEN ISNULL(@impTot,0) > 0 AND @impPct < 90.0
                THEN N'Imprisonment-bearing items do not concentrate on RiskType 3 as they do in production (98.7% across 528 tenants). In production this warrants investigation; in a test environment it usually means the data is not representative. Risk-dimension output from this tenant should not be trusted.'
                ELSE NULL END AS RiskMappingNote;

    -- NULL statuses raise only beyond the proportionate threshold.
    IF ISNULL(@nullStatus,0) > @MaxAbsoluteGap OR @pct > @MaxProportionPct
        THROW 51004, N'UNKNOWN-STATUS VOLUME EXCEEDS THRESHOLD - too many past-due schedules have a NULL status to report reliably. Investigate the source data.', 1;
END
GO

PRINT 'Classification Dictionary v1.0 installed.';
GO
