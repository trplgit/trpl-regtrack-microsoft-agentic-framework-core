/*===========================================================================
  RegTrack Insights - Phase 1b (extension)
  LICENCE DIMENSION

  Pattern     : sql/09_dimension_nature.sql (peer-member list, not a tree).
  Emits SIX result sets. Error block 51160-51169.

  -- [FIX] Moved from 51130-51139: that block was already owned by
  -- sql/15_freetier_digest_log.sql, AND this file reused 51131 and 51132 for two
  -- DIFFERENT conditions each - an operator seeing 51132 could not tell whether
  -- to seed a dictionary row or investigate empty master data. Every condition
  -- now has its own code.

  -- WHY THIS PROC EXISTS ----------------------------------------------------
  sql/06's LicencesLapsingNext30 previously read Compliance.ComplianceType = 2.
  Confirmed live against prod (tenant 1403): that is an unrelated 3,423-row
  in-table flag, NOT the licence module. Real licence data lives in
  Lic_tbl_LicenseInstance / Lic_tbl_LicenseStatusTransaction /
  Lic_tbl_LicenseType_Master / Lic_tbl_StatusMaster. This proc is the paid-tier
  dimension over those tables; sql/06 was fixed separately (see that file).

  -- SCHEMA - confirmed live via sys.columns after this file was first drafted
  Confirmed real (column, type, nullable):
    - Lic_tbl_LicenseInstance: ID bigint, CustomerID int, IsDeleted bit NOT NULL,
      CustomerBranchID int, DepartmentID int NULL, LicenseTypeID bigint,
      StartDate/EndDate datetime NULL. IsDeleted=0 now filtered below.
    - Lic_tbl_LicenseStatusTransaction: ID, CustomerID, LicenseID, StatusID,
      StatusChangeOn, IsActive bit NOT NULL, ComplianceScheduleOnID bigint NULL.
      Confirmed live: IsActive is 1 on every row checked (20/20 consecutive rows
      on one licence) - it is NOT a current-vs-superseded flag, does not
      discriminate anything useful here. Latest status stays MAX(StatusChangeOn).
      [DATA QUALITY - found live, not previously known] some licences carry a
      STATUS TRANSACTION ROW EVERY SINGLE DAY (one tenant's LicenseID 1: a row
      for every calendar day back to 2019, all StatusID 3/Expired). This does
      not affect correctness here - MAX(StatusChangeOn) still resolves correctly
      regardless of duplicate volume - but it means this table can be far larger
      than licence count alone suggests. Check row volume before running this
      proc against a large tenant; #latestStatus does a windowed scan of the
      whole table filtered to scoped licences.
    - Lic_tbl_LicenseType_Master: ID bigint, Name varchar, IsDeleted bit NOT NULL,
      Is_Statutory_NoStatutory varchar. IsDeleted now handled below, same
      retired-but-in-use treatment sql/09 gives NatureOfCompliance.
    - Lic_tbl_StatusMaster: ID bigint, StatusName varchar (NOT "Name" - fixed
      below), IsDeleted bit, IsVisibleToUser bit. Confirmed live to have the
      SAME duplicate-name trap as ComplianceStatus (Sec.5). Bucket by ID only -
      see the #licStatus gate below, which refuses to run rather than guess.
    - Lic_tbl_LicenseComplianceInstanceMapping: ID, LicenseID, ComplianceInstanceID,
      IsStatutoryORInternal char. Confirmed real, still not joined - see the
      branch-only-scope note below.

  -- [TRAP] "CONFIRMED" LAPSE VS. A STALE RECORD ------------------------------
  EndDate < today alone is NOT a lapse claim. Tested live: of licences past
  their EndDate, 354 on tenant 1403 still carried a latest status of 'Active' -
  i.e. the record itself has not caught up, not that the licence is fine.
  A lapse is CORROBORATED only when EndDate has passed AND the licence's own
  latest status transaction agrees. The uncorroborated set is real and is
  declared in data_quality, never silently folded into either bucket.

  -- [TRAP] STATUS RESOLVED VIA DICTIONARY, NEVER BY NAME --------------------
  Same mechanism as sql/09's @criticalRisk lookup: InsightsEnumPolarity holds
  the BA-verified ID, and the proc THROWs rather than guess or LIKE-match a
  name known to have duplicates on this exact table. Seed the dictionary row
  (Semantic='LicenceStatus', Meaning='Expired', RawValue=<verified ID>) once
  the ID is confirmed live - that is a data change, not a code change.

  -- SCOPE - THE LICENCE MODULE'S OWN RULE, NOT THE COMPLIANCE SCOPE --------
  [CHANGED 2026-09-28, user decision] The result must equal RegTrack's own
  licence report (SP_LicenseMyReport_V2) for the same user - verified
  licence-for-licence on 14 users. Step 1 = every operating branch of the
  tenant (no longer tvfInsightsScopePairs - see step 1's note).
  Step 2 mirrors RegTrack's own licence module
  (SP_LicenseMyReport_V2), which decides visibility by User.LicenseRoleID -
  NOT by User.RoleID:
    CADMN (Company Admin)      -> every licence in the authorised branches
    MGMT / AUDT                -> only the (BranchID, LicenseTypeID) pairs the
                                  user holds in LIC_EntitiesAssignment
    EXCT (Non-Admin)           -> only licences whose linked compliance
                                  instance is assigned to the user
    anything else / no role    -> THROW 51168 (no licence access)
    MGMT/AUDT/EXCT with nothing in scope -> THROW 51169
  Found live on tenant 1285 / user 11416 (MGMT): the branch-only scope showed
  214 licences where RegTrack's own licence report shows that user 180 - 29
  of the 34 were licence types / branches the user was never assigned.
  [CHANGED 2026-09-28, user decision] The other 5 are dropped too, as RegTrack
  does: a licence only counts when its linked compliance task is live (step 2b)
  - 214 -> 180, identical to RegTrack's report. Declared in data_quality
  'licence_no_live_task'.
  Error codes 51168/51169 sit in the x5-x9 part of this file's block because
  51160 (x0) is already the compliance-scope denial; each condition still has
  its own code.

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_Licence', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_Licence;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Licence
    @UserID      INT,
    @CustomerID  INT,
    @AsOf        DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*-- 0. PRE-FLIGHT --------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51160, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    /*  No usp_Insights_AssertStatusCoverage call here, unlike the other dimension
        procs - this proc never touches ComplianceStatus / vInsightsStatusCurrent,
        so that check would be a spurious coupling to an unrelated system.        */

    /*  Fail closed rather than guess the Expired status id on a table already
        confirmed to have duplicate-name rows. See header. */
    /*  LAPSE ELIGIBILITY - from the dictionary, per BA ruling.

        "Lapsed is only when the due date of the licence has expired.
         Terminated/Rejected licences shouldn't count as lapsed but as
         Terminated or Rejected."

        So EndDate decides, and status only EXCLUDES licences that ended some
        other way. Four buckets are not lapse-eligible: terminated, rejected,
        renewed (EndDate superseded) and not_applicable (never a live
        obligation). The last two are not in the ruling's words but follow from
        its logic - reporting a RENEWED licence as lapsed would be plainly wrong,
        and it affected 64 licences.

        [SUPERSEDED] This replaces a corroboration model that required the
        latest status to AGREE the licence had expired. That under-counted
        badly: it reported 885 lapses where the due-date rule gives 1,554,
        because a licence whose record still says 'Active' or 'Expiring' past
        its EndDate has a stale RECORD, not a valid licence.                  */
    IF OBJECT_ID('tempdb..#licStatus') IS NOT NULL DROP TABLE #licStatus;
    SELECT TRY_CAST(p.RawValue AS INT) AS StatusId,
           p.Meaning                   AS Bucket,
           CAST(CASE WHEN p.Meaning IN (N'terminated', N'rejected',
                                        N'renewed',    N'not_applicable')
                     THEN 0 ELSE 1 END AS BIT) AS LapseEligible
    INTO #licStatus
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'LicenceStatus'
      AND TRY_CAST(p.RawValue AS INT) IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM #licStatus)
        THROW 51165, N'DICTIONARY GAP - no Lic_tbl_StatusMaster ids are classified under Semantic=LicenceStatus in InsightsEnumPolarity. That table has confirmed whitespace-variant names, so a name lookup is unsafe. Seed the verified classification and re-run. Refusing to compute.', 1;

    /*  Fail closed on a status the dictionary has never seen - the same
        unknown-enum rule the ComplianceStatus dictionary applies.            */
    IF EXISTS (SELECT 1 FROM Lic_tbl_StatusMaster sm
               WHERE sm.IsDeleted = 0
                 AND NOT EXISTS (SELECT 1 FROM #licStatus ls WHERE ls.StatusId = sm.ID))
        THROW 51167, N'DICTIONARY GAP - Lic_tbl_StatusMaster contains active statuses absent from the LicenceStatus classification. Refusing to guess whether they are lapse-eligible. Seed them and re-run.', 1;

    /*-- 1. BRANCH SET - [CHANGED 2026-09-28, user decision: match RegTrack's
       own licence report exactly]. Every OPERATING branch of the tenant, as
       SP_LicenseMyReport_V2 uses; the licence role in 1b/2 then narrows it
       (MGMT/AUDT to their own LIC_EntitiesAssignment pairs, EXCT to their own
       tasks). Previously the user's COMPLIANCE branches (tvfInsightsScopePairs)
       were intersected first, which hid licences the licence module itself
       grants: 5 of 12 Management/Auditor users measured saw fewer licences than
       RegTrack shows them (one saw 49 of 404, one was refused outright). Still
       server-derived from the user's own assignment rows - never client input.
       The pre-flight above still requires the user to be an Insights user.  */
    IF OBJECT_ID('tempdb..#branches') IS NOT NULL DROP TABLE #branches;
    SELECT cb.ID AS BranchID
    INTO #branches
    FROM CustomerBranch cb
    WHERE cb.CustomerID = @CustomerID
      AND cb.IsDeleted = 0
      AND cb.Status = 1;

    /*-- 1b. LICENCE ROLE - see header. Same rule RegTrack's licence module
       applies (SP_LicenseMyReport_V2 @IsFlag), read from User.LicenseRoleID. */
    DECLARE @licRole VARCHAR(10) =
        (SELECT r.Code FROM [User] u JOIN Role r ON r.ID = u.LicenseRoleID
         WHERE u.ID = @UserID AND u.IsDeleted = 0);

    IF @licRole IS NULL OR @licRole NOT IN ('CADMN', 'MGMT', 'AUDT', 'EXCT')
        THROW 51168, N'SCOPE DENIED - user has no licence-module role (User.LicenseRoleID is not Company Admin, Management, Auditor or Non-Admin). Refusing to compute.', 1;

    /*-- 2. SCOPED LICENCE BASE -------------------------------------------*/
    IF OBJECT_ID('tempdb..#branchLic') IS NOT NULL DROP TABLE #branchLic;
    SELECT li.ID AS LicenseId, li.CustomerBranchID AS BranchID,
           NULLIF(li.LicenseTypeID, -1) AS LicenseTypeID, li.StartDate, li.EndDate,
           CAST(ISNULL(li.IsPermanantActive, 0) AS BIT) AS IsPerpetual   -- [TRAP] column is misspelled in the schema
    INTO #branchLic
    FROM Lic_tbl_LicenseInstance li
    JOIN #branches b ON b.BranchID = li.CustomerBranchID
    WHERE li.CustomerID = @CustomerID AND li.IsDeleted = 0;

    IF OBJECT_ID('tempdb..#roleLic') IS NOT NULL DROP TABLE #roleLic;
    SELECT bl.LicenseId, bl.BranchID, bl.LicenseTypeID, bl.StartDate, bl.EndDate, bl.IsPerpetual
    INTO #roleLic
    FROM #branchLic bl
    WHERE @licRole = 'CADMN'
       OR (@licRole IN ('MGMT', 'AUDT')
           AND EXISTS (SELECT 1 FROM LIC_EntitiesAssignment ea
                       WHERE ea.UserID = @UserID
                         AND ea.BranchID = bl.BranchID
                         AND ea.LicenseTypeID = bl.LicenseTypeID))
       OR (@licRole = 'EXCT'
           AND EXISTS (SELECT 1 FROM Lic_tbl_LicenseComplianceInstanceScheduleOnMapping m
                       JOIN ComplianceAssignment ca ON ca.ComplianceInstanceID = m.ComplianceInstanceID
                       WHERE m.LicenseID = bl.LicenseId AND ca.UserID = @UserID));

    /*  Outside-role count is declared in data_quality, never silent. */
    DECLARE @outsideRole INT = (SELECT COUNT(*) FROM #branchLic) - (SELECT COUNT(*) FROM #roleLic);

    IF @licRole <> 'CADMN' AND NOT EXISTS (SELECT 1 FROM #roleLic)
        THROW 51169, N'SCOPE DENIED - the user''s licence role grants no licences in their authorised branches (no LIC_EntitiesAssignment pair or assigned licence task). Refusing to compute.', 1;

    /*-- 2b. LIVE COMPLIANCE TASK - [CHANGED 2026-09-28, user decision] ----
       Same rule as RegTrack's own licence report (SP_LicenseMyReport_V2): a
       licence counts only when the compliance schedule its LATEST status points
       at is (a) mapped to the licence, (b) active and not deleted
       (IsActive = 1, IsUpcomingNotDeleted = 1), (c) on a compliance instance
       with an active performer (ComplianceAssignment RoleID 3, user active and
       not deleted, this tenant) and (d) has been acted on at least once (a
       ComplianceTransaction row exists). Licences failing this are NOT counted
       - on tenant 1285 / user 11416 this is 5 licences (185 -> 180, matching
       RegTrack's report exactly) - and the count is declared in data_quality
       'licence_no_live_task', never silent.                                  */
    IF OBJECT_ID('tempdb..#lic') IS NOT NULL DROP TABLE #lic;
    SELECT rl.LicenseId, rl.BranchID, rl.LicenseTypeID, rl.StartDate, rl.EndDate, rl.IsPerpetual
    INTO #lic
    FROM #roleLic rl
    WHERE EXISTS (SELECT 1
                  FROM Lic_tbl_LicenseComplianceInstanceScheduleOnMapping m
                  JOIN ComplianceScheduleOn cso ON cso.ID = m.ComplianceScheduleOnID
                                               AND cso.IsActive = 1
                                               AND cso.IsUpcomingNotDeleted = 1
                  JOIN ComplianceAssignment ca ON ca.ComplianceInstanceID = m.ComplianceInstanceID
                                              AND ca.RoleID = 3
                  JOIN [User] pu ON pu.ID = ca.UserID
                                AND pu.IsDeleted = 0
                                AND pu.IsActive = 1
                                AND pu.CustomerID = @CustomerID
                  WHERE m.LicenseID = rl.LicenseId
                    /*  "Latest" exactly as RegTrack's RecentLicenseTransactionView
                        defines it: the row(s) at MAX(CreatedOn) - NOT StatusChangeOn -
                        with a status in Lic_tbl_StatusMaster, and IsActive = 1 as
                        SP_LicenseMyReport_V2 adds. A licence whose status rows carry
                        no CreatedOn has no latest row there, so RegTrack drops it
                        (found live: 15 on tenant 5).                               */
                    AND m.ComplianceScheduleOnID IN (
                        SELECT lst.ComplianceScheduleOnID
                        FROM Lic_tbl_LicenseStatusTransaction lst
                        JOIN Lic_tbl_StatusMaster lsm ON lsm.ID = lst.StatusID
                        WHERE lst.LicenseID = rl.LicenseId
                          AND lst.IsActive = 1
                          AND lst.CreatedOn = (SELECT MAX(l2.CreatedOn) FROM Lic_tbl_LicenseStatusTransaction l2
                                               WHERE l2.LicenseID = rl.LicenseId))
                    AND EXISTS (SELECT 1 FROM ComplianceTransaction ct
                                WHERE ct.ComplianceScheduleOnID = m.ComplianceScheduleOnID));

    DECLARE @noLiveTask INT = (SELECT COUNT(*) FROM #roleLic) - (SELECT COUNT(*) FROM #lic);

    CREATE CLUSTERED INDEX IX_lic ON #lic (LicenseTypeID, LicenseId);

    /*-- 3. LATEST STATUS PER LICENCE - the corroboration check ----------*/
    IF OBJECT_ID('tempdb..#latestStatus') IS NOT NULL DROP TABLE #latestStatus;
    ;WITH ranked AS (
        SELECT lst.LicenseID, lst.StatusID, lst.ComplianceScheduleOnID,
               ROW_NUMBER() OVER (PARTITION BY lst.LicenseID ORDER BY lst.StatusChangeOn DESC) AS rn
        FROM Lic_tbl_LicenseStatusTransaction lst
        JOIN #lic l ON l.LicenseId = lst.LicenseID
    )
    SELECT LicenseID, StatusID, ComplianceScheduleOnID
    INTO #latestStatus
    FROM ranked WHERE rn = 1;

    /*  Confirmed live: some licences carry a status transaction row for EVERY
        CALENDAR DAY, not just on real status changes - one licence had ~2,500
        rows for a single status. Correctness is unaffected (MAX(StatusChangeOn)
        still resolves right), but this is worth surfacing as a volume fact,
        not just a source comment - see data_quality below.                  */
    DECLARE @scopedTxnRows   INT = (SELECT COUNT(*) FROM Lic_tbl_LicenseStatusTransaction lst
                                    JOIN #lic l ON l.LicenseId = lst.LicenseID);
    DECLARE @scopedLicCount0 INT = (SELECT COUNT(*) FROM #lic);
    DECLARE @avgTxnPerLic    DECIMAL(9,1) = CASE WHEN @scopedLicCount0 = 0 THEN 0
                                                 ELSE 1.0 * @scopedTxnRows / @scopedLicCount0 END;

    /*-- 4. MEMBER LIST = active licence types, plus any RETIRED type that
       still carries licences (same treatment as sql/09 gives a retired
       NatureOfCompliance - never silently drop live data). ---------------*/
    IF OBJECT_ID('tempdb..#type') IS NOT NULL DROP TABLE #type;
    SELECT lt.ID AS LicenseTypeID, lt.Name AS LicenseTypeName,
           CAST(CASE WHEN lt.IsDeleted = 1 THEN 1 ELSE 0 END AS BIT) AS IsRetired
    INTO #type
    FROM Lic_tbl_LicenseType_Master lt
    WHERE lt.IsDeleted = 0
       OR EXISTS (SELECT 1 FROM #lic l WHERE l.LicenseTypeID = lt.ID);

    IF NOT EXISTS (SELECT 1 FROM #type)
        THROW 51166, N'MASTER DATA GAP - Lic_tbl_LicenseType_Master has no active or in-use rows. Refusing to compute a licence dimension.', 1;

    /*-- 5. ROWS -----------------------------------------------------------
       Lapsed : EndDate has passed AND the licence is lapse-eligible - i.e. it
                did NOT end another way. A stale status that has not caught up
                (Active, Expiring) is still lapsed; so is a licence with no
                status row at all, since absence is not exclusion.
       ExcludedTerminalState : EndDate has passed but the licence was terminated,
                rejected, renewed (EndDate superseded) or never applicable. Per
                the BA ruling these are NOT lapses and get their own bucket.
       LapsingNext30 : forward window, mirrors sql/06's DueNext30 windowing.
       [FIX 2026-09-28] A PERPETUAL licence (IsPermanantActive = 1) is ACTIVE and
                never lapses, whatever EndDate holds - RegTrack's own licence
                report blanks EndDate for it. Before this, a perpetual licence
                with no EndDate sat in no bucket (found by the reasoning file on
                1285: Transport 9 + 76 + 11 = 96 of 97). A NON-perpetual licence
                with no EndDate still sits in no bucket - that is missing data,
                declared in data_quality 'licence_no_end_date'.                  */
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        LicenseTypeID          INT            NOT NULL PRIMARY KEY,
        LicenseTypeName        NVARCHAR(300)  NULL,
        IsRetired               BIT            NOT NULL,
        TotalLicences          INT            NOT NULL,
        ActiveLicences         INT            NOT NULL,
        Lapsed     INT            NOT NULL,
        ExcludedTerminalState   INT            NOT NULL,
        LapsingNext30          INT            NOT NULL,
        BranchesCovered        INT            NOT NULL,
        -- derived
        LapsedPct  DECIMAL(5,1)   NULL,
        OverdueRank            INT            NULL,
        Flags                  VARCHAR(200)   NULL
    );

    INSERT #rows (LicenseTypeID, LicenseTypeName, IsRetired, TotalLicences, ActiveLicences,
                  Lapsed, ExcludedTerminalState, LapsingNext30, BranchesCovered)
    SELECT
        t.LicenseTypeID, t.LicenseTypeName, t.IsRetired,
        COUNT(l.LicenseId),
        SUM(CASE WHEN l.IsPerpetual = 1 OR l.EndDate >= @AsOf THEN 1 ELSE 0 END),
        SUM(CASE WHEN l.IsPerpetual = 0 AND l.EndDate < @AsOf AND ISNULL(CAST(c.LapseEligible AS INT), 1) = 1 THEN 1 ELSE 0 END),
        /*  ISNULL(..., 1): a licence with NO status transaction row at all is
            still LAPSED if its EndDate has passed. Absence of a record is not
            evidence that it was terminated or renewed, and the BA ruling makes
            the due date the deciding fact. Without this the reconciliation
            drops them silently - 15 licences on the tenant this was built
            against. The count is declared in data_quality.                   */
        SUM(CASE WHEN l.IsPerpetual = 0 AND l.EndDate < @AsOf AND c.LapseEligible = 0 THEN 1 ELSE 0 END),
        SUM(CASE WHEN l.IsPerpetual = 0 AND l.EndDate > @AsOf AND l.EndDate <= DATEADD(DAY, 30, @AsOf) THEN 1 ELSE 0 END),
        COUNT(DISTINCT l.BranchID)
    FROM #type t
    LEFT JOIN #lic l           ON l.LicenseTypeID = t.LicenseTypeID
    LEFT JOIN #latestStatus ls ON ls.LicenseID = l.LicenseId
    LEFT JOIN #licStatus c    ON c.StatusId = ls.StatusID
    GROUP BY t.LicenseTypeID, t.LicenseTypeName, t.IsRetired;

    /*-- 6. RECONCILIATION, with the untyped bucket counted back ---------
       Confirmed live: LicenseTypeID is NOT NULL on Lic_tbl_LicenseInstance,
       so @untyped is structurally always 0. Kept anyway (same shape as
       sql/09's untagged-nature handling) as a cheap belt-and-suspenders
       check - if this ever fires, the NOT NULL constraint changed underneath
       this proc, which is itself worth knowing about.                    */
    DECLARE @rowSum      INT = (SELECT ISNULL(SUM(TotalLicences),0) FROM #rows);
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #lic);
    DECLARE @untyped     INT = (SELECT COUNT(*) FROM #lic WHERE LicenseTypeID IS NULL);
    DECLARE @orphanType  INT = (SELECT COUNT(*) FROM #lic l
                                WHERE l.LicenseTypeID IS NOT NULL AND l.LicenseTypeID > 0
                                  AND NOT EXISTS (SELECT 1 FROM #type t WHERE t.LicenseTypeID = l.LicenseTypeID));

    IF @orphanType > 0
        THROW 51161, N'LICENCE DIMENSION RECONCILIATION FAILED - a licence carries a LicenseTypeID absent from the type master. This is a referential break, not a tagging gap. Refusing to publish.', 1;

    IF @rowSum + @untyped <> @scopedTotal
        THROW 51162, N'LICENCE DIMENSION RECONCILIATION FAILED - per-type sums plus the untyped bucket do not tie to the scoped licence total. Refusing to publish.', 1;

    DECLARE @hasAnyLicences BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @tenantLapsedPct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0
             ELSE 100.0 * (SELECT ISNULL(SUM(Lapsed),0) FROM #rows) / @scopedTotal END;
    DECLARE @totalExcludedTerminal INT = (SELECT ISNULL(SUM(ExcludedTerminalState),0) FROM #rows);
    DECLARE @perpetual   INT = (SELECT COUNT(*) FROM #lic WHERE IsPerpetual = 1);
    DECLARE @noEndDate   INT = (SELECT COUNT(*) FROM #lic WHERE IsPerpetual = 0 AND EndDate IS NULL);

    UPDATE #rows SET
        LapsedPct = CASE WHEN TotalLicences = 0 THEN 0
                                     ELSE 100.0 * Lapsed / TotalLicences END;

    /*  Materiality floor on ranking - same rule as every other dimension:
        a type with one lapsed licence must not outrank one with hundreds. */
    DECLARE @materialityFloor INT = 50;
    DECLARE @materialMembers  INT = (SELECT COUNT(*) FROM #rows WHERE TotalLicences >= @materialityFloor);
    DECLARE @rankDegraded     BIT = CASE WHEN @materialMembers < 2 THEN 1 ELSE 0 END;
    DECLARE @rankFloor        INT = CASE WHEN @rankDegraded = 1 THEN 1 ELSE @materialityFloor END;

    ;WITH r AS (SELECT LicenseTypeID, RANK() OVER (ORDER BY LapsedPct DESC) AS rk
                FROM #rows WHERE TotalLicences >= @rankFloor)
    UPDATE #rows SET OverdueRank = r.rk FROM #rows JOIN r ON r.LicenseTypeID = #rows.LicenseTypeID;

    /*-- 7. DETECTIONS - peer-relative to this tenant's own distribution -*/
    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyLicences = 1 AND TotalLicences >= @rankFloor
                  AND LapsedPct > @tenantLapsedPct
                 THEN ',high_lapse_rate' ELSE '' END
        , 1, 1, '');

    SELECT
        'control_totals'              AS ResultSet,
        @scopedTotal                  AS ScopedLicences,
        @rowSum                       AS TypedLicences   /* + UntypedLicences = ScopedLicences - see the residual note above */,
        CAST(1 AS BIT)                AS Reconciled,
        @tenantLapsedPct              AS TenantLapsedPct,
        (SELECT COUNT(*) FROM #rows)  AS LicenceTypesReported,
        (SELECT COUNT(*) FROM #rows WHERE TotalLicences > 0) AS LicenceTypesWithLicences,
        @untyped                      AS UntypedLicences,
        @totalExcludedTerminal          AS ExcludedTerminalStateLicences;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY TotalLicences DESC;

    /*-- 8. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @material INT = (SELECT COUNT(*) FROM #rows WHERE TotalLicences >= @rankFloor);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'high_lapse_rate', @material,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%high_lapse_rate%');

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

    /*-- 9. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    INSERT #assert VALUES ('A-TENANT','lapsed_pct',N'tenant',@tenantLapsedPct,NULL,NULL,NULL,NULL,NULL,NULL);

    DECLARE @rankable  INT = (SELECT COUNT(*) FROM #rows WHERE TotalLicences >= @rankFloor);
    DECLARE @tiedAtTop INT = (SELECT COUNT(*) FROM #rows WHERE TotalLicences >= @rankFloor AND OverdueRank = 1);

    IF @rankable >= 2
    INSERT #assert
    SELECT TOP 1 'A-WORST-LICTYPE','lapsed_pct',LicenseTypeName,LapsedPct,OverdueRank,@rankable,
           @tenantLapsedPct, LapsedPct - @tenantLapsedPct,
           CASE WHEN LapsedPct > @tenantLapsedPct THEN 'worse' ELSE 'better' END,
           NULLIF(CONCAT(
               /*  [FIX 2026-09-28] Degraded means FEWER THAN TWO types reach the floor, not none -
                   the old text said "no licence type reaches" while Transport had 97, and the
                   report repeated the contradiction. Now states the real count.              */
               CASE WHEN @rankDegraded = 1 AND @materialMembers = 0
                    THEN CONCAT(N'degraded_ranking_sample: no licence type reaches the ', @materialityFloor,
                                N'-licence materiality floor, so every type is ranked regardless of size. ')
                    WHEN @rankDegraded = 1
                    THEN CONCAT(N'degraded_ranking_sample: only one licence type reaches the ', @materialityFloor,
                                N'-licence materiality floor, too few to rank, so every type is ranked regardless of size. ')
                    ELSE N'' END,
               CASE WHEN @tiedAtTop > 1
                    THEN CONCAT(N'tied_at_top: ', @tiedAtTop, N' licence types share this rate - not uniquely the highest. ')
                    ELSE N'' END), N'')
    FROM #rows WHERE TotalLicences >= @rankFloor
    ORDER BY LapsedPct DESC, TotalLicences DESC;

    IF (SELECT EmitMode FROM #detector WHERE Detector='high_lapse_rate') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-LAPSE-' + CAST(ROW_NUMBER() OVER (ORDER BY Lapsed DESC) AS VARCHAR(5)),
               'lapsed_pct', LicenseTypeName, LapsedPct, NULL, NULL,
               @tenantLapsedPct, LapsedPct - @tenantLapsedPct, 'worse', NULL
        FROM #rows WHERE Flags LIKE '%high_lapse_rate%' ORDER BY Lapsed DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='high_lapse_rate') = 'aggregate'
        INSERT #assert
        SELECT 'A-LAPSE-AGG','licence_types_high_lapse_rate',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - elevated lapse rate is spread across many licence types'
        FROM #detector WHERE Detector='high_lapse_rate';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS ----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-WORST-LICTYPE','high',
           CONCAT(N'', ScopeLabel, N' has the highest lapse rate at ', Value, N'%'),
           'A-WORST-LICTYPE,A-TENANT',
           N'Lapsed = due date passed, per the BA ruling. Status only EXCLUDES licences that ended another way (terminated, rejected, renewed, not applicable). A stale status that has not caught up is still lapsed.'
    FROM #assert WHERE AssertionId = 'A-WORST-LICTYPE' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-LAPSE','high',
           CONCAT(N'', ScopeLabel, N' runs a ', Value, N'% lapse rate, against a tenant rate of ', ComparatorValue, N'%'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-LAPSE-[0-9]%';

    INSERT #find
    SELECT 'F-LAPSE-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' licence types (', VsComparatorPP,
                  N'%) run a lapse rate above the tenant average'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-LAPSE-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY - declared, never silent ------------------------*/
    /*  [ADDED 2026-09-10, handoff] AppliesToMetric binds each declaration to the value it
        constrains, so the narrative layer can look it up instead of inferring it. Some caveats
        exist ONLY here - attached to no assertion and no finding. */
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'licence_module_scope'                 THEN 'ScopedLicences'
                   WHEN 'licence_role_scope'                   THEN 'ScopedLicences'
                   WHEN 'licence_no_live_task'                 THEN 'ScopedLicences'
                   WHEN 'perpetual_licences'                   THEN 'ActiveLicences'
                   WHEN 'licence_no_end_date'                  THEN 'ScopedLicences'
                   WHEN 'excluded_terminal_states'             THEN 'TenantLapsedPct'
                   WHEN 'untyped_licences'                     THEN 'UntypedLicences'
                   WHEN 'licence_type_retired_still_in_use'    THEN 'LicenceTypesReported'
                   WHEN 'licence_status_transaction_volume'    THEN 'TenantLapsedPct'
                   WHEN 'licence_status_classification'        THEN 'TenantLapsedPct'
                   WHEN 'licence_statuses_unclassified'        THEN 'TenantLapsedPct'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'licence_module_scope' AS Issue,
               N'Licences follow the licence module''s own access rules, the same set RegTrack''s own licence report '
             + N'shows this user - not the compliance branch and category scope the other dimensions use.' AS Detail
        UNION ALL
        SELECT 'licence_role_scope',
               CASE WHEN @licRole = 'CADMN'
                    THEN N'Company Admin licence role: every licence at the company''s operating branches is included.'
                    ELSE CONCAT(N'', @outsideRole, N' licence(s) at the company''s operating branches are outside this user''s '
                         + N'licence role (', CASE @licRole WHEN 'MGMT' THEN N'Management' WHEN 'AUDT' THEN N'Auditor'
                                                            WHEN 'EXCT' THEN N'Non-Admin' ELSE @licRole END,
                         N') and are not included - the same rule RegTrack''s own '
                         + N'licence report applies. Counts cover only the licences this user is responsible for.') END
        WHERE @licRole = 'CADMN' OR @outsideRole > 0
        UNION ALL
        SELECT 'licence_no_live_task',
               CONCAT(N'', @noLiveTask, N' licence(s) in this user''s scope are not counted, the same way RegTrack''s own '
                    + N'licence report leaves them out: the linked compliance task is inactive, has no active performer '
                    + N'or has never been acted on, or the licence has no dated latest status record.')
        WHERE @noLiveTask > 0
        UNION ALL
        SELECT 'perpetual_licences',
               CONCAT(N'', @perpetual, N' licence(s) are perpetual (no expiry). They are counted as active and never as lapsed.')
        WHERE @perpetual > 0
        UNION ALL
        SELECT 'licence_no_end_date',
               CONCAT(N'', @noEndDate, N' licence(s) are not perpetual but have no end date recorded, so they are counted in '
                    + N'the total but not as active, lapsed or ended another way.')
        WHERE @noEndDate > 0
        UNION ALL
        SELECT 'excluded_terminal_states',
               CONCAT(N'', @totalExcludedTerminal, N' licence(s) are past EndDate but ended another way - '
                    + N'terminated, rejected, renewed or never applicable. Per the BA ruling these are NOT '
                    + N'lapsed and are reported in their own bucket, not as a shortfall.')
        WHERE @totalExcludedTerminal > 0
        UNION ALL
        SELECT 'untyped_licences',
               CONCAT(N'', @untyped, N' licence(s) in this scope carry no LicenseTypeID and appear in NO row below.')
        WHERE @untyped > 0
        UNION ALL
        SELECT 'licence_type_retired_still_in_use',
               CONCAT(N'', COUNT(*), N' retired licence type(s) still carry licences in this scope. Reported as '
                    + N'members flagged IsRetired rather than dropped, which would lose their licences.')
        FROM #rows WHERE IsRetired = 1 HAVING COUNT(*) > 0
        UNION ALL
        SELECT 'licence_status_transaction_volume',
               CONCAT(N'', @scopedTxnRows, N' status transaction row(s) across ', @scopedLicCount0,
                      N' licence(s) in this scope (', @avgTxnPerLic, N' rows/licence average). Some licences '
                    + N'carry a status row for every calendar day rather than only on real status changes - '
                    + N'this does not affect the corroborated-lapse figures above but is a real volume '
                    + N'characteristic of this table, worth knowing before running against a large tenant.')
        WHERE @avgTxnPerLic > 20.0
        UNION ALL
        SELECT 'licence_status_classification',
               N'All 18 Lic_tbl_StatusMaster ids are classified in the dictionary (Semantic=LicenceStatus). '
             + N'Two read as expiry - ID 3 (Expired) and ID 11 (Validity Expired) - and both are lapse-eligible. '
             + N'Four buckets are NOT lapse-eligible: terminated, rejected, renewed, not_applicable. '
             + N'Bucket by ID only: IDs 13/14 and 15/16 differ solely by a double space and both are in live use.'
        UNION ALL
        SELECT 'licence_statuses_unclassified',
               CONCAT(N'', (SELECT COUNT(*) FROM Lic_tbl_StatusMaster sm
                            WHERE sm.IsDeleted = 0
                              AND sm.ID NOT IN (SELECT StatusId FROM #licStatus)
                              AND sm.ID NOT IN (2,4,5,6,7,9,10)),
                      N' active licence statuses are neither mapped as expiry-equivalent nor '
                    + N'recognised as valid - e.g. Terminated, Terminated_P, Rejected. Whether a '
                    + N'terminated licence counts as a lapse is a BA ruling, not a code decision. '
                    + N'Those licences are currently reported as NOT lapsed.')
        WHERE EXISTS (SELECT 1 FROM Lic_tbl_StatusMaster sm
                      WHERE sm.IsDeleted = 0
                        AND sm.ID NOT IN (SELECT StatusId FROM #licStatus)
                        AND sm.ID NOT IN (2,4,5,6,7,9,10))
    ) q;

    DROP TABLE #branches; DROP TABLE #branchLic; DROP TABLE #roleLic; DROP TABLE #lic; DROP TABLE #latestStatus; DROP TABLE #type;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Licence dimension installed.';
GO
