/*===========================================================================
  RegTrack Insights - Phase 1b (extension)
  LICENCE DIMENSION

  Pattern     : sql/09_dimension_nature.sql (peer-member list, not a tree).
  Emits SIX result sets. Error block 51160-51169.

  -- [FIX] Moved from 51130-51139: that block is owned by sql/15_freetier_
  -- digest_log.sql, which this file collided with outright on THROW 51130.
  -- This file also reused 51131 for two different reconciliation conditions
  -- and 51132 for two different dictionary/master-data conditions - every
  -- condition below now has its own code, per CLAUDE.md Sec.5b.

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
      see the #expiredStatusIds gate below, which refuses to run rather than guess.
      [RESOLVED] the dictionary now confirms StatusID 11 ("Validity Expired") is
      expiry-equivalent to StatusID 3 - both carry Meaning='expired'. The gate reads
      a SET now, not a single id, for exactly this reason.
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

  -- SCOPE - BRANCH-ONLY, NOT FULL 2-D --------------------------------------
  Licences are not linked to ComplianceCategoryId without
  Lic_tbl_LicenseComplianceInstanceMapping, which this proc does not join
  (not yet needed for the fields below). Scope is therefore the DISTINCT
  branch set from tvfInsightsScopePairs, not the full (branch, category) pair.
  A recipient still never sees a licence outside their authorised branches,
  but this is a narrower scope guarantee than every other dimension gives and
  must be called out as such - see data_quality.

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

    /*  Fail closed rather than guess the Expired status id(s) on a table already
        confirmed to have duplicate-name rows. See header.
        [UPDATED - dictionary now richer than when this proc was first written] The
        dictionary has since been enriched (by someone/something else) to classify all
        18 Lic_tbl_StatusMaster ids, and it confirms StatusID 11 ("Validity Expired") IS
        expiry-equivalent to StatusID 3 - the open question this proc originally left
        unresolved. A SET, not a scalar, because that confirmation means there can now
        legitimately be more than one Meaning='expired' row. */
    IF OBJECT_ID('tempdb..#expiredStatusIds') IS NOT NULL DROP TABLE #expiredStatusIds;
    SELECT TRY_CAST(p.RawValue AS INT) AS StatusID
    INTO #expiredStatusIds
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'LicenceStatus' AND p.Meaning = 'expired';

    IF NOT EXISTS (SELECT 1 FROM #expiredStatusIds)
        THROW 51165, N'DICTIONARY GAP - no Lic_tbl_StatusMaster id is mapped to Meaning=expired in InsightsEnumPolarity (Semantic=LicenceStatus). Lic_tbl_StatusMaster has confirmed duplicate-name rows, so a name lookup is unsafe. Seed the verified id(s) and re-run. Refusing to compute.', 1;

    /*-- 1. SCOPED BRANCH SET - see header: branch-only, not full 2-D ----*/
    IF OBJECT_ID('tempdb..#branches') IS NOT NULL DROP TABLE #branches;
    SELECT DISTINCT sp.BranchID
    INTO #branches
    FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp;

    /*-- 2. SCOPED LICENCE BASE -------------------------------------------*/
    IF OBJECT_ID('tempdb..#lic') IS NOT NULL DROP TABLE #lic;
    SELECT li.ID AS LicenseId, li.CustomerBranchID AS BranchID,
           li.LicenseTypeID, li.StartDate, li.EndDate
    INTO #lic
    FROM Lic_tbl_LicenseInstance li
    JOIN #branches b ON b.BranchID = li.CustomerBranchID
    WHERE li.CustomerID = @CustomerID AND li.IsDeleted = 0;

    CREATE CLUSTERED INDEX IX_lic ON #lic (LicenseTypeID, LicenseId);

    /*-- 3. LATEST STATUS PER LICENCE - the corroboration check ----------*/
    IF OBJECT_ID('tempdb..#latestStatus') IS NOT NULL DROP TABLE #latestStatus;
    ;WITH ranked AS (
        SELECT lst.LicenseID, lst.StatusID,
               ROW_NUMBER() OVER (PARTITION BY lst.LicenseID ORDER BY lst.StatusChangeOn DESC) AS rn
        FROM Lic_tbl_LicenseStatusTransaction lst
        JOIN #lic l ON l.LicenseId = lst.LicenseID
    )
    SELECT LicenseID, StatusID
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
       LapsedCorroborated : EndDate has passed AND latest status = Expired.
       LapsedUncorroborated : EndDate has passed but latest status disagrees
                               (stale record - real, declared, never claimed
                               as a confirmed lapse). See header trap note.
       LapsingNext30 : forward window, mirrors sql/06's DueNext30 windowing. */
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        LicenseTypeID          INT            NOT NULL PRIMARY KEY,
        LicenseTypeName        NVARCHAR(300)  NULL,
        IsRetired               BIT            NOT NULL,
        TotalLicences          INT            NOT NULL,
        ActiveLicences         INT            NOT NULL,
        LapsedCorroborated     INT            NOT NULL,
        LapsedUncorroborated   INT            NOT NULL,
        LapsingNext30          INT            NOT NULL,
        BranchesCovered        INT            NOT NULL,
        -- derived
        LapsedCorroboratedPct  DECIMAL(5,1)   NULL,
        OverdueRank            INT            NULL,
        Flags                  VARCHAR(200)   NULL
    );

    INSERT #rows (LicenseTypeID, LicenseTypeName, IsRetired, TotalLicences, ActiveLicences,
                  LapsedCorroborated, LapsedUncorroborated, LapsingNext30, BranchesCovered)
    SELECT
        t.LicenseTypeID, t.LicenseTypeName, t.IsRetired,
        COUNT(l.LicenseId),
        SUM(CASE WHEN l.EndDate >= @AsOf THEN 1 ELSE 0 END),
        -- [BUG FOUND LIVE, matches a documented CLAUDE.md trap] SUM(CASE WHEN ... IN (SELECT ...))
        -- is an aggregate over a subquery - SQL Server rejects it outright ("Cannot perform an
        -- aggregate function on an expression containing an aggregate or a subquery"). Fixed with
        -- a LEFT JOIN to #expiredStatusIds (es) below and a NULL test instead, per the exact fix
        -- CLAUDE.md's own hard-rules list already prescribes for this class of bug.
        SUM(CASE WHEN l.EndDate < @AsOf AND es.StatusID IS NOT NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN l.EndDate < @AsOf AND es.StatusID IS NULL THEN 1 ELSE 0 END),
        SUM(CASE WHEN l.EndDate > @AsOf AND l.EndDate <= DATEADD(DAY, 30, @AsOf) THEN 1 ELSE 0 END),
        COUNT(DISTINCT l.BranchID)
    FROM #type t
    LEFT JOIN #lic l              ON l.LicenseTypeID = t.LicenseTypeID
    LEFT JOIN #latestStatus ls    ON ls.LicenseID = l.LicenseId
    LEFT JOIN #expiredStatusIds es ON es.StatusID = ls.StatusID
    GROUP BY t.LicenseTypeID, t.LicenseTypeName, t.IsRetired;

    /*-- 6. RECONCILIATION, with the untyped bucket counted back ---------
       [FIX, 2026-09-02] This comment used to claim "@untyped is structurally
       always 0" (LicenseTypeID is NOT NULL on Lic_tbl_LicenseInstance, so a
       NULL-only check can never fire) - true of the column, wrong about the
       real data. Confirmed live against UAT tenant 29: 5 licences (branches
       211/214/20482, all dated 2019-2021 - legacy) carry LicenseTypeID = -1,
       a SENTINEL for "no type assigned", not a real Lic_tbl_LicenseType_Master.ID
       (those are always positive, auto-increment). The column being NOT NULL
       just means the source system needed an out-of-band value to express
       "untyped" instead of NULL - the same class of trap as CLAUDE.md's own
       ProductMapping.IsActive/RiskType entries (Sec.5): believe the data, not
       the constraint. Any LicenseTypeID <= 0 is now treated as the untyped
       sentinel (declared honestly via UntypedLicences/the untyped_licences
       data_quality note below, never silently dropped) - a POSITIVE
       LicenseTypeID that still matches no real type row stays a genuine
       referential break (THROW 51161, unchanged) and is never absorbed into
       this bucket, since that really is corrupted data, not a placeholder. */
    DECLARE @rowSum      INT = (SELECT ISNULL(SUM(TotalLicences),0) FROM #rows);
    DECLARE @scopedTotal INT = (SELECT COUNT(*) FROM #lic);
    DECLARE @untyped     INT = (SELECT COUNT(*) FROM #lic WHERE LicenseTypeID IS NULL OR LicenseTypeID <= 0);
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
             ELSE 100.0 * (SELECT ISNULL(SUM(LapsedCorroborated),0) FROM #rows) / @scopedTotal END;
    DECLARE @totalUncorroborated INT = (SELECT ISNULL(SUM(LapsedUncorroborated),0) FROM #rows);

    UPDATE #rows SET
        LapsedCorroboratedPct = CASE WHEN TotalLicences = 0 THEN 0
                                     ELSE 100.0 * LapsedCorroborated / TotalLicences END;

    /*  Materiality floor on ranking - same rule as every other dimension:
        a type with one lapsed licence must not outrank one with hundreds. */
    DECLARE @materialityFloor INT = 50;
    DECLARE @materialMembers  INT = (SELECT COUNT(*) FROM #rows WHERE TotalLicences >= @materialityFloor);
    DECLARE @rankDegraded     BIT = CASE WHEN @materialMembers < 2 THEN 1 ELSE 0 END;
    DECLARE @rankFloor        INT = CASE WHEN @rankDegraded = 1 THEN 1 ELSE @materialityFloor END;

    ;WITH r AS (SELECT LicenseTypeID, RANK() OVER (ORDER BY LapsedCorroboratedPct DESC) AS rk
                FROM #rows WHERE TotalLicences >= @rankFloor)
    UPDATE #rows SET OverdueRank = r.rk FROM #rows JOIN r ON r.LicenseTypeID = #rows.LicenseTypeID;

    /*-- 7. DETECTIONS - peer-relative to this tenant's own distribution -*/
    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyLicences = 1 AND TotalLicences >= @rankFloor
                  AND LapsedCorroboratedPct > @tenantLapsedPct
                 THEN ',high_lapse_rate' ELSE '' END
        , 1, 1, '');

    SELECT
        'control_totals'              AS ResultSet,
        @scopedTotal                  AS ScopedLicences,
        @rowSum                       AS TypedLicences   /* + UntypedLicences = ScopedLicences - see the residual note above */,
        CAST(1 AS BIT)                AS Reconciled,
        @tenantLapsedPct              AS TenantLapsedCorroboratedPct,
        (SELECT COUNT(*) FROM #rows)  AS LicenceTypesReported,
        (SELECT COUNT(*) FROM #rows WHERE TotalLicences > 0) AS LicenceTypesWithLicences,
        @untyped                      AS UntypedLicences,
        @totalUncorroborated          AS UncorroboratedLapsedLicences;

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

    SELECT 'detector_policy' AS ResultSet, * FROM #detector;

    /*-- 9. ASSERTIONS ---------------------------------------------------*/
    IF OBJECT_ID('tempdb..#assert') IS NOT NULL DROP TABLE #assert;
    CREATE TABLE #assert (
        AssertionId VARCHAR(20), Metric VARCHAR(60), ScopeLabel NVARCHAR(500),
        Value DECIMAL(18,2), Rank_ INT NULL, OfN INT NULL,
        ComparatorValue DECIMAL(18,2) NULL, VsComparatorPP DECIMAL(9,2) NULL,
        Direction VARCHAR(10) NULL, Caveat NVARCHAR(500) NULL);

    INSERT #assert VALUES ('A-TENANT','lapsed_corroborated_pct',N'tenant',@tenantLapsedPct,NULL,NULL,NULL,NULL,NULL,NULL);

    DECLARE @rankable  INT = (SELECT COUNT(*) FROM #rows WHERE TotalLicences >= @rankFloor);
    DECLARE @tiedAtTop INT = (SELECT COUNT(*) FROM #rows WHERE TotalLicences >= @rankFloor AND OverdueRank = 1);

    IF @rankable >= 2
    INSERT #assert
    SELECT TOP 1 'A-WORST-LICTYPE','lapsed_corroborated_pct',LicenseTypeName,LapsedCorroboratedPct,OverdueRank,@rankable,
           @tenantLapsedPct, LapsedCorroboratedPct - @tenantLapsedPct,
           CASE WHEN LapsedCorroboratedPct > @tenantLapsedPct THEN 'worse' ELSE 'better' END,
           NULLIF(CONCAT(
               CASE WHEN @rankDegraded = 1
                    THEN CONCAT(N'degraded_ranking_sample: no licence type reaches the ', @materialityFloor,
                                N'-licence materiality floor. ') ELSE N'' END,
               CASE WHEN @tiedAtTop > 1
                    THEN CONCAT(N'tied_at_top: ', @tiedAtTop, N' licence types share this rate - not uniquely the highest. ')
                    ELSE N'' END), N'')
    FROM #rows WHERE TotalLicences >= @rankFloor
    ORDER BY LapsedCorroboratedPct DESC, TotalLicences DESC;

    IF (SELECT EmitMode FROM #detector WHERE Detector='high_lapse_rate') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-LAPSE-' + CAST(ROW_NUMBER() OVER (ORDER BY LapsedCorroborated DESC) AS VARCHAR(5)),
               'lapsed_corroborated_pct', LicenseTypeName, LapsedCorroboratedPct, NULL, NULL,
               @tenantLapsedPct, LapsedCorroboratedPct - @tenantLapsedPct, 'worse', NULL
        FROM #rows WHERE Flags LIKE '%high_lapse_rate%' ORDER BY LapsedCorroborated DESC;
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
           CONCAT(N'', ScopeLabel, N' has the highest confirmed lapse rate at ', Value, N'%'),
           'A-WORST-LICTYPE,A-TENANT',
           N'Corroborated lapses only - EndDate past AND latest status confirms Expired. See UncorroboratedLapsedLicences in control_totals for the stale-record count this excludes.'
    FROM #assert WHERE AssertionId = 'A-WORST-LICTYPE' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-LAPSE','high',
           CONCAT(N'', ScopeLabel, N' runs a ', Value, N'% confirmed lapse rate, against a tenant rate of ', ComparatorValue, N'%'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-LAPSE-[0-9]%';

    INSERT #find
    SELECT 'F-LAPSE-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' licence types (', VsComparatorPP,
                  N'%) run a confirmed lapse rate above the tenant average'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-LAPSE-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY - declared, never silent ------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue, Detail FROM (
        SELECT 'branch_only_scope' AS Issue,
               N'This dimension scopes by authorised BRANCH only, not the full (branch, category) pair every '
             + N'other dimension enforces - licences are not linked to ComplianceCategoryId without '
             + N'Lic_tbl_LicenseComplianceInstanceMapping, which this proc does not yet join.' AS Detail
        UNION ALL
        SELECT 'uncorroborated_lapses',
               CONCAT(N'', @totalUncorroborated, N' licence(s) are past EndDate but their latest recorded status '
                    + N'does NOT confirm Expired (a stale record, not a confirmed lapse). Excluded from every '
                    + N'lapse-rate figure above and never claimed as lapsed.')
        WHERE @totalUncorroborated > 0
        UNION ALL
        SELECT 'untyped_licences',
               CONCAT(N'', @untyped, N' licence(s) in this scope carry no real licence type (LicenseTypeID is '
                    + N'missing or a non-positive sentinel, e.g. -1) and appear in NO row below - counted back '
                    + N'via UntypedLicences in control_totals, never silently dropped.')
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
        SELECT 'licence_status_expired_ids_in_use',
               CONCAT(N'Corroborated lapse this run is decided by ', ids.List,
                      N' (Lic_tbl_StatusMaster ids the dictionary maps to LicenceStatus=expired). '
                    + N'Previously only StatusID 3 was mapped and StatusID 11 (Validity Expired) fell into '
                    + N'LapsedUncorroborated instead - the dictionary has since confirmed the two are '
                    + N'expiry-equivalent.')
        FROM (SELECT STRING_AGG(CAST(StatusID AS VARCHAR(10)), ', ') AS List FROM #expiredStatusIds) ids
    ) q;

    DROP TABLE #expiredStatusIds; DROP TABLE #branches; DROP TABLE #lic; DROP TABLE #latestStatus; DROP TABLE #type;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Licence dimension installed.';
GO
