/*===========================================================================
  RegTrack Insights - Phase 1a, Step 1
  Error block 51000-51009.  Convention: x0 = SCOPE DENIED,
  x1-x4 = RECONCILIATION FAILED, x5-x9 = DICTIONARY / MASTER DATA GAP.
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
/*---------------------------------------------------------------------------
  SEED - DICTIONARY v1.0  (BA-signed-off)

  [TRAP] ATOMIC BY NECESSITY. Each seed is DELETE-then-INSERT, so a failure
  part-way through leaves the dictionary EMPTY rather than unchanged - which is
  strictly worse than not running at all. An empty InsightsEnumPolarity silently
  disables the RiskType mapping, the inverted ProductMapping.IsActive convention,
  CustomerBranch.Status, the licence classification and NodeType.

  This happened for real on 2026-09-04: one Notes value was 457 characters
  against NVARCHAR(400), the INSERT aborted, the DELETE had already committed,
  and the script still printed "installed" because the caller had no -b flag.

  The transaction makes that impossible. XACT_ABORT ON also guarantees a
  rollback on any error, not just the ones TRY/CATCH would see.
---------------------------------------------------------------------------*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo.InsightsDictionaryVersion WHERE VersionId = 1)
        INSERT dbo.InsightsDictionaryVersion (VersionId, VersionLabel, IsCurrent, Notes)
        VALUES (1, '1.0', 1,
                N'Initial BA-signed-off dictionary. Status facets + enum/polarity. '
              + N'Statuses 8 and 19 provisionally mapped, deprecated pending deletion.');

    DELETE FROM dbo.InsightsStatusClassification WHERE VersionId = 1;

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

    DELETE FROM dbo.InsightsEnumPolarity WHERE VersionId = 1;

    INSERT dbo.InsightsEnumPolarity (VersionId, Semantic, RawValue, Meaning, AppliesTo, Notes)
    VALUES
     (1,'RiskType','3',N'Critical',N'Compliance.RiskType',N'Empirically confirmed: 1,419 of 1,424 imprisonment-bearing instances are RiskType 3.'),
     (1,'RiskType','0',N'High',    N'Compliance.RiskType',N'BA-confirmed. NOT intuitive - 0 is High, not Low.'),
     (1,'RiskType','1',N'Medium',  N'Compliance.RiskType',NULL),
     (1,'RiskType','2',N'Low',     N'Compliance.RiskType',N'BA-confirmed. NOT intuitive - 2 is Low, not High.'),
     (1,'ProductMapping.IsActive','0',N'ENABLED (subscription active)', N'ProductMapping',N'INVERTED. Confirmed: core Compliance product has ~1,865 customers mapped with only 7 at IsActive=1 (i.e. 7 disabled).'),
     (1,'ProductMapping.IsActive','1',N'DISABLED (subscription stopped)',N'ProductMapping',N'INVERTED - see above.'),
     /*  NODE TYPE on the branch - the KIND OF LOCATION. Lookup: dbo.NodeType (15
         rows, verified in production). This is the establishment-class attribute
         the peer-coverage dimension uses as its second key, grouped into CLASSES:

           retail       Branch(2), Store(5), Showroom(6)   compared together
           warehouse    Warehouse(4)                         compared among themselves
           plant        Plant(3)                             compared among themselves
           office       Head Office(7), Headquarter(8), Accounts office(19),
                        book of Accounts office(20)          excluded - different regime
           nonphysical  Legal Entity(1), PE(10), PE State(11), PE Location(12),
                        PE Branch(13), Power distribution(9) excluded - not establishments
           unknown      any value absent from NodeType       excluded and DECLARED

         [DATA] Production distribution (IsDeleted=0, Status=1): Branch 51,632 (77%),
         Legal Entity 8,247, PE types 4,746 combined, Plant 1,475, Warehouse 892,
         Head Office 433, Store 261, Showroom 67. "Branch" is the default for almost
         everything; on the reference retailer Branch-typed and Store-typed leaves
         carry identical obligation profiles (75.6 vs 76.0 avg). Treat them as ONE
         class - splitting them would divide identical peers arbitrarily.

         [TRAP] 17 orphan Type values exist in production (23, 24, 27, 30-32, 37, 43,
         45, 46, 52-54, 61, 63, 73, 75) on ~26 branches - not in NodeType. Fail
         closed: class 'unknown', never compared, always declared.                 */
     (1,'CustomerBranch.Type','1', N'nonphysical', N'NodeType',N'Legal Entity (Company). A rollup node, not an establishment.'),
     (1,'CustomerBranch.Type','2', N'retail',      N'NodeType',N'Branch. The generic default - 77% of all branches.'),
     (1,'CustomerBranch.Type','3', N'plant',       N'NodeType',N'Plant. Factory regime (OSH Code 20/40).'),
     (1,'CustomerBranch.Type','4', N'warehouse',   N'NodeType',N'Warehouse.'),
     (1,'CustomerBranch.Type','5', N'retail',      N'NodeType',N'Store. Same class as Branch - identical obligation profile measured.'),
     (1,'CustomerBranch.Type','6', N'retail',      N'NodeType',N'Showroom.'),
     (1,'CustomerBranch.Type','7', N'office',      N'NodeType',N'Head Office. Different obligation profile (16.7 avg labour vs 44 for retail).'),
     (1,'CustomerBranch.Type','8', N'office',      N'NodeType',N'Headquarter.'),
     (1,'CustomerBranch.Type','9', N'nonphysical', N'NodeType',N'Power distribution. 2 branches in production.'),
     (1,'CustomerBranch.Type','10',N'nonphysical', N'NodeType',N'PE - Principal Employer (contract labour context).'),
     (1,'CustomerBranch.Type','11',N'nonphysical', N'NodeType',N'PE State.'),
     (1,'CustomerBranch.Type','12',N'nonphysical', N'NodeType',N'PE Location.'),
     (1,'CustomerBranch.Type','13',N'nonphysical', N'NodeType',N'PE Branch.'),
     (1,'CustomerBranch.Type','19',N'office',      N'NodeType',N'Accounts office.'),
     (1,'CustomerBranch.Type','20',N'office',      N'NodeType',N'book of Accounts office.'),
     /*  COMPANY TYPE on the branch - the LEGAL ENTITY form, NOT the establishment
         or location type. Relevant to the SECRETARIAL category (MCA obligations
         depend on it), irrelevant to labour-law applicability.

         [TRAP] The ID range 1-7 coincides with LocationType.ID, and a join on that
         coincidence produces plausible-looking counts. It is NOT a foreign key -
         nothing references LocationType, not even Act.LocationTypeID. Verified via
         sys.foreign_keys. Do not infer relationships from overlapping ID ranges. */
     (1,'CustomerBranch.ComType','1', N'Public',                          N'CustomerBranch',N'Company type.'),
     (1,'CustomerBranch.ComType','2', N'Private',                         N'CustomerBranch',N'Company type.'),
     (1,'CustomerBranch.ComType','3', N'Listed',                          N'CustomerBranch',N'Company type.'),
     (1,'CustomerBranch.ComType','4', N'Non-Secretarial',                 N'CustomerBranch',N'Company type. No MCA obligations.'),
     (1,'CustomerBranch.ComType','5', N'Limited Liability Partnership',   N'CustomerBranch',N'Company type.'),
     (1,'CustomerBranch.ComType','6', N'Trust',                           N'CustomerBranch',N'Company type.'),
     (1,'CustomerBranch.ComType','7', N'Body Corporate',                  N'CustomerBranch',N'Company type.'),
     (1,'CustomerBranch.ComType','8', N'Proprietor',                      N'CustomerBranch',N'Company type.'),
     (1,'CustomerBranch.ComType','9', N'Firm',                            N'CustomerBranch',N'Company type.'),
     (1,'CustomerBranch.ComType','10',N'OPC',                             N'CustomerBranch',N'One Person Company.'),
     (1,'CustomerBranch.ComType','11',N'Deemed Public Company',           N'CustomerBranch',N'Company type.'),
     (1,'CustomerBranch.ComType','12',N'Public Section 8',                N'CustomerBranch',N'Not-for-profit, public.'),
     (1,'CustomerBranch.ComType','13',N'Private Section 8',               N'CustomerBranch',N'Not-for-profit, private.'),
     (1,'CustomerBranch.ComType','14',N'Partnership Firm',                N'CustomerBranch',N'Company type.'),
     (1,'CustomerBranch.ComType','15',N'Individual',                      N'CustomerBranch',N'Company type.'),
     (1,'CustomerBranch.ComType','16',N'HUF',                             N'CustomerBranch',N'Hindu Undivided Family.'),
     (1,'CustomerBranch.ComType','0', N'UNSET',                           N'CustomerBranch',N'Not populated. 62 of 632 leaf stores on the reference retail tenant.'),
     (1,'CustomerBranch.Status','1',N'ACTIVE - location operating; obligations are live and reported',N'CustomerBranch',N'Every estate query must filter Status=1 as well as IsDeleted=0.'),
     (1,'CustomerBranch.Status','0',N'DEACTIVATED - obligations remain but are FROZEN',N'CustomerBranch',N'BA ruling: location deactivated. Obligations remain tagged but are NOT reported, and no schedules, notifications, alerts or escalations fire. Not live obligations - exclude from every metric and declare the count as data_quality. Measured: 23 of 200 branches on one tenant, 75 of 819 on another.'),
     (1,'IsDeleted','0',N'Active',                        N'User / Customer / CustomerBranch',NULL),
     (1,'IsDeleted','1',N'Inactive (soft-deleted)',       N'User / Customer / CustomerBranch',NULL),
     (1,'User.IsActive','0',N'Deactivated but EXISTS - keep and FLAG, never hide',N'User',N'Distinct from IsDeleted. Filtering to IsActive=1 would hide the "deactivated user still holding live assignments" finding.'),
     (1,'EntityApex','ParentID IS NULL',N'Top-level entity of a tenant (with IsDeleted=0)',N'CustomerBranch',NULL),
     (1,'ScopeSource','EntitiesAssignment',N'2-D scope: (BranchID x ComplianceCatagoryID). Column is misspelled "Catagory".',N'EntitiesAssignment',N'Chosen over ComplianceCategoryMgmtUser: CM is a strict subset (0 exceptions across 2 tenants) that excludes configured-but-dormant sites, which are themselves a key finding.'),
     (1,'CategoryJoin','Act.ComplianceCategoryId',N'The ONLY category join path: ComplianceInstance -> Compliance -> Act',N'Act',N'Compliance, ComplianceInstance and ComplianceSubType have no category column.'),
     /*  LICENCE STATUS CLASSIFICATION - required by sql/21_dimension_licence.sql,
         which THROWs 51165 rather than guess.

         BA RULING: "Lapsed is only when the due date of the licence has expired.
         Terminated/Rejected licences shouldn't count as lapsed but as Terminated
         or Rejected."

         So the due date decides, and status is used only to EXCLUDE licences that
         ended some other way. Applying that literally would have mis-stated two
         groups, both of which follow from the same principle - they did not run
         out either:
           - Renewed (7), 64 licences: renewed, so the EndDate on the record is
             superseded. Reporting a renewed licence as lapsed is plainly wrong.
           - Not Applicable (15,16), 48 licences: never a live obligation.

         LapseEligible = TRUE for every bucket EXCEPT terminated, rejected,
         renewed and not_applicable.

         [TRAP] Bucket by ID. Lic_tbl_StatusMaster carries the same whitespace trap
         as ComplianceStatus - IDs 13/14 'Applied for Assessment' vs
         'Applied for  Assessment', IDs 15/16 'Not Applicable' vs
         'Not  Applicable' - and BOTH of each pair are in live use.

         Measured on live data with this classification (2,319 licences):
           318 no EndDate (unassessable)   214 still valid      54 terminated
            67 rejected                     64 renewed           48 not applicable
         1,554 LAPSED - of which 311 have a renewal in progress and 1,228 show no
         visible action. That split is the actionable part: "1,228 expired with no
         renewal activity" is a far better finding than a single lapse count.      */
     (1,'LicenceStatus','1', N'draft',          N'Lic_tbl_StatusMaster',N'Pre-licence. Lapse-eligible if past EndDate.'),
     (1,'LicenceStatus','2', N'active',         N'Lic_tbl_StatusMaster',N'Status may be stale: 118 licences sit here while past EndDate. Lapse-eligible.'),
     (1,'LicenceStatus','3', N'expired',        N'Lic_tbl_StatusMaster',N'Verified live: exact-unique name. Lapse-eligible.'),
     (1,'LicenceStatus','4', N'active',         N'Lic_tbl_StatusMaster',N'Expiring. 225 sit here while already past EndDate - status has not caught up. Lapse-eligible.'),
     (1,'LicenceStatus','5', N'in_progress',    N'Lic_tbl_StatusMaster',N'Applied. Lapse-eligible: expired is expired, but flag renewal in progress.'),
     (1,'LicenceStatus','6', N'in_progress',    N'Lic_tbl_StatusMaster',N'Applied but Pending For Renewal. Lapse-eligible, renewal in progress.'),
     (1,'LicenceStatus','7', N'renewed',        N'Lic_tbl_StatusMaster',N'NOT lapse-eligible - renewed, so the EndDate on the record is superseded.'),
     (1,'LicenceStatus','8', N'rejected',       N'Lic_tbl_StatusMaster',N'NOT lapse-eligible - BA ruling: report as Rejected.'),
     (1,'LicenceStatus','9', N'in_progress',    N'Lic_tbl_StatusMaster',N'Registered. Lapse-eligible.'),
     (1,'LicenceStatus','10',N'in_progress',    N'Lic_tbl_StatusMaster',N'Registered and Renewal Filed. Lapse-eligible, renewal in progress.'),
     (1,'LicenceStatus','11',N'expired',        N'Lic_tbl_StatusMaster',N'Validity Expired - expiry-equivalent to ID 3. Lapse-eligible.'),
     (1,'LicenceStatus','12',N'terminated',     N'Lic_tbl_StatusMaster',N'NOT lapse-eligible - BA ruling: report as Terminated.'),
     (1,'LicenceStatus','13',N'in_progress',    N'Lic_tbl_StatusMaster',N'Applied for Assessment. Lapse-eligible.'),
     (1,'LicenceStatus','14',N'in_progress',    N'Lic_tbl_StatusMaster',N'Whitespace twin of ID 13 - both in live use. Lapse-eligible.'),
     (1,'LicenceStatus','15',N'not_applicable', N'Lic_tbl_StatusMaster',N'NOT lapse-eligible - never a live obligation.'),
     (1,'LicenceStatus','16',N'not_applicable', N'Lic_tbl_StatusMaster',N'Whitespace twin of ID 15 - both in live use. NOT lapse-eligible.'),
     (1,'LicenceStatus','17',N'terminated',     N'Lic_tbl_StatusMaster',N'Terminated_P. NOT lapse-eligible.'),
     (1,'LicenceStatus','18',N'rejected',       N'Lic_tbl_StatusMaster',N'Application Rejected. NOT lapse-eligible.');

    COMMIT TRANSACTION;
    PRINT 'Dictionary seed committed.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    PRINT 'DICTIONARY SEED FAILED AND WAS ROLLED BACK - the dictionary is unchanged.';
    THROW;   -- re-raise so the caller sees a non-zero result, never a false success
END CATCH;
GO

SET XACT_ABORT OFF;
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
/*  PERFORMANCE - WHY THESE TWO FUNCTIONS DO NOT JOIN RecentComplianceTransactionView

    RecentComplianceTransactionView is a NON-INDEXED view over ComplianceTransaction
    (45.7M rows). Its definition is hidden, but it computes "latest transaction
    per schedule". When a query joins it with a tenant filter three joins away
    (CustomerBranch -> ComplianceInstance -> ComplianceScheduleOn -> view), the
    optimizer frequently cannot push the filter through, so the view is computed
    for ALL 45.7M rows before the tenant is applied.

    Measured in production, tenant 1490 (52,554 schedules):
        via the view      : > 4 minutes, hung the calling tool
        explicit pattern  : 611 ms total (517 scope + 94 latest-status)
    Verified identical results on 400 schedules with 3+ transactions each.

    The explicit pattern: scope schedules to the tenant FIRST, then resolve the
    latest status per schedule with a TOP 1 seek on IX_CT_CSO_Dated_ID
    (ComplianceScheduleOnID, Dated, ID) - the tiebreak order the view uses.

    tvfInsightsLatestStatus is the single place this happens. Nothing else in
    the Insights schema may join RecentComplianceTransactionView directly.       */
IF OBJECT_ID('dbo.tvfInsightsLatestStatus', 'IF') IS NOT NULL
    DROP FUNCTION dbo.tvfInsightsLatestStatus;
GO
CREATE FUNCTION dbo.tvfInsightsLatestStatus (@CustomerID INT, @AsOf DATETIME)
RETURNS TABLE
AS
RETURN
(
    /*  Every active, not-deleted schedule of the tenant's OPERATING branches,
        with its latest status. PAST-DUE ONLY (ScheduleOn <= @AsOf) - the
        forward window is a different population served by the forward-pipeline
        function.

        [FOUND IN PRODUCTION] Some past-due schedules have NO transaction row at
        all. The old view's inner join hid them; this function surfaces them
        with StatusId = NULL AND LatestTransactionId = NULL. On one tenant all
        22 shared a single timestamp two years old - a bulk-creation artifact.
        They are NOT the same as a transaction whose status is NULL. Use
        LatestTransactionId to tell the two apart. BA RULING: they COUNT as
        overdue - see tvfInsightsOverdueSchedules.                            */
    SELECT
        cso.ID                      AS ComplianceScheduleOnID,
        i.ID                        AS ComplianceInstanceID,
        i.CustomerBranchID,
        i.ComplianceID,
        cso.ScheduleOn,
        lt.StatusId,
        lt.ID                       AS LatestTransactionId    -- NULL = no transaction EVER
    FROM ComplianceScheduleOn cso
    JOIN ComplianceInstance i  ON i.ID  = cso.ComplianceInstanceID AND i.IsDeleted = 0
    JOIN CustomerBranch     cb ON cb.ID = i.CustomerBranchID
                              AND cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1
    JOIN Compliance         c  ON c.ID  = i.ComplianceID AND c.IsDeleted = 0   -- the ESTATE definition, Sec.1.1
    OUTER APPLY (SELECT TOP 1 t.StatusId, t.ID
                 FROM ComplianceTransaction t
                 WHERE t.ComplianceScheduleOnID = cso.ID
                 ORDER BY t.Dated DESC, t.ID DESC) lt
    WHERE cso.IsActive = 1
      AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn <= @AsOf
);
GO

/*  OWNERSHIP - RegTrack assigns a performer by TWO INDEPENDENT MECHANISMS and
    any analysis that reads only one is wrong.

      1. ComplianceAssignment (RoleID = 3)   - attached to the OBLIGATION
      2. ComplianceScheduleOn.Performerid    - attached to each OCCURRENCE,
                                               populated on 99.8% of schedules

    [DEFECT FOUND 2026-09-05] Seven dimensions read only mechanism 1 and reported
    the difference as "ownerless". On one production tenant that overstated the
    figure by 181x: 44,480 reported against 245 actually unowned.

    The two-way split is genuinely predictive and must be KEPT - only the label
    was wrong:

        instance_assigned   187,872 instances   16.3% overdue
        schedule_only        34,171 instances   64.9% overdue
        unowned                 245 instances   83.3% overdue
        no_schedules         10,064 instances    0.0% overdue (nothing to be late for)

    So: emit NoInstanceOwner (the predictive metric) and NoOwnerAnywhere (the
    absolute failure). NEVER emit a bare "Ownerless" - it means neither.

    [PERF] Materialise this into an indexed temp table in every caller. Do not
    join it to another inline TVF directly - see CLAUDE.md Sec.5.               */
IF OBJECT_ID('dbo.tvfInsightsOwnership', 'IF') IS NOT NULL
    DROP FUNCTION dbo.tvfInsightsOwnership;
GO
CREATE FUNCTION dbo.tvfInsightsOwnership (@UserID INT, @CustomerID INT)
RETURNS TABLE
AS
RETURN
(
    SELECT
        s.ComplianceInstanceID,
        s.BranchID,
        CAST(CASE WHEN ca.ComplianceInstanceID IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasInstanceOwner,
        CAST(CASE WHEN x.SchedulesWithPerformer > 0        THEN 1 ELSE 0 END AS BIT) AS HasScheduleOwner,
        CAST(CASE WHEN x.ScheduleCount = 0                 THEN 1 ELSE 0 END AS BIT) AS HasNoSchedules,
        CAST(CASE WHEN ca.ComplianceInstanceID IS NULL     THEN 1 ELSE 0 END AS BIT) AS NoInstanceOwner,
        CAST(CASE WHEN ca.ComplianceInstanceID IS NULL
                   AND x.ScheduleCount > 0
                   AND x.SchedulesWithPerformer = 0        THEN 1 ELSE 0 END AS BIT) AS NoOwnerAnywhere,
        CASE WHEN ca.ComplianceInstanceID IS NOT NULL THEN 'instance_assigned'
             WHEN x.ScheduleCount = 0                 THEN 'no_schedules'
             WHEN x.SchedulesWithPerformer > 0        THEN 'schedule_only'
             ELSE 'unowned' END                                                      AS OwnerClass
    FROM dbo.tvfInsightsScopedInstances(@UserID, @CustomerID) s
    LEFT JOIN (SELECT DISTINCT ComplianceInstanceID
               FROM ComplianceAssignment
               WHERE RoleID = 3 AND UserID > 0) ca
           ON ca.ComplianceInstanceID = s.ComplianceInstanceID
    CROSS APPLY (SELECT COUNT(*) AS ScheduleCount,
                        SUM(CASE WHEN cso.Performerid IS NOT NULL AND cso.Performerid <> 0
                                 THEN 1 ELSE 0 END) AS SchedulesWithPerformer
                 FROM ComplianceScheduleOn cso
                 WHERE cso.ComplianceInstanceID = s.ComplianceInstanceID) x
);
GO

IF OBJECT_ID('dbo.tvfInsightsOverdueSchedules', 'IF') IS NOT NULL
    DROP FUNCTION dbo.tvfInsightsOverdueSchedules;
GO
CREATE FUNCTION dbo.tvfInsightsOverdueSchedules (@CustomerID INT, @AsOf DATETIME)
RETURNS TABLE
AS
RETURN
(
    /*  The canonical overdue predicate. Built on tvfInsightsLatestStatus - see
        the note above for why it no longer touches the view.

        A past-due schedule is overdue when EITHER:
          (a) its latest status is overdue-eligible per the dictionary, OR
          (b) it has NO transaction at all - BA RULING: "a past-due schedule
              with no transaction is to be considered overdue". Nobody has
              ever touched it; that is the purest form of overdue.

        Still FAIL-CLOSED on unknown: a schedule whose latest status is NOT in
        the dictionary is neither (a) nor (b) and is excluded, so a dictionary
        gap still surfaces through reconciliation rather than being guessed. */
    SELECT
        ls.ComplianceInstanceID,
        ls.ComplianceScheduleOnID,
        ls.CustomerBranchID,
        a.ComplianceCategoryId      AS CategoryId,
        ls.StatusId,                                     -- NULL for case (b)
        ls.ScheduleOn,
        CAST(CASE WHEN ls.LatestTransactionId IS NULL THEN 1 ELSE 0 END AS BIT) AS NeverTouched
    FROM dbo.tvfInsightsLatestStatus(@CustomerID, @AsOf) ls
    JOIN Compliance c ON c.ID = ls.ComplianceID          -- IsDeleted already applied upstream
    JOIN Act        a ON a.ID = c.ActID
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = ls.StatusId
    WHERE d.OverdueEligible = 1
       OR ls.LatestTransactionId IS NULL
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

    /*  [PERF] via tvfInsightsLatestStatus, never the 45.7M-row view - see above. */
    DECLARE @noTransaction INT;
    SELECT @pastdue       = COUNT(*),
           @noTransaction = SUM(CASE WHEN ls.LatestTransactionId IS NULL THEN 1 ELSE 0 END),
           @nullStatus    = SUM(CASE WHEN ls.LatestTransactionId IS NOT NULL AND ls.StatusId IS NULL THEN 1 ELSE 0 END),
           @unmapped      = SUM(CASE WHEN ls.StatusId IS NOT NULL AND d.StatusId IS NULL THEN 1 ELSE 0 END)
    FROM dbo.tvfInsightsLatestStatus(@CustomerID, GETDATE()) ls
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = ls.StatusId;

    DECLARE @gap INT = ISNULL(@nullStatus,0) + ISNULL(@unmapped,0);
    DECLARE @pct DECIMAL(9,4) = CASE WHEN ISNULL(@pastdue,0) = 0 THEN 0
                                     ELSE @gap * 100.0 / @pastdue END;

    SELECT
        @CustomerID AS CustomerID,
        @pastdue    AS PastDueSchedules,
        @noTransaction AS SchedulesWithNoTransaction,   -- past due, never touched; COUNTED overdue per BA ruling
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
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND cb.Status = 1
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
