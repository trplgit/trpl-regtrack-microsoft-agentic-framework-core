/*===========================================================================
  RegTrack Insights - Free tier, MONTHLY edition
  VALIDATION SCRIPT (UAT) - for sql/34 to sql/41

  INSTALL ORDER : 34, 35, 36, 37, 38, 39, 40, 41 - then run this file.
                  All eight create NEW objects only (usp_Insights_FreeMonthly_*).
                  No existing table, view, function or procedure is altered.
  WHO RUNS THIS : Vinay, after installing sql/34 to sql/41 on UAT.
  WHAT IT DOES  : Runs the monthly loaders and the Overview proc across the
                  tenant profiles CLAUDE.md Sec.11 requires, and REPORTS -
                  it never THROWs on data (CLAUDE.md Sec.11: a data-sanity
                  check warns; only structural invariants throw, and those
                  live inside the procs).
  CHANGES DATA  : NO. Read-only. Creates only session temp tables.
  RESULT        : one summary grid at the end. Every row must read PASS or
                  a WARN you have looked at. Any ERROR = the proc threw;
                  the message says which invariant.

  Runs against the free-tier tenants listed in #tenants below. CLAUDE.md
  Sec.10/11 asks for 5+ tenants of different profiles (single branch, large,
  empty peer sample ...) before calling sql/34-41 validated - add rows there
  as more tenants join the free tier on UAT.

  PURE ASCII. Target: SQL Server (vitComplianceSystem), UAT.
===========================================================================*/

SET NOCOUNT ON;

/*  [RE-RUN SAFETY] Drop this script's temp tables in their OWN batch first.
    SQL Server compiles a whole batch before running it, so a #tenants left
    over from an earlier run in the same window (with a different shape) is
    what the INSERT below would be checked against - Msg 213 - even though the
    DROP inside that batch would have removed it. The GO makes the drop happen
    before the main batch is compiled.                                       */
IF OBJECT_ID('tempdb..#tenants') IS NOT NULL DROP TABLE #tenants;
IF OBJECT_ID('tempdb..#report')  IS NOT NULL DROP TABLE #report;
IF OBJECT_ID('tempdb..#inst')    IS NOT NULL DROP TABLE #inst;
IF OBJECT_ID('tempdb..#sched')   IS NOT NULL DROP TABLE #sched;
IF OBJECT_ID('tempdb..#weekly')  IS NOT NULL DROP TABLE #weekly;
GO

SET NOCOUNT ON;

/*---------------------------------------------------------------------------
  0. INPUTS - the edition being simulated. Use the CURRENT month so that
     @AsOf is a real "now". @AsOf must be in the same clock as ScheduleOn.
---------------------------------------------------------------------------*/
DECLARE @CurrMonthStart DATE     = DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1);
DECLARE @AsOf           DATETIME = GETDATE();

/*  Tenants - the ones subscribed to the free tier (Product 18) on UAT.
    UserID: the recipient to run as. NULL = pick the first management-role
    user with entity scope on that tenant (same kind of user the digest
    orchestrator passes). Add a row per tenant to widen the run.           */
IF OBJECT_ID('tempdb..#tenants') IS NOT NULL DROP TABLE #tenants;
CREATE TABLE #tenants (CustomerID INT PRIMARY KEY, UserID INT NULL, Profile VARCHAR(60));
INSERT #tenants VALUES
 (5,    36,   'most activity - 19 sites'),
 (1285, NULL, 'most liability-bearing work - 13 sites'),
 (1355, NULL, 'mid-size - 15 sites'),
 (23,   NULL, 'volume, but no liability-bearing work');

IF OBJECT_ID('tempdb..#report') IS NOT NULL DROP TABLE #report;
CREATE TABLE #report (
    CustomerID  INT,
    UserID      INT NULL,
    CheckName   VARCHAR(60),
    Verdict     VARCHAR(8),        -- PASS | WARN | ERROR | SKIP
    Detail      NVARCHAR(MAX)
);

/*---------------------------------------------------------------------------
  1. OBJECTS INSTALLED + ENCODING (CLAUDE.md Sec.5a - must be zero rows)
---------------------------------------------------------------------------*/
INSERT #report (CustomerID, CheckName, Verdict, Detail)
SELECT 0, 'object_installed:' + n.name,
       CASE WHEN OBJECT_ID('dbo.' + n.name, 'P') IS NOT NULL THEN 'PASS' ELSE 'ERROR' END,
       CASE WHEN OBJECT_ID('dbo.' + n.name, 'P') IS NOT NULL THEN N'present' ELSE N'MISSING - install failed or was not run' END
FROM (VALUES ('usp_Insights_FreeMonthly_LoadFacts'),
             ('usp_Insights_FreeMonthly_LoadLicences'),
             ('usp_Insights_FreeMonthly_Overview'),
             ('usp_Insights_FreeMonthly_MemberDetectors'),
             ('usp_Insights_FreeMonthly_Users'),
             ('usp_Insights_FreeMonthly_Location'),
             ('usp_Insights_FreeMonthly_Act'),
             ('usp_Insights_FreeMonthly_Licence')) AS n(name);

INSERT #report (CustomerID, CheckName, Verdict, Detail)
SELECT 0, 'encoding_non_ascii:' + o.name, 'ERROR',
       N'Stored definition contains a non-ASCII character - the deployment path corrupted it. Re-deploy from source.'
FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id
WHERE o.name LIKE 'usp_Insights_FreeMonthly%'
  AND m.definition COLLATE Latin1_General_BIN2
      LIKE N'%[^ -~' + NCHAR(9) + NCHAR(10) + NCHAR(13) + N']%';

/*---------------------------------------------------------------------------
  2. PER TENANT
---------------------------------------------------------------------------*/
DECLARE @c INT, @u INT, @t0 DATETIME2, @ms INT, @err NVARCHAR(4000);

DECLARE tenant_cur CURSOR LOCAL FAST_FORWARD FOR SELECT CustomerID FROM #tenants ORDER BY CustomerID;
OPEN tenant_cur;
FETCH NEXT FROM tenant_cur INTO @c;

WHILE @@FETCH_STATUS = 0
BEGIN
    /*  Representative user: the one listed for this tenant, otherwise a real
        management-role recipient who HAS scope - the same kind of user the
        orchestrator passes.                                                  */
    SET @u = (SELECT UserID FROM #tenants WHERE CustomerID = @c);
    IF @u IS NULL
    SELECT TOP 1 @u = m.UserID
    FROM dbo.tvfInsightsManagementUsers(@c) m
    WHERE EXISTS (SELECT 1 FROM EntitiesAssignment ea
                  JOIN CustomerBranch cb ON cb.ID = ea.BranchID
                  WHERE ea.UserID = m.UserID AND cb.CustomerID = @c
                    AND cb.IsDeleted = 0 AND cb.Status = 1)
    ORDER BY m.UserID;

    IF @u IS NULL
    BEGIN
        INSERT #report VALUES (@c, NULL, 'representative_user', 'SKIP',
            N'No management-role user with entity scope on this tenant in UAT - set a UserID for it in #tenants.');
        FETCH NEXT FROM tenant_cur INTO @c;
        CONTINUE;
    END

    /*-- 2a. Load facts directly, so the estate can be cross-checked ---------*/
    IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst;
    CREATE TABLE #inst (
        ComplianceInstanceID BIGINT NOT NULL PRIMARY KEY, BranchID INT NOT NULL, CategoryId INT NULL,
        ActID INT NULL, ComplianceID BIGINT NOT NULL, DepartmentID BIGINT NULL, Imprisonment BIT NOT NULL,
        RiskType INT NULL, RiskClass NVARCHAR(200) NULL);
    IF OBJECT_ID('tempdb..#sched') IS NOT NULL DROP TABLE #sched;
    CREATE TABLE #sched (
        ComplianceScheduleOnID BIGINT NOT NULL PRIMARY KEY, ComplianceInstanceID BIGINT NOT NULL,
        BranchID INT NOT NULL, ScheduleOn DATETIME NOT NULL, WindowPart VARCHAR(14) NULL,
        PerformerID BIGINT NULL, PerformerSource VARCHAR(10) NOT NULL, ReviewerID BIGINT NULL,
        ReviewerSource VARCHAR(10) NOT NULL, NeverTouched BIT NOT NULL, Outcome VARCHAR(20) NOT NULL,
        IsOverdue BIT NOT NULL, DaysPastDue INT NULL, AgeBand VARCHAR(10) NULL);

    BEGIN TRY
        SET @t0 = SYSDATETIME();
        EXEC dbo.usp_Insights_FreeMonthly_LoadFacts @u, @c, @CurrMonthStart, @AsOf;
        SET @ms = DATEDIFF(MILLISECOND, @t0, SYSDATETIME());

        INSERT #report VALUES (@c, @u, 'load_facts_ms',
            CASE WHEN @ms <= 30000 THEN 'PASS' ELSE 'WARN' END,
            CONCAT(N'LoadFacts took ', @ms, N' ms; ', (SELECT COUNT(*) FROM #inst), N' instances, ',
                   (SELECT COUNT(*) FROM #sched), N' schedules loaded.'));

        /*  ESTATE EQUIVALENCE 1 - overdue stock must equal the CANONICAL
            overdue function over the same scope. sql/34 restates the predicate
            (one read, one instant); this proves the restatement is exact.
            A small difference can be a status change between the two reads;
            re-run before treating it as a defect.                            */
        DECLARE @mine INT = (SELECT COUNT(*) FROM #sched WHERE IsOverdue = 1);
        DECLARE @canon INT = (SELECT COUNT(*)
                              FROM dbo.tvfInsightsOverdueSchedules(@c, @AsOf) o
                              JOIN #inst i ON i.ComplianceInstanceID = o.ComplianceInstanceID);
        INSERT #report VALUES (@c, @u, 'overdue_equals_canonical',
            CASE WHEN @mine = @canon THEN 'PASS' ELSE 'WARN' END,
            CONCAT(N'monthly loader ', @mine, N' vs tvfInsightsOverdueSchedules ', @canon,
                   CASE WHEN @mine = @canon THEN N'' ELSE N' - re-run; if it persists, the restated predicate has drifted.' END));

        /*  ESTATE EQUIVALENCE 2 - the scope is UNCHANGED from today's free
            email: instance count must equal sql/06 TotalActiveObligations for
            the same user.                                                    */
        IF OBJECT_ID('tempdb..#weekly') IS NOT NULL DROP TABLE #weekly;
        CREATE TABLE #weekly (
            CustomerID INT, GeneratedAt DATETIME, TotalActiveObligations INT, DueNext7 INT,
            CriticalDueNext7 INT, ImprisonmentDueNext7 INT, BranchesInScope INT, DueNext30 INT,
            ImprisonmentDueNext30 INT, LicencesLapsingNext30 INT, CriticalDueNext30 INT,
            CompletedLast7 INT, DueNext14 INT, DistinctImprisonmentObligations INT, BranchesWithUpcoming INT);
        INSERT #weekly EXEC dbo.usp_Insights_FreeDigestAggregates @CustomerID = @c, @UserID = @u, @AsOf = @AsOf;

        DECLARE @weeklyObl INT = (SELECT TOP 1 TotalActiveObligations FROM #weekly);
        DECLARE @monthlyObl INT = (SELECT COUNT(*) FROM #inst);
        INSERT #report VALUES (@c, @u, 'scope_equals_weekly_digest',
            CASE WHEN @weeklyObl = @monthlyObl THEN 'PASS' ELSE 'WARN' END,
            CONCAT(N'monthly ', @monthlyObl, N' obligations vs weekly sql/06 ', @weeklyObl,
                   CASE WHEN @weeklyObl = @monthlyObl THEN N'' ELSE N' - a difference means the scope drifted; investigate before shipping.' END));

        /*  Ownership sanity - schedule-first must leave almost nothing
            ownerless (CLAUDE.md Sec.5: 99.8% of schedules carry a performer).  */
        DECLARE @noOwner INT = (SELECT COUNT(*) FROM #sched WHERE PerformerSource = 'none');
        DECLARE @allS INT = (SELECT COUNT(*) FROM #sched);
        INSERT #report VALUES (@c, @u, 'ownerless_share',
            CASE WHEN @allS = 0 THEN 'PASS' WHEN 100.0 * @noOwner / @allS <= 2.0 THEN 'PASS' ELSE 'WARN' END,
            CONCAT(@noOwner, N' of ', @allS, N' loaded schedules have no owner anywhere. Expected well under 2%; a large share suggests the schedule-first read is not working.'));
    END TRY
    BEGIN CATCH
        SET @err = CONCAT(N'Error ', ERROR_NUMBER(), N': ', ERROR_MESSAGE());
        INSERT #report VALUES (@c, @u, 'load_facts', 'ERROR', @err);
    END CATCH

    /*-- 2b. Every slot proc end to end - each must return 6 result sets:
           control_totals, facts, detector_policy, candidates, data_quality,
           examples (added 2026-09-23, always LAST; empty is normal).
           What to eyeball in the grids above:
             - at most 2 candidate rows carry a DefaultSlot (1 and 2)
             - examples: every row's Detector shows EmitMode = 'aggregate' in
               detector_policy; no Detector appears in BOTH candidates and
               examples; at most 3 rows per Detector; no NULL or blank
               EntityLabel; NEVER a ghost_location row; no person rows when
               @AllowPersonNames = 0; ItemCount <= BaseCount where both set;
               every PatternFactKey exists in facts with its _of partner
             - Overview: exactly one fact with IsHeadline = 1
             - other slots: HeadlineSource = candidate, or exactly one
               IsHeadline fact
             - a single-branch tenant: Location detectors say "suppressed",
               no location candidates
             - a small tenant: Notes may mention degraded_peer_sample
             - a large tenant: timings below                               */
    DECLARE @slot VARCHAR(20);
    DECLARE slot_cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT s FROM (VALUES ('overview'), ('users'), ('location'), ('act'), ('licence')) v(s);
    OPEN slot_cur;
    FETCH NEXT FROM slot_cur INTO @slot;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            SET @t0 = SYSDATETIME();
            IF @slot = 'overview' EXEC dbo.usp_Insights_FreeMonthly_Overview @u, @c, @CurrMonthStart, @AsOf;
            IF @slot = 'users'    EXEC dbo.usp_Insights_FreeMonthly_Users    @u, @c, @CurrMonthStart, @AsOf;
            IF @slot = 'location' EXEC dbo.usp_Insights_FreeMonthly_Location @u, @c, @CurrMonthStart, @AsOf;
            IF @slot = 'act'      EXEC dbo.usp_Insights_FreeMonthly_Act      @u, @c, @CurrMonthStart, @AsOf;
            IF @slot = 'licence'  EXEC dbo.usp_Insights_FreeMonthly_Licence  @u, @c, @CurrMonthStart, @AsOf;
            SET @ms = DATEDIFF(MILLISECOND, @t0, SYSDATETIME());
            INSERT #report VALUES (@c, @u, 'slot_ran:' + @slot,
                CASE WHEN @ms <= 60000 THEN 'PASS' ELSE 'WARN' END,
                CONCAT(@slot, N' completed in ', @ms, N' ms.'));
        END TRY
        BEGIN CATCH
            SET @err = CONCAT(N'Error ', ERROR_NUMBER(), N': ', ERROR_MESSAGE());
            INSERT #report VALUES (@c, @u, 'slot_ran:' + @slot, 'ERROR', @err);
        END CATCH
        FETCH NEXT FROM slot_cur INTO @slot;
    END
    CLOSE slot_cur;
    DEALLOCATE slot_cur;

    /*  [ORDER MATTERS] Each slot proc starts with
        "IF OBJECT_ID('tempdb..#inst') IS NOT NULL DROP TABLE #inst", which finds
        and DROPS this script's own #inst / #sched (a nested proc sees its
        caller's temp tables). They are recreated at the top of the next tenant
        loop. So any check that reads #inst or #sched MUST sit above the slot
        loop - one added here fails with Msg 208 (invalid object name).       */

    FETCH NEXT FROM tenant_cur INTO @c;
END

CLOSE tenant_cur;
DEALLOCATE tenant_cur;

/*---------------------------------------------------------------------------
  3. NEGATIVE TESTS - the input contract must refuse, loudly
---------------------------------------------------------------------------*/
DECLARE @anyC INT, @anyU INT;
SELECT TOP 1 @anyC = r.CustomerID, @anyU = r.UserID FROM #report r WHERE r.UserID IS NOT NULL;

IF @anyC IS NOT NULL
BEGIN
    /*  Clock mismatch: an @AsOf before the edition month must THROW 51237. */
    BEGIN TRY
        DECLARE @badAsOf DATETIME = DATEADD(HOUR, -6, CAST(@CurrMonthStart AS DATETIME));
        EXEC dbo.usp_Insights_FreeMonthly_Overview @anyU, @anyC, @CurrMonthStart, @badAsOf;
        INSERT #report VALUES (@anyC, @anyU, 'negative_clock_mismatch', 'ERROR', N'Did NOT throw - a UTC-vs-local mismatch would be silently accepted.');
    END TRY
    BEGIN CATCH
        INSERT #report VALUES (@anyC, @anyU, 'negative_clock_mismatch',
            CASE WHEN ERROR_NUMBER() = 51237 THEN 'PASS' ELSE 'ERROR' END,
            CONCAT(N'Threw ', ERROR_NUMBER(), N' (expected 51237).'));
    END CATCH

    /*  Not-the-first-of-month: must THROW 51236.                             */
    BEGIN TRY
        DECLARE @badStart DATE = DATEADD(DAY, 1, @CurrMonthStart);
        EXEC dbo.usp_Insights_FreeMonthly_Overview @anyU, @anyC, @badStart, @AsOf;
        INSERT #report VALUES (@anyC, @anyU, 'negative_month_start', 'ERROR', N'Did NOT throw on a month start that is not the 1st.');
    END TRY
    BEGIN CATCH
        INSERT #report VALUES (@anyC, @anyU, 'negative_month_start',
            CASE WHEN ERROR_NUMBER() = 51236 THEN 'PASS' ELSE 'ERROR' END,
            CONCAT(N'Threw ', ERROR_NUMBER(), N' (expected 51236).'));
    END CATCH

    /*  No scope: user 0 has no pairs - must THROW 51230.                      */
    BEGIN TRY
        EXEC dbo.usp_Insights_FreeMonthly_Overview 0, @anyC, @CurrMonthStart, @AsOf;
        INSERT #report VALUES (@anyC, 0, 'negative_no_scope', 'ERROR', N'Did NOT throw for a user with no scope.');
    END TRY
    BEGIN CATCH
        INSERT #report VALUES (@anyC, 0, 'negative_no_scope',
            CASE WHEN ERROR_NUMBER() = 51230 THEN 'PASS' ELSE 'ERROR' END,
            CONCAT(N'Threw ', ERROR_NUMBER(), N' (expected 51230).'));
    END CATCH
END

/*---------------------------------------------------------------------------
  4. SUMMARY - read this grid. Everything else above is detail.
---------------------------------------------------------------------------*/
SELECT r.CustomerID, t.Profile, r.UserID, r.CheckName, r.Verdict, r.Detail
FROM #report r
LEFT JOIN #tenants t ON t.CustomerID = r.CustomerID
ORDER BY CASE r.Verdict WHEN 'ERROR' THEN 0 WHEN 'WARN' THEN 1 WHEN 'SKIP' THEN 2 ELSE 3 END,
         r.CustomerID, r.CheckName;
