/*===========================================================================
  RegTrack Insights - v4 free tier: follow RegTrack's Detailed Report
  04_usp_Insights_FreeMonthly_LoadFacts.sql

  Free tier follows RegTrack Detailed Report export, 2026-10-06.
  Design: ADR "Free tier monthly digest = RegTrack Detailed Report export"
  (Sec. 7.1). Reference: Kendo_DetailedReport_Details_Pagination_2 (DR),
  MGMT path, tabs 'Statutory' + 'StatutoryChecklist', as called by the
  Detailed Report Excel export. Base: _current_prod/usp_Insights_FreeMonthly_LoadFacts.sql
  (the management-dashboard parity version, 2026-09-29). Rollback: 99_rollback.sql.

  Install order: 00 -> 01 -> 02 -> 03 -> 04 (this file LAST - its callers must
  already create #sched with the ExportRows column).

  WHAT CHANGED vs the prod base, and why
  --------------------------------------
  Testers count ROWS of the Detailed Report Excel export, so the estate, the
  latest status and the item grain all follow the DR instead of the dashboard.

  1. Pre-flight 51234 now requires only the 'timed_by_close_date' rule set.
     The dashboard 'overdue' rule set is no longer read (kept in the table for
     rollback; delete after one clean monthly cycle).
  2. #inst (DR master set, DR 410-501):
       - reads dbo.Compliance_Master (DR R4), not Compliance
       - ComplianceType IS NOT NULL AND EventFlag IS NULL replaces
         ComplianceType <> 1 (checklists IN, event-based OUT; DR 490-495)
       - Compliance_Description, ComplianceCategory and
         dbo.ComplianceType (Act.ComplianceTypeId) rows must exist - the DR's
         final select INNER-joins them (DR 1707, 1715, 1721)
       - unchanged: scope pairs, branch filters, active-performer EXISTS,
         COALESCE(ci.Risk, c.RiskType), no ci.IsDeleted filter (DR R13)
  3. Latest status = the DR's tied set (DR 1110-1161):
       - MAX(Dated) over ALL transactions of the schedule
       - only tied rows whose StatusId exists in ComplianceStatus are kept;
         if none are kept the schedule is NOT loaded (as in the DR - on UAT a
         schedule whose newest status is 20 disappears)
       - representative row = highest ID among the kept tied rows
       NOTE (ADR U1, decided: no row split): tied rows carrying DIFFERENT
       statuses all land in the representative's bucket; the export would split
       them across Status values. Measured: 3 schedules on one prod tenant.
  4. Overdue = vInsightsStatusCurrent.OverdueEligible = 1 and due DATE before
     the as-at DATE (the dashboard overdue set is retired).
  5. Approved ids (rule 'timed_by_close_date') are timed at DATE grain:
     CAST(StatusChangedOn AS DATE) <= CAST(ScheduleOn AS DATE) = on time.
  6. Window split at the as-at DATE, not the @AsOf instant:
       curr_elapsed = [CurrStart, @AsOfDate), curr_remaining = [@AsOfDate, Next)
  7. NEW #sched.ExportRows (caller-created, last column, DEFAULT 1) = the number
     of Detailed Report export rows this schedule produces (ADR Sec. 5.2):
       ExportRows = P x K x D x L x M x G x A x F x T
     Every factor is >= 1 by construction (defaulted to 1, raised only by a
     grouped DISTINCT count). Slot procs sum it for item counts.
  8. 51232 reworded: curr_remaining is now "due on or after the as-at DATE".

  Contract: same parameters, no result set (helper proc), same #inst / #sched
  columns plus ExportRows. No new error codes (block 51230-51239).
  Pure ASCII (CLAUDE.md Sec. 5a). No join hints (CLAUDE.md Sec. 5).
===========================================================================*/
SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Insights_FreeMonthly_LoadFacts
    @UserID          INT,
    @CustomerID      INT,
    @CurrMonthStart  DATE,
    @AsOf            DATETIME
AS
BEGIN
    SET NOCOUNT ON;

    /*===================================================================
      0. INPUT CONTRACT - fail closed before reading anything
    ===================================================================*/
    IF @UserID IS NULL OR @CustomerID IS NULL OR @CurrMonthStart IS NULL OR @AsOf IS NULL
        THROW 51235, N'FREE MONTHLY - WINDOW INPUT MISSING: @UserID, @CustomerID, @CurrMonthStart and @AsOf are all required. The caller resolves the edition and passes concrete values. Refusing to compute.', 1;

    IF DATEPART(DAY, @CurrMonthStart) <> 1
        THROW 51236, N'FREE MONTHLY - @CurrMonthStart IS NOT THE FIRST OF A MONTH. It must be the 1st of the edition month, computed from the edition in C#. Refusing to compute.', 1;

    DECLARE @CurrStart      DATETIME = CAST(@CurrMonthStart AS DATETIME);
    DECLARE @PrevMonthStart DATETIME = DATEADD(MONTH, -1, @CurrStart);
    DECLARE @NextMonthStart DATETIME = DATEADD(MONTH,  1, @CurrStart);
    DECLARE @AsOfDate       DATETIME = CAST(CAST(@AsOf AS DATE) AS DATETIME);   -- the Detailed Report compares whole dates

    IF @AsOf < @CurrStart OR @AsOf >= @NextMonthStart
        THROW 51237, N'FREE MONTHLY - @AsOf FALLS OUTSIDE THE EDITION MONTH. Most likely a clock mismatch (a UTC @AsOf against a local-time month). Pass @AsOf in the same clock as ComplianceScheduleOn.ScheduleOn. Refusing to compute.', 1;

    /*===================================================================
      1. SCOPE + DICTIONARY PRE-FLIGHT
    ===================================================================*/
    IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
        THROW 51230, N'SCOPE DENIED - user has no authorised (branch, category) pairs for this tenant. Refusing to compute.', 1;

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- THROWs 51001 on a dictionary gap; returns no grid

    /*  [v4] Only the Approved timing rule is read now. Overdue comes from
        vInsightsStatusCurrent.OverdueEligible (dictionary v2).            */
    IF NOT EXISTS (SELECT 1 FROM dbo.InsightsFreeDashboardStatusRule WHERE RuleName = 'timed_by_close_date')
        THROW 51234, N'DICTIONARY GAP - dbo.InsightsFreeDashboardStatusRule has no timed_by_close_date rule set (the Approved ids the Detailed Report times by close date). It is seeded by sql/34; redeploy sql/34. Refusing to compute.', 1;

    /*  Active, undeleted users of THIS tenant - the DR's #tempUser (DR 228-248).
        An obligation counts only when one of them holds its performer role.  */
    IF OBJECT_ID('tempdb..#lf_user') IS NOT NULL DROP TABLE #lf_user;
    SELECT u.ID
    INTO #lf_user
    FROM [User] u
    WHERE u.CustomerID = @CustomerID
      AND u.IsDeleted  = 0
      AND u.IsActive   = 1;
    CREATE CLUSTERED INDEX IX_lf_user ON #lf_user (ID);

    IF OBJECT_ID('tempdb..#lf_risk') IS NOT NULL DROP TABLE #lf_risk;
    SELECT TRY_CAST(p.RawValue AS INT) AS RawValue, p.Meaning
    INTO #lf_risk
    FROM dbo.InsightsEnumPolarity p
    JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
    WHERE p.Semantic = 'RiskType'
      AND TRY_CAST(p.RawValue AS INT) IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM #lf_risk WHERE Meaning = N'Critical')
        THROW 51239, N'DICTIONARY GAP - InsightsEnumPolarity has no current RiskType row meaning Critical. The monthly tier never compares RiskType to a literal; seed the BA-verified mapping and re-run. Refusing to compute.', 1;

    /*===================================================================
      2. SCOPED INSTANCES - materialised FIRST (scope-first, then reach)
         [v4] The Detailed Report master set, Statutory + Statutory
         Checklist tabs, MGMT path (DR 410-501, ADR R1-R19):
           - dbo.Compliance_Master (DR reads it unconditionally)
           - ComplianceType IS NOT NULL AND EventFlag IS NULL
             (= Statutory "<> 1" UNION Checklist "= 1", both EventFlag NULL)
           - act not deleted, compliance not deleted, compliance visible
           - a performer (RoleID 3) held by an active user of this tenant
           - Compliance_Description, ComplianceCategory and
             dbo.ComplianceType(Act.ComplianceTypeId) rows exist (DR final
             select INNER-joins them: DR 1707, 1715, 1721)
         Risk = instance override first, as the DR reads it
         (COALESCE(CI.Risk, CM.RiskType), DR 457).
         Selected here, not via tvfInsightsScopedInstances, because that
         shared function drops soft-deleted instances and the DR does not
         (DR R13). Same 2-D scope pairs, same branch filters.
    ===================================================================*/
    INSERT #inst (ComplianceInstanceID, BranchID, CategoryId, ActID, ComplianceID,
                  DepartmentID, Imprisonment, RiskType, RiskClass)
    SELECT ci.ID,
           ci.CustomerBranchID,
           a.ComplianceCategoryId,
           a.ID,
           ci.ComplianceID,
           ci.DepartmentID,
           CAST(ISNULL(c.Imprisonment, 0) AS BIT),
           COALESCE(ci.Risk, c.RiskType),
           r.Meaning
    FROM ComplianceInstance ci
    JOIN CustomerBranch cb          ON cb.ID = ci.CustomerBranchID
    JOIN dbo.Compliance_Master c    ON c.ID  = ci.ComplianceID
    JOIN Act a                      ON a.ID  = c.ActID
    JOIN dbo.tvfInsightsScopePairs(@UserID, @CustomerID) sp      -- 2-D: BOTH branch AND category
         ON sp.BranchID   = ci.CustomerBranchID
        AND sp.CategoryId = a.ComplianceCategoryId
    LEFT JOIN #lf_risk r            ON r.RawValue = COALESCE(ci.Risk, c.RiskType)
    WHERE cb.CustomerID = @CustomerID
      AND cb.IsDeleted  = 0 AND cb.Status = 1
      AND c.IsDeleted   = 0
      AND a.IsDeleted   = 0
      AND (c.ComplinceVisible = 1 OR c.ComplinceVisible IS NULL)   -- schema spelling
      AND c.ComplianceType IS NOT NULL                             -- [v4] Statutory + Statutory Checklist tabs
      AND c.EventFlag IS NULL                                      -- [v4] event-based tabs excluded
      AND EXISTS (SELECT 1
                  FROM ComplianceAssignment ca
                  JOIN #lf_user u ON u.ID = ca.UserID
                  WHERE ca.ComplianceInstanceID = ci.ID
                    AND ca.RoleID = 3)                             -- 3 = performer (a role id, not a status)
      AND EXISTS (SELECT 1 FROM dbo.Compliance_Description cd
                  WHERE cd.ComplianceID = ci.ComplianceID)         -- [v4] DR 1707 (#tempComplianceLOB INNER)
      AND EXISTS (SELECT 1 FROM dbo.ComplianceCategory cc
                  WHERE cc.ID = a.ComplianceCategoryId)            -- [v4] DR 1715
      AND EXISTS (SELECT 1 FROM dbo.ComplianceType ctm
                  WHERE ctm.ID = a.ComplianceTypeId);              -- [v4] DR 1721

    CREATE NONCLUSTERED INDEX IX_inst_branch ON #inst (BranchID) INCLUDE (ActID, Imprisonment, RiskClass);

    /*  Instance-level owner fallback. MIN(UserID) = deterministic pick when an
        instance has several assignees. UserID > 0 excludes placeholder rows. */
    IF OBJECT_ID('tempdb..#lf_owner') IS NOT NULL DROP TABLE #lf_owner;
    SELECT ca.ComplianceInstanceID,
           MIN(CASE WHEN ca.RoleID = 3 THEN ca.UserID END) AS PerformerID,
           MIN(CASE WHEN ca.RoleID = 4 THEN ca.UserID END) AS ReviewerID
    INTO #lf_owner
    FROM #inst i
    JOIN ComplianceAssignment ca ON ca.ComplianceInstanceID = i.ComplianceInstanceID
    WHERE ca.UserID > 0
      AND ca.RoleID IN (3, 4)      -- 3 = performer, 4 = reviewer (role ids, not statuses)
    GROUP BY ca.ComplianceInstanceID;

    CREATE CLUSTERED INDEX IX_lf_owner ON #lf_owner (ComplianceInstanceID);

    /*===================================================================
      3. ONE READ: window schedules + overdue stock, latest status resolved once
         [v4] Latest = the DR's tied set (DR 1110-1161, ADR T1-T3):
           mx = MAX(Dated) over ALL transactions of the schedule (TOP 1
                Dated DESC; NULL Dated sorts last, as MAX ignores NULL)
           rp = highest ID among the transactions AT mx.Dated whose StatusId
                exists in ComplianceStatus. None -> CROSS APPLY drops the
                schedule, exactly as the DR's INNER join to the status table.
                It never falls back to an older Dated.
         A schedule with no transaction at all is not loaded either (DR #PartB
         INNER, ADR S3), so NeverTouched stays 0.
         [PERF] explicit TOP 1 seeks on IX_CT_CSO_Dated_ID - never the view.
         [PERF] no join hints anywhere (CLAUDE.md Sec.5, 877 ms -> 26,492 ms).
    ===================================================================*/
    IF OBJECT_ID('tempdb..#lf_rep') IS NOT NULL DROP TABLE #lf_rep;
    CREATE TABLE #lf_rep (
        ComplianceScheduleOnID BIGINT       NOT NULL PRIMARY KEY,
        ComplianceInstanceID   BIGINT       NOT NULL,
        BranchID               INT          NOT NULL,
        ScheduleOn             DATETIME     NOT NULL,
        SchedPerformerID       BIGINT       NULL,
        SchedReviewerID        BIGINT       NULL,
        MaxDated               DATETIME     NOT NULL,  -- DATETIME as the DR's own #partb2.Dated (DR 1102-1105)
        RepTransactionID       BIGINT       NOT NULL,
        StatusId               INT          NOT NULL,
        StatusChangedOn        DATETIME     NULL,
        DictStatusId           INT          NULL,      -- NULL = dictionary cannot place it (51238)
        ClosureClass           VARCHAR(20)  NULL,
        Timeliness             VARCHAR(10)  NULL,
        OverdueEligible        BIT          NULL,
        TimedByCloseDate       BIT          NOT NULL
    );

    INSERT #lf_rep (ComplianceScheduleOnID, ComplianceInstanceID, BranchID, ScheduleOn,
                    SchedPerformerID, SchedReviewerID, MaxDated, RepTransactionID, StatusId,
                    StatusChangedOn, DictStatusId, ClosureClass, Timeliness, OverdueEligible,
                    TimedByCloseDate)
    SELECT
        cso.ID,
        i.ComplianceInstanceID,
        i.BranchID,
        cso.ScheduleOn,
        cso.Performerid,
        cso.Reviewerid,
        mx.Dated,
        rp.ID,
        rp.StatusId,
        rp.StatusChangedOn,
        d.StatusId,
        d.ClosureClass,
        d.Timeliness,
        d.OverdueEligible,
        CAST(CASE WHEN tc.StatusId IS NOT NULL THEN 1 ELSE 0 END AS BIT)
    FROM #inst i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    CROSS APPLY (SELECT TOP 1 t.Dated
                 FROM ComplianceTransaction t
                 WHERE t.ComplianceScheduleOnID = cso.ID
                 ORDER BY t.Dated DESC) mx
    CROSS APPLY (SELECT TOP 1 t2.ID, t2.StatusId, t2.StatusChangedOn
                 FROM ComplianceTransaction t2
                 JOIN ComplianceStatus cs ON cs.ID = t2.StatusId          -- known status only (DR 1160-1161)
                 WHERE t2.ComplianceScheduleOnID = cso.ID
                   AND t2.Dated = mx.Dated
                 ORDER BY t2.ID DESC) rp
    LEFT JOIN dbo.vInsightsStatusCurrent d            ON d.StatusId  = rp.StatusId
    LEFT JOIN dbo.InsightsFreeDashboardStatusRule tc  ON tc.StatusId = rp.StatusId AND tc.RuleName = 'timed_by_close_date'
    WHERE cso.IsActive = 1
      AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn < @NextMonthStart
      AND (    cso.ScheduleOn >= @PrevMonthStart
           OR (cso.ScheduleOn < @AsOfDate AND d.OverdueEligible = 1) );

    CREATE NONCLUSTERED INDEX IX_lf_rep_inst ON #lf_rep (ComplianceInstanceID);

    /*===================================================================
      3b. EXPORT-ROW FACTORS (ADR Sec. 5.2)
          ExportRows(S) = P x K x D x L x M x G x A x F x T
          One export row = one DISTINCT tuple of the DR final select
          (DR 1420-1421). Each factor feeds a disjoint group of output columns
          through its own join, so the DISTINCT count is the product of each
          group's DISTINCT count. Each factor = SELECT DISTINCT over the DR's
          own expressions, then COUNT(*) per key (NULLs compare equal under
          DISTINCT, as in the DR). Defaults are 1 ("no row" = 1 row).
          Instance-level: P, D, G, A, F, T  (instance fixes compliance + branch)
          Schedule-level: K (tied tx x audit), L, M
    ===================================================================*/
    IF OBJECT_ID('tempdb..#lf_fi') IS NOT NULL DROP TABLE #lf_fi;
    CREATE TABLE #lf_fi (
        ComplianceInstanceID BIGINT NOT NULL PRIMARY KEY,
        ComplianceID         BIGINT NOT NULL,
        BranchID             INT    NOT NULL,
        P                    INT    NOT NULL DEFAULT 1,
        D                    INT    NOT NULL DEFAULT 1,
        G                    INT    NOT NULL DEFAULT 1,
        A                    INT    NOT NULL DEFAULT 1,
        F                    INT    NOT NULL DEFAULT 1,
        T                    INT    NOT NULL DEFAULT 1
    );

    INSERT #lf_fi (ComplianceInstanceID, ComplianceID, BranchID)
    SELECT i.ComplianceInstanceID, i.ComplianceID, i.BranchID
    FROM #inst i
    WHERE EXISTS (SELECT 1 FROM #lf_rep r WHERE r.ComplianceInstanceID = i.ComplianceInstanceID);

    CREATE NONCLUSTERED INDEX IX_lf_fi_comp ON #lf_fi (ComplianceID, BranchID);

    IF OBJECT_ID('tempdb..#lf_fs') IS NOT NULL DROP TABLE #lf_fs;
    CREATE TABLE #lf_fs (
        ComplianceScheduleOnID BIGINT NOT NULL PRIMARY KEY,
        KT                     INT    NOT NULL DEFAULT 1,   -- K, tied-transaction part
        KA                     INT    NOT NULL DEFAULT 1,   -- K, audit part
        L                      INT    NOT NULL DEFAULT 1,
        M                      INT    NOT NULL DEFAULT 1
    );

    INSERT #lf_fs (ComplianceScheduleOnID)
    SELECT r.ComplianceScheduleOnID FROM #lf_rep r;

    /*  P - distinct active performers of the instance (DR 480-486, 1601:
        CI.UserID is a select column; duplicate EA/CA rows collapse).     */
    UPDATE fi SET P = x.N
    FROM #lf_fi fi
    JOIN (SELECT p.ComplianceInstanceID, COUNT(*) AS N
          FROM (SELECT DISTINCT f2.ComplianceInstanceID, ca.UserID
                FROM #lf_fi f2
                JOIN ComplianceAssignment ca ON ca.ComplianceInstanceID = f2.ComplianceInstanceID
                JOIN #lf_user u              ON u.ID = ca.UserID
                WHERE ca.RoleID = 3) p                          -- 3 = performer (role id)
          GROUP BY p.ComplianceInstanceID) x
      ON x.ComplianceInstanceID = fi.ComplianceInstanceID;

    /*  D - Compliance_Description rows of C x Compliance_ShortDescr_ByClient
        rows of I, on the DR's own output expressions (DR 1355-1379,
        1428-1435, 1596, 1687-1688, 1707, 1761). Others reaches the output
        only through ImprisonmentTenure when NonComplianceType IN (1,2) AND
        Imprisonment = 0; every other branch of that CASE is fixed per
        compliance.                                                         */
    UPDATE fi SET D = x.N
    FROM #lf_fi fi
    JOIN (SELECT dd.ComplianceInstanceID, COUNT(*) AS N
          FROM (SELECT DISTINCT
                       f2.ComplianceInstanceID,
                       CASE WHEN csd.ShortDescription IS NULL THEN cd.ShortDescription
                            ELSE csd.ShortDescription END                       AS ShortDescription,
                       cd.ShortForm,
                       cd.Sections,
                       cd.Description,
                       cd.PenaltyDescription,
                       REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                           cd.ReferenceMaterialText,
                           CHAR(9),  ' '),
                           CHAR(34), ' '),
                           CHAR(13), ' '),
                           CHAR(13), ' '),
                           CHAR(20), ' '),
                           CHAR(10), '')                                        AS ReferenceMaterialText,
                       CASE WHEN cm.NonComplianceType IN (1, 2) AND cm.Imprisonment = 0
                            THEN cd.Others END                                  AS OthersShown
                FROM #lf_fi f2
                JOIN dbo.Compliance_Master cm          ON cm.ID = f2.ComplianceID
                JOIN dbo.Compliance_Description cd     ON cd.ComplianceID = f2.ComplianceID
                LEFT JOIN Compliance_ShortDescr_ByClient csd
                       ON csd.ComplianceInstanceID = f2.ComplianceInstanceID) dd
          GROUP BY dd.ComplianceInstanceID) x
      ON x.ComplianceInstanceID = fi.ComplianceInstanceID;

    /*  G - (GroupID, GroupName) for (compliance, branch); both rows not
        deleted; this customer (DR 972-985, 1595, 1602, 1754-1756).       */
    UPDATE fi SET G = x.N
    FROM #lf_fi fi
    JOIN (SELECT g.ComplianceInstanceID, COUNT(*) AS N
          FROM (SELECT DISTINCT f2.ComplianceInstanceID, gm.ID AS GroupID, gm.NAME AS GroupName
                FROM #lf_fi f2
                JOIN GroupingDetails gd ON gd.ComplianceID = f2.ComplianceID
                                       AND gd.BranchID     = f2.BranchID
                JOIN GroupMaster gm     ON gm.ID           = gd.GroupID
                                       AND gm.CustomerID   = gd.CustomerID
                WHERE gm.CustomerID = @CustomerID
                  AND gm.IsDeleted  = 0
                  AND gd.IsDeleted  = 0) g
          GROUP BY g.ComplianceInstanceID) x
      ON x.ComplianceInstanceID = fi.ComplianceInstanceID;

    /*  A - cleaned ActionableProcedure for the compliance; this customer
        (DR 1047-1063, 1608-1611, 1759). The DR stages it as VARCHAR(MAX). */
    UPDATE fi SET A = x.N
    FROM #lf_fi fi
    JOIN (SELECT ap.ComplianceInstanceID, COUNT(*) AS N
          FROM (SELECT DISTINCT
                       f2.ComplianceInstanceID,
                       REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                           CAST(apm.ActionableProcedure AS VARCHAR(MAX)),
                           CHAR(9),  ' '), CHAR(34), ' '), CHAR(13), ' '),
                           CHAR(13), ' '), CHAR(20), ' '), CHAR(10), '')        AS ActionableProcedure
                FROM #lf_fi f2
                JOIN ActionableProcedureComplianceMapping apm ON apm.ComplianceID = f2.ComplianceID
                WHERE apm.CustomerID = @CustomerID) ap
          GROUP BY ap.ComplianceInstanceID) x
      ON x.ComplianceInstanceID = fi.ComplianceInstanceID;

    /*  F - client checklist frequency label for the compliance; not deleted;
        this customer (DR 918-927, 1535-1548, 1773-1775). Counted on the
        LABEL, as the DR outputs it (codes outside 0-11 all render NULL).   */
    UPDATE fi SET F = x.N
    FROM #lf_fi fi
    JOIN (SELECT fq.ComplianceInstanceID, COUNT(*) AS N
          FROM (SELECT DISTINCT
                       f2.ComplianceInstanceID,
                       CASE cbcf.CFrequency
                           WHEN 0  THEN 'Monthly'
                           WHEN 1  THEN 'Quarterly'
                           WHEN 2  THEN 'HalfYearly'
                           WHEN 3  THEN 'Annual'
                           WHEN 4  THEN 'FourMonthly'
                           WHEN 5  THEN 'TwoYearly'
                           WHEN 6  THEN 'SevenYearly'
                           WHEN 7  THEN 'Daily'
                           WHEN 8  THEN 'Weekly'
                           WHEN 9  THEN 'ThreeYearly'
                           WHEN 10 THEN 'FiveYearly'
                           WHEN 11 THEN 'FortNightly'
                       END                                                      AS ClientFrequency
                FROM #lf_fi f2
                JOIN ClientBasedCheckListFrequency cbcf ON cbcf.ComplianceID = f2.ComplianceID
                WHERE cbcf.IsDeleted = 0
                  AND cbcf.ClientID  = @CustomerID) fq
          GROUP BY fq.ComplianceInstanceID) x
      ON x.ComplianceInstanceID = fi.ComplianceInstanceID;

    /*  T - Temp_CustomerBranch.NAME rows for the branch (DR 364-377, 1423, 1746). */
    UPDATE fi SET T = x.N
    FROM #lf_fi fi
    JOIN (SELECT tb.ComplianceInstanceID, COUNT(*) AS N
          FROM (SELECT DISTINCT f2.ComplianceInstanceID, tcb.NAME
                FROM #lf_fi f2
                JOIN Temp_CustomerBranch tcb ON tcb.BranchId = f2.BranchID) tb
          GROUP BY tb.ComplianceInstanceID) x
      ON x.ComplianceInstanceID = fi.ComplianceInstanceID;

    /*  K, tied-transaction part - distinct output tuples of the tied latest
        transactions with a known status, at EXACTLY #lf_rep.MaxDated, so the
        representative and K come from the same tied set (non-negotiable 3).
        Types follow the DR's #PartB staging (DR 1110-1161, 1445-1449,
        1584-1590, 1613-1633). Raw StatusId is used, not the DR label: two
        tied ids the DR renders alike would count 2 here (ADR U1).          */
    UPDATE fs SET KT = x.N
    FROM #lf_fs fs
    JOIN (SELECT k.ComplianceScheduleOnID, COUNT(*) AS N
          FROM (SELECT DISTINCT
                       r.ComplianceScheduleOnID,
                       t.StatusId,
                       CAST(t.StatusChangedOn AS DATE)                AS CloseDate,
                       CAST(t.ChallanNo       AS VARCHAR(MAX))        AS ChallanNo,
                       CAST(t.ChallanAmount   AS VARCHAR(MAX))        AS ChallanAmount,
                       CAST(t.BankName        AS VARCHAR(MAX))        AS BankName,
                       CAST(t.Challanpaiddate AS DATE)                AS Challanpaiddate,
                       CAST(t.Penalty           AS NUMERIC(18, 2))    AS Penalty,
                       CAST(t.ValuesAsPerReturn AS NUMERIC(18, 2))    AS ValuesAsPerReturn
                FROM #lf_rep r
                JOIN ComplianceTransaction t ON t.ComplianceScheduleOnID = r.ComplianceScheduleOnID
                                            AND t.Dated                  = r.MaxDated
                JOIN ComplianceStatus cs     ON cs.ID = t.StatusId) k
          GROUP BY k.ComplianceScheduleOnID) x
      ON x.ComplianceScheduleOnID = fs.ComplianceScheduleOnID;

    /*  K, audit part - audit rows at the schedule's max audit Dated,
        distinct StatusID (DR 1066-1094). The DR's max is taken over audit
        rows whose instance is in its master set.                          */
    /*  SELECT ... INTO (never altered later) so MaxDated keeps the source
        column's exact type - the DR's PartA does the same - and the
        equality re-join below cannot miss on a rounded value.             */
    IF OBJECT_ID('tempdb..#lf_audmax') IS NOT NULL DROP TABLE #lf_audmax;
    SELECT ra.ComplianceScheduleOnID, MAX(ra.Dated) AS MaxDated
    INTO #lf_audmax
    FROM #lf_rep r
    JOIN dbo.ReOpeningComplianeAuditTransaction ra ON ra.ComplianceScheduleOnID = r.ComplianceScheduleOnID
    JOIN #inst ia                                  ON ia.ComplianceInstanceID   = ra.ComplianceInstanceId
    WHERE ra.Dated IS NOT NULL
    GROUP BY ra.ComplianceScheduleOnID;

    CREATE CLUSTERED INDEX IX_lf_audmax ON #lf_audmax (ComplianceScheduleOnID);

    UPDATE fs SET KA = x.N
    FROM #lf_fs fs
    JOIN (SELECT au.ComplianceScheduleOnID, COUNT(*) AS N
          FROM (SELECT DISTINCT am.ComplianceScheduleOnID, rb.StatusID
                FROM #lf_audmax am
                JOIN dbo.ReOpeningComplianeAuditTransaction rb
                  ON rb.ComplianceScheduleOnID = am.ComplianceScheduleOnID
                 AND rb.Dated                  = am.MaxDated) au
          GROUP BY au.ComplianceScheduleOnID) x
      ON x.ComplianceScheduleOnID = fs.ComplianceScheduleOnID;

    /*  L - LicenseNo of licences mapped to the schedule (DR 1405-1415, 1515, 1750). */
    UPDATE fs SET L = x.N
    FROM #lf_fs fs
    JOIN (SELECT ln.ComplianceScheduleOnID, COUNT(*) AS N
          FROM (SELECT DISTINCT r.ComplianceScheduleOnID,
                       CAST(lti.LicenseNo AS VARCHAR(500)) AS LicenseNo
                FROM #lf_rep r
                JOIN Lic_tbl_LicenseComplianceInstanceScheduleOnMapping lsm
                  ON lsm.ComplianceScheduleOnID = r.ComplianceScheduleOnID
                JOIN Lic_tbl_LicenseInstance lti
                  ON lti.ID = lsm.LicenseID) ln
          GROUP BY ln.ComplianceScheduleOnID) x
      ON x.ComplianceScheduleOnID = fs.ComplianceScheduleOnID;

    /*  M - mitigation plan rows at the schedule's max Createdon; Type
        'Statutory'; this customer (DR 999-1036, 1591-1594, 1752). Types
        follow the DR's staging table (VARCHAR(MAX), TimeLine VARCHAR(20)). */
    IF OBJECT_ID('tempdb..#lf_mitmax') IS NOT NULL DROP TABLE #lf_mitmax;
    SELECT cmp2.CompliancescheduleonID AS ComplianceScheduleOnID,
           MAX(cmp2.Createdon)         AS MaxCreatedon     -- INTO keeps the source type (exact re-join)
    INTO #lf_mitmax
    FROM #lf_rep r
    JOIN ComplianceMitigationPlan cmp2 ON cmp2.CompliancescheduleonID = r.ComplianceScheduleOnID
    WHERE cmp2.Type       = 'Statutory'
      AND cmp2.CustomerID = @CustomerID
      AND cmp2.Createdon IS NOT NULL
    GROUP BY cmp2.CompliancescheduleonID;

    CREATE CLUSTERED INDEX IX_lf_mitmax ON #lf_mitmax (ComplianceScheduleOnID);

    UPDATE fs SET M = x.N
    FROM #lf_fs fs
    JOIN (SELECT mp.ComplianceScheduleOnID, COUNT(*) AS N
          FROM (SELECT DISTINCT
                       mm.ComplianceScheduleOnID,
                       CAST(cmp.Reason           AS VARCHAR(MAX))                   AS Reason,
                       CAST(cmp.Legal_Financial  AS VARCHAR(MAX))                   AS Legal_Financial,
                       CAST(cmp.Remediation_plan AS VARCHAR(MAX))                   AS Remediation_plan,
                       CAST(FORMAT(cmp.TimeLine, 'dd-MMM-yyyy') AS VARCHAR(20))     AS TimeLine
                FROM #lf_mitmax mm
                JOIN ComplianceMitigationPlan cmp
                  ON cmp.CompliancescheduleonID = mm.ComplianceScheduleOnID
                 AND cmp.Createdon              = mm.MaxCreatedon
                WHERE cmp.Type       = 'Statutory'
                  AND cmp.CustomerID = @CustomerID) mp
          GROUP BY mp.ComplianceScheduleOnID) x
      ON x.ComplianceScheduleOnID = fs.ComplianceScheduleOnID;

    /*===================================================================
      3c. #sched from the representative rows, ExportRows set explicitly
    ===================================================================*/
    INSERT #sched (ComplianceScheduleOnID, ComplianceInstanceID, BranchID, ScheduleOn,
                   WindowPart, PerformerID, PerformerSource, ReviewerID, ReviewerSource,
                   NeverTouched, Outcome, IsOverdue, DaysPastDue, AgeBand, ExportRows)
    SELECT
        r.ComplianceScheduleOnID,
        r.ComplianceInstanceID,
        r.BranchID,
        r.ScheduleOn,
        /*  [v4] split at the as-at DATE: due today is "rest of month". */
        CASE WHEN r.ScheduleOn >= @PrevMonthStart AND r.ScheduleOn < @CurrStart      THEN 'prev_month'
             WHEN r.ScheduleOn >= @CurrStart      AND r.ScheduleOn < @AsOfDate       THEN 'curr_elapsed'
             WHEN r.ScheduleOn >= @AsOfDate       AND r.ScheduleOn < @NextMonthStart THEN 'curr_remaining'
             ELSE NULL END,
        CASE WHEN r.SchedPerformerID IS NOT NULL AND r.SchedPerformerID <> 0 THEN r.SchedPerformerID
             ELSE o.PerformerID END,
        CASE WHEN r.SchedPerformerID IS NOT NULL AND r.SchedPerformerID <> 0 THEN 'schedule'
             WHEN o.PerformerID IS NOT NULL                                  THEN 'instance'
             ELSE 'none' END,
        CASE WHEN r.SchedReviewerID IS NOT NULL AND r.SchedReviewerID <> 0 THEN r.SchedReviewerID
             ELSE o.ReviewerID END,
        CASE WHEN r.SchedReviewerID IS NOT NULL AND r.SchedReviewerID <> 0 THEN 'schedule'
             WHEN o.ReviewerID IS NOT NULL                                 THEN 'instance'
             ELSE 'none' END,
        CAST(0 AS BIT),              -- NeverTouched: a schedule with no transaction is not loaded (DR S3)
        CASE WHEN r.DictStatusId IS NULL                                      THEN 'unclassified'
             /*  The "Approved" ids are timed by close DATE, not by id (DR 1730). */
             WHEN r.ClosureClass = 'completed' AND r.TimedByCloseDate = 1
                  THEN CASE WHEN r.StatusChangedOn IS NULL                                    THEN 'completed_untimed'
                            WHEN CAST(r.StatusChangedOn AS DATE) <= CAST(r.ScheduleOn AS DATE) THEN 'completed_on_time'
                            ELSE 'completed_late' END
             WHEN r.ClosureClass = 'completed' AND r.Timeliness = 'on_time'   THEN 'completed_on_time'
             WHEN r.ClosureClass = 'completed' AND r.Timeliness = 'delayed'   THEN 'completed_late'
             WHEN r.ClosureClass = 'completed'                                THEN 'completed_untimed'
             WHEN r.ClosureClass = 'resolved_terminal'                        THEN 'resolved_terminal'
             WHEN r.ClosureClass = 'open'                                     THEN 'open'
             ELSE 'unclassified' END,
        CAST(CASE WHEN r.ScheduleOn < @AsOfDate AND r.OverdueEligible = 1 THEN 1 ELSE 0 END AS BIT),
        CASE WHEN r.ScheduleOn < @AsOfDate AND r.OverdueEligible = 1
             THEN DATEDIFF(DAY, r.ScheduleOn, @AsOf) END,
        CASE WHEN r.ScheduleOn < @AsOfDate AND r.OverdueEligible = 1
             THEN CASE WHEN DATEDIFF(DAY, r.ScheduleOn, @AsOf) <= 30 THEN 'd000_030'
                       WHEN DATEDIFF(DAY, r.ScheduleOn, @AsOf) <= 60 THEN 'd031_060'
                       WHEN DATEDIFF(DAY, r.ScheduleOn, @AsOf) <= 90 THEN 'd061_090'
                       ELSE 'd091_plus' END END,
        /*  ADR Sec. 5.2: P x K x D x L x M x G x A x F x T, every factor >= 1. */
        fi.P * fs.KT * fs.KA * fi.D * fs.L * fs.M * fi.G * fi.A * fi.F * fi.T
    FROM #lf_rep r
    JOIN #lf_fi fi        ON fi.ComplianceInstanceID   = r.ComplianceInstanceID
    JOIN #lf_fs fs        ON fs.ComplianceScheduleOnID = r.ComplianceScheduleOnID
    LEFT JOIN #lf_owner o ON o.ComplianceInstanceID    = r.ComplianceInstanceID;

    CREATE NONCLUSTERED INDEX IX_sched_window ON #sched (WindowPart, Outcome) INCLUDE (ComplianceInstanceID, BranchID);
    CREATE NONCLUSTERED INDEX IX_sched_overdue ON #sched (IsOverdue, AgeBand) INCLUDE (ComplianceInstanceID, BranchID, PerformerID);
    CREATE NONCLUSTERED INDEX IX_sched_perf ON #sched (PerformerID) INCLUDE (WindowPart, Outcome, IsOverdue);

    /*===================================================================
      4. STRUCTURAL INVARIANTS - these hold REGARDLESS of data, so they
         THROW (CLAUDE.md Sec.11). A failure means this CODE is wrong.
         All are per-row predicates; ExportRows does not touch them.
    ===================================================================*/
    IF EXISTS (SELECT 1 FROM #sched WHERE Outcome = 'unclassified')
        THROW 51238, N'DICTIONARY GAP - a scoped schedule has a latest status the dictionary cannot place in a closure class (status id absent from vInsightsStatusCurrent, or an unknown ClosureClass). Refusing to guess.', 1;

    IF EXISTS (SELECT 1 FROM #sched WHERE WindowPart IS NULL AND IsOverdue = 0)
        THROW 51231, N'FREE MONTHLY RECONCILIATION FAILED - a row outside the window is not overdue. Rows outside the window may exist only as overdue stock; the load predicate has drifted. Refusing to publish.', 1;

    IF EXISTS (SELECT 1 FROM #sched WHERE WindowPart = 'curr_remaining' AND IsOverdue = 1)
        THROW 51232, N'FREE MONTHLY RECONCILIATION FAILED - a schedule due on or after the as-at DATE is marked overdue. Overdue requires a due date before the as-at date. Refusing to publish.', 1;

    IF EXISTS (SELECT 1 FROM #sched
               WHERE (IsOverdue = 1 AND (DaysPastDue IS NULL OR AgeBand IS NULL))
                  OR (IsOverdue = 0 AND (DaysPastDue IS NOT NULL OR AgeBand IS NOT NULL)))
        THROW 51233, N'FREE MONTHLY RECONCILIATION FAILED - overdue age fields are inconsistent with IsOverdue. Refusing to publish.', 1;

    DROP TABLE #lf_mitmax;
    DROP TABLE #lf_audmax;
    DROP TABLE #lf_fs;
    DROP TABLE #lf_fi;
    DROP TABLE #lf_rep;
    DROP TABLE #lf_owner;
    DROP TABLE #lf_risk;
    DROP TABLE #lf_user;
END
GO

PRINT 'usp_Insights_FreeMonthly_LoadFacts (v4, Detailed Report export parity) installed';
GO
