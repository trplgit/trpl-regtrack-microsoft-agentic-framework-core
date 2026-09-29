/*===========================================================================
  RegTrack Insights - v2 (2026-09-29): LICENCE DIMENSION, RegTrack status labels,
  scoped to the report period the user picked.
  [25, 2026-09-29] Supersedes sql/v2/24. Numbers, filters and result-set shape
  are IDENTICAL to 24; only the reader-facing text changed (data_quality
  details and the finding guard), after user review of a real report:
  the report must not say "RegTrack shows ..." / "RegTrack's own licence
  report ...", and must not explain how a status is derived ("not worked out
  from the end date"). Operator-facing THROW messages are unchanged.
  24 superseded sql/v2/23 (same procedure name; 23 stays in the repo as the
  unwindowed version). sql/v2/99_rollback_v2.sql restores the pre-v2 sql/21.
  Error block 51160-51169. Emits SIX result sets (same order and names as v1).

  -- WHAT CHANGED FROM sql/v2/23 --------------------------------------------
  Product decision (2026-09-29): the Licence report follows the period the
  user selects (last 30/60/90 days, Q1-Q4), like every other dimension.
  Copied from RegTrack's own licence report (SP_LicenseMyReport_V2) date
  filter: a licence is in the report when its END DATE falls in the period -
      LI.EndDate >= @dtStart AND LI.EndDate <= @dtEnd
  on the raw EndDate, so a licence with no end date (perpetual) drops out, as
  RegTrack's own comment says. @WindowEnd arrives exclusive (as every other
  dimension proc takes it); @dtEnd = the last day at 00:00, see step 2c.
  @WindowStart/@WindowEnd NULL = no filter (every licence, as sql/v2/23) -
  kept for callers that have no period (runtime harness, older tests).
  Every row, total, ranking and finding covers the windowed licences only.
  Three context totals cover ALL licences in the user's licence scope,
  unfiltered, so the report can say "across all N licences: A Active,
  E Expired" - AllLicences / AllActiveLicences / AllExpiredLicences.
  A period with zero licences is a legitimate empty report, not an error.
  THROW 51180 = incomplete period (51160-51169 is full; 51180 taken from the
  free list, see CLAUDE.md Sec.5b).

  -- WHAT CHANGED FROM v1 (carried over from sql/v2/23) ----------------------
  Product decision (2026-09-29, raised by the head of testing on tenant 1285):
  Transport showed 9 Active in Insights, RegTrack's own licence report
  (SP_LicenseMyReport_V2, the Excel testers use) shows 5. v1 decided
  Active / Lapsed from EndDate ("BA ruling" that was never actually given);
  RegTrack shows the licence's latest STATUS. v2 copies RegTrack:

    v1 (EndDate)                          v2 (RegTrack Status column)
    Active  = EndDate >= today/perpetual  ActiveLicences = label Active
    Lapsed  = EndDate passed              Expired        = label Expired
    ExcludedTerminalState                 one column per RegTrack label:
                                          Expiring, Applied, PendingForReview,
                                          Rejected, ApplicationRejected,
                                          Terminated, NotApplicable,
                                          OtherStatus (Draft, Registered,
                                          Registered & Renewal Filed,
                                          Validity Expired - 17 licences
                                          across all tenants on 2026-09-29)
    LapsingNext30                         EndingNext30 (unchanged rule: not
                                          perpetual, EndDate in the next 30
                                          days - RegTrack's own expiry-window
                                          filter is also date-based)

  The label per status id comes from the dictionary (Semantic
  'LicenceReportStatus', sql/v2/22) - never from a name match: the names
  have whitespace duplicates. "Latest status" is RegTrack's own
  (RecentLicenseTransactionView): the row at MAX(CreatedOn), IsActive = 1.
  The status columns sum to TotalLicences on every row, or the proc THROWs
  (51163).

  Scope (licence role, live compliance task) is unchanged from v1 - see
  sql/21's header; it already equals RegTrack licence-for-licence.

  IDEMPOTENT. ASCII only. Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.usp_Insights_Dimension_Licence', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_Dimension_Licence;
GO
CREATE PROCEDURE dbo.usp_Insights_Dimension_Licence
    @UserID      INT,
    @CustomerID  INT,
    @AsOf        DATETIME = NULL,
    @WindowStart DATETIME = NULL,
    @WindowEnd   DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    /*  Both window ends or neither - half a window is a caller bug, not a filter. */
    IF (@WindowStart IS NULL AND @WindowEnd IS NOT NULL) OR (@WindowStart IS NOT NULL AND @WindowEnd IS NULL)
       OR (@WindowStart IS NOT NULL AND @WindowEnd <= @WindowStart)
        THROW 51180, N'LICENCE DIMENSION - the report period is incomplete or empty (WindowStart/WindowEnd must both be set, start before end). Refusing to compute.', 1;

    /*-- 0. PRE-FLIGHT --------------------------------------------------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51160, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    /*  RegTrack status label per Lic_tbl_StatusMaster id - dictionary only. */
    IF OBJECT_ID('tempdb..#label') IS NOT NULL DROP TABLE #label;
    SELECT TRY_CAST(p.RawValue AS INT) AS StatusId, p.Meaning AS Label
    INTO #label
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'LicenceReportStatus'
      AND TRY_CAST(p.RawValue AS INT) IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM #label)
        THROW 51165, N'DICTIONARY GAP - no Lic_tbl_StatusMaster ids carry a RegTrack label (Semantic=LicenceReportStatus) in the current dictionary. Install sql/v2/22 and re-run. Refusing to compute.', 1;

    /*  Every status id - deleted ones too (13/14 are deleted but still the
        latest status of live licences) - must have a label.                  */
    IF EXISTS (SELECT 1 FROM Lic_tbl_StatusMaster sm
               WHERE NOT EXISTS (SELECT 1 FROM #label l WHERE l.StatusId = sm.ID))
        THROW 51167, N'DICTIONARY GAP - Lic_tbl_StatusMaster contains a status with no RegTrack label (Semantic=LicenceReportStatus). Refusing to guess how RegTrack shows it. Seed it and re-run.', 1;

    /*-- 1. BRANCH SET - every operating branch (as SP_LicenseMyReport_V2) -*/
    IF OBJECT_ID('tempdb..#branches') IS NOT NULL DROP TABLE #branches;
    SELECT cb.ID AS BranchID
    INTO #branches
    FROM CustomerBranch cb
    WHERE cb.CustomerID = @CustomerID
      AND cb.IsDeleted = 0
      AND cb.Status = 1;

    /*-- 1b. LICENCE ROLE (User.LicenseRoleID, as SP_LicenseMyReport_V2) ---*/
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

    DECLARE @outsideRole INT = (SELECT COUNT(*) FROM #branchLic) - (SELECT COUNT(*) FROM #roleLic);

    IF @licRole <> 'CADMN' AND NOT EXISTS (SELECT 1 FROM #roleLic)
        THROW 51169, N'SCOPE DENIED - the user''s licence role grants no licences in their authorised branches (no LIC_EntitiesAssignment pair or assigned licence task). Refusing to compute.', 1;

    /*-- 2b. LIVE COMPLIANCE TASK - unchanged from v1 (see sql/21) ----------*/
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

    /*-- 2c. REPORT PERIOD - RegTrack's own end-date filter (see header) ---
       #allLic keeps every licence in scope for the context totals; #lic is
       narrowed to the period and drives everything else.                    */
    IF OBJECT_ID('tempdb..#allLic') IS NOT NULL DROP TABLE #allLic;
    SELECT LicenseId, LicenseTypeID INTO #allLic FROM #lic;

    DECLARE @outsideWindow INT = 0;
    IF @WindowStart IS NOT NULL
    BEGIN
        /*  [TRAP] Spelled out, NOT "WHERE NOT (EndDate >= ... AND EndDate < ...)": with a NULL
            EndDate that NOT(...) is UNKNOWN, not TRUE, so no-end-date licences were never deleted
            and sat in every period (found by the parity run, 2026-09-29).                   */
        /*  Exact copy of RegTrack's inclusive end bound: "EndDate <= @dtEnd" where @dtEnd is the
            LAST DAY at 00:00 (dd-mm-yyyy, no time). A licence ending on the last day with a time
            of day (e.g. 31 Mar 17:20 - 55 such licences on UAT) is therefore OUTSIDE the period in
            RegTrack, and so here too. @WindowEnd is exclusive midnight (ReportPeriodResolver), so
            the last day at 00:00 is @WindowEnd - 1 day.                                        */
        DECLARE @lastDay DATETIME = DATEADD(DAY, -1, CAST(CAST(@WindowEnd AS DATE) AS DATETIME));
        DELETE FROM #lic
        WHERE EndDate IS NULL OR EndDate < @WindowStart OR EndDate > @lastDay;
        SET @outsideWindow = (SELECT COUNT(*) FROM #allLic) - (SELECT COUNT(*) FROM #lic);
    END

    /*-- 3. LATEST STATUS = RegTrack's (RecentLicenseTransactionView) -------
       The row at MAX(CreatedOn) - NOT StatusChangeOn - with IsActive = 1, as
       SP_LicenseMyReport_V2 reads it. 0 licences had two different statuses
       at the same MAX(CreatedOn) on 2026-09-29; ID DESC breaks a tie anyway
       so the result is deterministic.                                        */
    IF OBJECT_ID('tempdb..#latestStatus') IS NOT NULL DROP TABLE #latestStatus;
    ;WITH mx AS (
        SELECT lst.LicenseID, MAX(lst.CreatedOn) AS Dated
        FROM Lic_tbl_LicenseStatusTransaction lst
        JOIN #allLic l ON l.LicenseId = lst.LicenseID
        GROUP BY lst.LicenseID
    ), ranked AS (
        SELECT lst.LicenseID, lst.StatusID,
               ROW_NUMBER() OVER (PARTITION BY lst.LicenseID ORDER BY lst.ID DESC) AS rn
        FROM Lic_tbl_LicenseStatusTransaction lst
        JOIN mx ON mx.LicenseID = lst.LicenseID AND mx.Dated = lst.CreatedOn
        WHERE lst.IsActive = 1
    )
    SELECT r.LicenseID, r.StatusID, lb.Label
    INTO #latestStatus
    FROM ranked r
    LEFT JOIN #label lb ON lb.StatusId = r.StatusID
    WHERE r.rn = 1;

    DECLARE @scopedTxnRows   INT = (SELECT COUNT(*) FROM Lic_tbl_LicenseStatusTransaction lst
                                    JOIN #lic l ON l.LicenseId = lst.LicenseID);
    DECLARE @scopedLicCount0 INT = (SELECT COUNT(*) FROM #lic);
    DECLARE @avgTxnPerLic    DECIMAL(9,1) = CASE WHEN @scopedLicCount0 = 0 THEN 0
                                                 ELSE 1.0 * @scopedTxnRows / @scopedLicCount0 END;

    /*  Every counted licence must have a labelled latest status - the live-task
        rule above already requires a latest status row, so a miss here is a
        code defect, not data.                                                 */
    IF EXISTS (SELECT 1 FROM #allLic l
               WHERE NOT EXISTS (SELECT 1 FROM #latestStatus ls
                                 WHERE ls.LicenseID = l.LicenseId AND ls.Label IS NOT NULL))
        THROW 51164, N'LICENCE DIMENSION RECONCILIATION FAILED - a counted licence has no labelled latest status. Refusing to publish.', 1;

    /*-- 4. MEMBER LIST = active licence types, plus retired types in use --*/
    IF OBJECT_ID('tempdb..#type') IS NOT NULL DROP TABLE #type;
    SELECT lt.ID AS LicenseTypeID, lt.Name AS LicenseTypeName,
           CAST(CASE WHEN lt.IsDeleted = 1 THEN 1 ELSE 0 END AS BIT) AS IsRetired
    INTO #type
    FROM Lic_tbl_LicenseType_Master lt
    WHERE lt.IsDeleted = 0
       OR EXISTS (SELECT 1 FROM #lic l WHERE l.LicenseTypeID = lt.ID);

    IF NOT EXISTS (SELECT 1 FROM #type)
        THROW 51166, N'MASTER DATA GAP - Lic_tbl_LicenseType_Master has no active or in-use rows. Refusing to compute a licence dimension.', 1;

    /*-- 5. ROWS - one column per RegTrack Status label -------------------*/
    IF OBJECT_ID('tempdb..#rows') IS NOT NULL DROP TABLE #rows;
    CREATE TABLE #rows (
        LicenseTypeID          INT            NOT NULL PRIMARY KEY,
        LicenseTypeName        NVARCHAR(300)  NULL,
        IsRetired              BIT            NOT NULL,
        TotalLicences          INT            NOT NULL,
        ActiveLicences         INT            NOT NULL,
        Expiring               INT            NOT NULL,
        Expired                INT            NOT NULL,
        Applied                INT            NOT NULL,
        PendingForReview       INT            NOT NULL,
        Rejected               INT            NOT NULL,
        ApplicationRejected    INT            NOT NULL,
        Terminated             INT            NOT NULL,
        NotApplicable          INT            NOT NULL,
        OtherStatus            INT            NOT NULL,
        EndingNext30           INT            NOT NULL,
        BranchesCovered        INT            NOT NULL,
        -- derived
        ExpiredPct             DECIMAL(5,1)   NULL,
        OverdueRank            INT            NULL,
        Flags                  VARCHAR(200)   NULL
    );

    INSERT #rows (LicenseTypeID, LicenseTypeName, IsRetired, TotalLicences, ActiveLicences, Expiring, Expired,
                  Applied, PendingForReview, Rejected, ApplicationRejected, Terminated, NotApplicable, OtherStatus,
                  EndingNext30, BranchesCovered)
    SELECT
        t.LicenseTypeID, t.LicenseTypeName, t.IsRetired,
        COUNT(l.LicenseId),
        SUM(CASE WHEN ls.Label = N'Active'               THEN 1 ELSE 0 END),
        SUM(CASE WHEN ls.Label = N'Expiring'             THEN 1 ELSE 0 END),
        SUM(CASE WHEN ls.Label = N'Expired'              THEN 1 ELSE 0 END),
        SUM(CASE WHEN ls.Label = N'Applied'              THEN 1 ELSE 0 END),
        SUM(CASE WHEN ls.Label = N'PendingForReview'     THEN 1 ELSE 0 END),
        SUM(CASE WHEN ls.Label = N'Rejected'             THEN 1 ELSE 0 END),
        SUM(CASE WHEN ls.Label = N'Application Rejected' THEN 1 ELSE 0 END),
        SUM(CASE WHEN ls.Label = N'Terminated'           THEN 1 ELSE 0 END),
        SUM(CASE WHEN ls.Label = N'Not Applicable'       THEN 1 ELSE 0 END),
        SUM(CASE WHEN ls.Label IN (N'Draft', N'Registered', N'Registered & Renewal Filed', N'Validity Expired')
                 THEN 1 ELSE 0 END),
        SUM(CASE WHEN l.IsPerpetual = 0 AND l.EndDate > @AsOf AND l.EndDate <= DATEADD(DAY, 30, @AsOf) THEN 1 ELSE 0 END),
        COUNT(DISTINCT l.BranchID)
    FROM #type t
    LEFT JOIN #lic l           ON l.LicenseTypeID = t.LicenseTypeID
    LEFT JOIN #latestStatus ls ON ls.LicenseID = l.LicenseId
    GROUP BY t.LicenseTypeID, t.LicenseTypeName, t.IsRetired;

    /*-- 6. RECONCILIATION ------------------------------------------------*/
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

    /*  The status columns must partition each row exactly - a label added to
        the dictionary without a column here would otherwise vanish.          */
    IF EXISTS (SELECT 1 FROM #rows
               WHERE ActiveLicences + Expiring + Expired + Applied + PendingForReview + Rejected
                     + ApplicationRejected + Terminated + NotApplicable + OtherStatus <> TotalLicences)
        THROW 51163, N'LICENCE DIMENSION RECONCILIATION FAILED - the RegTrack status columns do not sum to TotalLicences on every row (a status label has no column). Refusing to publish.', 1;

    DECLARE @hasAnyLicences BIT = CASE WHEN @scopedTotal > 0 THEN 1 ELSE 0 END;
    DECLARE @tenantActive   INT = (SELECT ISNULL(SUM(ActiveLicences),0) FROM #rows);
    DECLARE @tenantExpired  INT = (SELECT ISNULL(SUM(Expired),0) FROM #rows);
    DECLARE @tenantExpiredPct DECIMAL(5,1) =
        CASE WHEN @scopedTotal = 0 THEN 0 ELSE 100.0 * @tenantExpired / @scopedTotal END;
    DECLARE @perpetual   INT = (SELECT COUNT(*) FROM #lic WHERE IsPerpetual = 1);
    DECLARE @allLicences INT = (SELECT COUNT(*) FROM #allLic);
    DECLARE @allActive   INT = (SELECT COUNT(*) FROM #allLic a JOIN #latestStatus ls ON ls.LicenseID = a.LicenseId
                                WHERE a.LicenseTypeID IS NOT NULL AND ls.Label = N'Active');
    DECLARE @allExpired  INT = (SELECT COUNT(*) FROM #allLic a JOIN #latestStatus ls ON ls.LicenseID = a.LicenseId
                                WHERE a.LicenseTypeID IS NOT NULL AND ls.Label = N'Expired');
    DECLARE @otherTotal  INT = (SELECT ISNULL(SUM(OtherStatus),0) FROM #rows);

    UPDATE #rows SET
        ExpiredPct = CASE WHEN TotalLicences = 0 THEN 0 ELSE 100.0 * Expired / TotalLicences END;

    /*  Materiality floor on ranking - same rule as every other dimension. */
    DECLARE @materialityFloor INT = 50;
    DECLARE @materialMembers  INT = (SELECT COUNT(*) FROM #rows WHERE TotalLicences >= @materialityFloor);
    DECLARE @rankDegraded     BIT = CASE WHEN @materialMembers < 2 THEN 1 ELSE 0 END;
    DECLARE @rankFloor        INT = CASE WHEN @rankDegraded = 1 THEN 1 ELSE @materialityFloor END;

    ;WITH r AS (SELECT LicenseTypeID, RANK() OVER (ORDER BY ExpiredPct DESC) AS rk
                FROM #rows WHERE TotalLicences >= @rankFloor)
    UPDATE #rows SET OverdueRank = r.rk FROM #rows JOIN r ON r.LicenseTypeID = #rows.LicenseTypeID;

    /*-- 7. DETECTIONS - peer-relative to this tenant's own distribution -*/
    UPDATE #rows SET Flags =
        STUFF(
            CASE WHEN @hasAnyLicences = 1 AND TotalLicences >= @rankFloor
                  AND ExpiredPct > @tenantExpiredPct
                 THEN ',high_expired_rate' ELSE '' END
        , 1, 1, '');

    SELECT
        'control_totals'              AS ResultSet,
        @scopedTotal                  AS ScopedLicences,
        @rowSum                       AS TypedLicences   /* + UntypedLicences = ScopedLicences */,
        CAST(1 AS BIT)                AS Reconciled,
        @tenantActive                 AS TenantActiveLicences,
        @tenantExpired                AS TenantExpiredLicences,
        @tenantExpiredPct             AS TenantExpiredPct,
        (SELECT COUNT(*) FROM #rows)  AS LicenceTypesReported,
        (SELECT COUNT(*) FROM #rows r2 WHERE r2.TotalLicences > 0) AS LicenceTypesWithLicences,
        @untyped                      AS UntypedLicences,
        @WindowStart                  AS WindowStart,
        @WindowEnd                    AS WindowEnd,
        @allLicences                  AS AllLicences,
        @allActive                    AS AllActiveLicences,
        @allExpired                   AS AllExpiredLicences;

    SELECT 'rows' AS ResultSet, * FROM #rows ORDER BY TotalLicences DESC;

    /*-- 8. EMISSION POLICY ---------------------------------------------*/
    IF OBJECT_ID('tempdb..#detector') IS NOT NULL DROP TABLE #detector;
    CREATE TABLE #detector (
        Detector VARCHAR(40) PRIMARY KEY, Eligible INT, Flagged INT,
        FlaggedPct DECIMAL(5,1), EmitMode VARCHAR(12));

    DECLARE @material INT = (SELECT COUNT(*) FROM #rows WHERE TotalLicences >= @rankFloor);

    INSERT #detector (Detector, Eligible, Flagged)
    SELECT 'high_expired_rate', @material,
           (SELECT COUNT(*) FROM #rows WHERE Flags LIKE '%high_expired_rate%');

    UPDATE #detector SET FlaggedPct = CASE WHEN Eligible = 0 THEN 0 ELSE 100.0 * Flagged / Eligible END;
    UPDATE #detector
       SET EmitMode = CASE WHEN Flagged = 0        THEN 'none'
                           WHEN Eligible <= 5      THEN 'individual'
                           WHEN FlaggedPct > 20.0  THEN 'aggregate'
                           ELSE 'individual' END;

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

    INSERT #assert VALUES ('A-TENANT','expired_pct',N'tenant',@tenantExpiredPct,NULL,NULL,NULL,NULL,NULL,NULL);

    DECLARE @rankable  INT = (SELECT COUNT(*) FROM #rows WHERE TotalLicences >= @rankFloor);
    DECLARE @tiedAtTop INT = (SELECT COUNT(*) FROM #rows WHERE TotalLicences >= @rankFloor AND OverdueRank = 1);

    IF @rankable >= 2
    INSERT #assert
    SELECT TOP 1 'A-WORST-LICTYPE','expired_pct',LicenseTypeName,ExpiredPct,OverdueRank,@rankable,
           @tenantExpiredPct, ExpiredPct - @tenantExpiredPct,
           CASE WHEN ExpiredPct > @tenantExpiredPct THEN 'worse' ELSE 'better' END,
           NULLIF(CONCAT(
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
    ORDER BY ExpiredPct DESC, TotalLicences DESC;

    IF (SELECT EmitMode FROM #detector WHERE Detector='high_expired_rate') = 'individual'
        INSERT #assert
        SELECT TOP 5 'A-EXPIRED-' + CAST(ROW_NUMBER() OVER (ORDER BY Expired DESC) AS VARCHAR(5)),
               'expired_pct', LicenseTypeName, ExpiredPct, NULL, NULL,
               @tenantExpiredPct, ExpiredPct - @tenantExpiredPct, 'worse', NULL
        FROM #rows WHERE Flags LIKE '%high_expired_rate%' ORDER BY Expired DESC;
    ELSE IF (SELECT EmitMode FROM #detector WHERE Detector='high_expired_rate') = 'aggregate'
        INSERT #assert
        SELECT 'A-EXPIRED-AGG','licence_types_high_expired_rate',N'tenant',
               Flagged, NULL, Eligible, NULL, FlaggedPct, 'worse',
               N'aggregate - an above-average Expired share is spread across many licence types'
        FROM #detector WHERE Detector='high_expired_rate';

    SELECT 'assertions' AS ResultSet, * FROM #assert;

    /*-- 10. FINDINGS ----------------------------------------------------*/
    IF OBJECT_ID('tempdb..#find') IS NOT NULL DROP TABLE #find;
    CREATE TABLE #find (FindingId VARCHAR(20), Severity VARCHAR(10),
        Headline NVARCHAR(1000), AssertionIds VARCHAR(400), NarrativeGuard NVARCHAR(500) NULL);

    INSERT #find
    SELECT 'F-WORST-LICTYPE','high',
           CONCAT(N'', ScopeLabel, N' has the highest share of Expired licences at ', Value, N'%'),
           'A-WORST-LICTYPE,A-TENANT',
           N'Expired is the licence''s latest recorded status. State the count plainly; do not speculate about whether the licence is still valid.'
    FROM #assert WHERE AssertionId = 'A-WORST-LICTYPE' AND Direction = 'worse';

    INSERT #find
    SELECT 'F-EXPIRED','high',
           CONCAT(N'', ScopeLabel, N' has ', Value, N'% of its licences Expired, against a tenant share of ', ComparatorValue, N'%'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId LIKE 'A-EXPIRED-[0-9]%';

    INSERT #find
    SELECT 'F-EXPIRED-AGG','medium',
           CONCAT(N'', CAST(Value AS INT), N' of ', OfN, N' licence types (', VsComparatorPP,
                  N'%) have an Expired share above the tenant average'),
           AssertionId, NULL
    FROM #assert WHERE AssertionId = 'A-EXPIRED-AGG';

    SELECT 'findings' AS ResultSet, * FROM #find;

    /*-- 11. DATA QUALITY - declared, never silent ------------------------*/
    SELECT 'data_quality' AS ResultSet, Issue,
           CASE Issue
                   WHEN 'licence_module_scope'                 THEN 'ScopedLicences'
                   WHEN 'licence_role_scope'                   THEN 'ScopedLicences'
                   WHEN 'licence_no_live_task'                 THEN 'ScopedLicences'
                   WHEN 'licence_report_period'                THEN 'ScopedLicences'
                   WHEN 'licence_status_is_regtrack_status'    THEN 'TenantActiveLicences'
                   WHEN 'perpetual_licences'                   THEN 'TenantActiveLicences'
                   WHEN 'licence_other_status'                 THEN 'ScopedLicences'
                   WHEN 'untyped_licences'                     THEN 'UntypedLicences'
                   WHEN 'licence_type_retired_still_in_use'    THEN 'LicenceTypesReported'
                   WHEN 'licence_status_transaction_volume'    THEN 'ScopedLicences'
                   ELSE NULL END AS AppliesToMetric,
           Detail FROM (
        SELECT 'licence_module_scope' AS Issue,
               N'Licences follow the licence module''s own access rules - the same licences the licence report shows '
             + N'this user - not the compliance branch and category scope the other dimensions use.' AS Detail
        UNION ALL
        SELECT 'licence_role_scope',
               CASE WHEN @licRole = 'CADMN'
                    THEN N'Company Admin licence role: every licence at the company''s operating branches is included.'
                    ELSE CONCAT(N'', @outsideRole, N' licence(s) at the company''s operating branches are outside this user''s '
                         + N'licence role (', CASE @licRole WHEN 'MGMT' THEN N'Management' WHEN 'AUDT' THEN N'Auditor'
                                                            WHEN 'EXCT' THEN N'Non-Admin' ELSE @licRole END,
                         N') and are not included, as in the licence report. '
                         + N'Counts cover only the licences this user is responsible for.') END
        WHERE @licRole = 'CADMN' OR @outsideRole > 0
        UNION ALL
        SELECT 'licence_no_live_task',
               CONCAT(N'', @noLiveTask, N' licence(s) in this user''s scope are not counted, as in the licence report: '
                    + N'the linked compliance task is inactive, has no active performer or has never been acted on, '
                    + N'or the licence has no dated latest status record.')
        WHERE @noLiveTask > 0
        UNION ALL
        SELECT 'licence_report_period',
               CASE WHEN @WindowStart IS NULL
                    THEN CONCAT(N'No report period: all ', @allLicences, N' licence(s) in this user''s scope are counted.')
                    ELSE CONCAT(N'Only licences whose end date falls in the report period are counted - ', @scopedTotal,
                         N' of ', @allLicences, N'. ',
                         @outsideWindow, N' licence(s) end outside the period or have no end date (perpetual) and are not in these figures. ',
                         N'Across all ', @allLicences, N' licences, ', @allActive, N' are Active and ', @allExpired, N' are Expired.') END
        UNION ALL
        SELECT 'licence_status_is_regtrack_status',
               N'Every status count is the licence''s latest recorded status (Active, Expired, Applied, Pending for '
             + N'review and so on), the same status the licence report shows.'
        UNION ALL
        SELECT 'perpetual_licences',
               CONCAT(N'', @perpetual, N' licence(s) in these figures are perpetual. Like every other licence they are counted '
                    + N'under their recorded status, and never as ending in the next 30 days.')
        WHERE @perpetual > 0
        UNION ALL
        SELECT 'licence_other_status',
               CONCAT(N'', @otherTotal, N' licence(s) have a status of Draft, Registered, Registered & Renewal '
                    + N'Filed or Validity Expired, counted together in OtherStatus.')
        WHERE @otherTotal > 0
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
                    + N'this does not affect the status counts above but is a real volume '
                    + N'characteristic of this table, worth knowing before running against a large tenant.')
        WHERE @avgTxnPerLic > 20.0
    ) q;

    DROP TABLE #label; DROP TABLE #branches; DROP TABLE #branchLic; DROP TABLE #roleLic; DROP TABLE #lic; DROP TABLE #allLic;
    DROP TABLE #latestStatus; DROP TABLE #type;
    DROP TABLE #rows; DROP TABLE #detector; DROP TABLE #assert; DROP TABLE #find;
END
GO

PRINT 'Licence dimension v2 (report period, reader text 25) installed.';
GO
