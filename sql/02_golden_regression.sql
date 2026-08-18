/*═══════════════════════════════════════════════════════════════════════════
  RegTrack Insights — Phase 1a, Step 2
  GOLDEN-DATASET REGRESSION SUITE

  Spec reference : RegTrack_Insights_System_Design_v1.md §6.6
  Purpose        : Catch a dictionary or procedure edit that silently moves a
                   status across buckets, BEFORE it ships.

  ── DESIGN REFINEMENT (important, supersedes the simpler §6.6 sketch) ───────
  Overdue is a FLOW metric and drifts continuously against live production
  (observed: 1,387 → 1,398 → 1,116 on identical SQL within one session).
  Therefore a regression test CANNOT assert an absolute overdue count against
  production — it would fail randomly and be switched off, which is worse than
  no test at all.

  Two tiers instead:

    TIER 1 — PRODUCTION INVARIANTS (this file, §A)
             Relationship assertions that hold regardless of drift, because
             both sides of the equation move together. Run against live prod.

    TIER 2 — FROZEN FIXTURES (this file, §B — seed script for a test database)
             Deterministic rows with hand-verified expected values. Run in CI.

  Run TIER 1 nightly against production and in CI before any dictionary change.
  Run TIER 2 on every build.
═══════════════════════════════════════════════════════════════════════════*/

SET NOCOUNT ON;
GO

/*═══════════════════════════════════════════════════════════════════════════
  §A — TIER 1: PRODUCTION INVARIANTS (drift-proof)
═══════════════════════════════════════════════════════════════════════════*/

IF OBJECT_ID('dbo.usp_Insights_GoldenInvariants', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_GoldenInvariants;
GO
CREATE PROCEDURE dbo.usp_Insights_GoldenInvariants
    @CustomerID INT,
    @AsOf       DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    DECLARE @results TABLE (
        TestId      VARCHAR(10),
        TestName    NVARCHAR(120),
        Passed      BIT,
        Detail      NVARCHAR(400)
    );

    /*───────────────────────────────────────────────────────────────────────
      G-1  DICTIONARY COVERAGE
           Every ComplianceStatus in the system is mapped. Fail closed.
    ───────────────────────────────────────────────────────────────────────*/
    DECLARE @unmapped INT = (
        SELECT COUNT(*) FROM ComplianceStatus cs
        WHERE NOT EXISTS (SELECT 1 FROM dbo.vInsightsStatusCurrent d WHERE d.StatusId = cs.ID)
    );
    INSERT @results VALUES ('G-1', N'All ComplianceStatus values are mapped',
        CASE WHEN @unmapped = 0 THEN 1 ELSE 0 END,
        CONCAT(N'Unmapped status count = ', @unmapped, N' (must be 0)'));

    /*───────────────────────────────────────────────────────────────────────
      G-2  OVERDUE DEFINITION INVARIANT  ★ the central test ★

           overdue_NEW = overdue_OLD - pastdue(7,9) - pastdue(17) + pastdue(18)

           WHY this is the right assertion: it is drift-proof. Every term is
           measured in the same instant, so they move together. It pins the
           EXACT semantic difference between the old (wrong) exclusionary
           definition `NOT IN (4,5,15,18)` and the dictionary-driven one:
             • 7,9  = completed items the OLD form wrongly counted as overdue
             • 17   = not-complied-by-REVIEWER (resolved_terminal), wrongly
                      counted as overdue by the OLD form
             • 18   = not-applicable-by-PERFORMER (proposed ⇒ open), wrongly
                      EXCLUDED by the OLD form

           ── HOW THIS TEST WAS ITSELF CAUGHT BEING WRONG ────────────────────
           The first version of this invariant omitted the 17 term. It PASSED
           on four production tenants and FAILED on the fifth, which had 416
           past-due schedules in status 17 while the others had zero.

           That is precisely the failure mode this whole dictionary exists to
           prevent: an incomplete enumeration that is correct BY LUCK on the
           data you happen to look at first. Always validate an invariant
           across several tenants with DIFFERENT status profiles.

           Verified across 5 production tenants at design time, e.g.:
             tenant A: 3,558 − 784 − 0   + 46  = 2,820  ✓ (old OVERstated by 738)
             tenant E: 1,238 − 0   − 416 + 16  =   838  ✓ (old OVERstated by 400)
             tenant D: 24,232 − 0  − 0   + 391 = 24,623 ✓ (old UNDERstated by 391)

           Note the old form erred in BOTH directions depending on tenant.
    ───────────────────────────────────────────────────────────────────────*/
    DECLARE @old INT, @new INT, @c79 INT, @c17 INT, @c18 INT;

    SELECT
        @old = SUM(CASE WHEN rct.ComplianceStatusID NOT IN (4,5,15,18) THEN 1 ELSE 0 END),
        @new = SUM(CASE WHEN d.OverdueEligible = 1 THEN 1 ELSE 0 END),
        @c79 = SUM(CASE WHEN rct.ComplianceStatusID IN (7,9) THEN 1 ELSE 0 END),
        @c17 = SUM(CASE WHEN rct.ComplianceStatusID = 17 THEN 1 ELSE 0 END),
        @c18 = SUM(CASE WHEN rct.ComplianceStatusID = 18 THEN 1 ELSE 0 END)
    FROM ComplianceScheduleOn cso
    JOIN ComplianceInstance i ON i.ID = cso.ComplianceInstanceID
    JOIN CustomerBranch cb    ON cb.ID = i.CustomerBranchID
    JOIN RecentComplianceTransactionView rct ON rct.ComplianceScheduleOnID = cso.ID
    LEFT JOIN dbo.vInsightsStatusCurrent d   ON d.StatusId = rct.ComplianceStatusID
    WHERE cb.CustomerID = @CustomerID
      AND cb.IsDeleted = 0 AND i.IsDeleted = 0
      AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn <= @AsOf;

    /*  [TRAP] SUM() over zero rows returns NULL, not 0 — a tenant with no
        past-due schedules at all would make every term above NULL, and
        NULL = NULL - NULL ... evaluates to UNKNOWN, which INSERT/CASE treats
        as FALSE. That reads as a regression FAILURE on a tenant that simply
        has nothing to test — the exact "zero obligations, cannot assess"
        boundary case this project's own rules require handling everywhere.
        ISNULL-wrap every term so an empty tenant vacuously PASSES, matching
        how G-5/G-7/G-8/G-9 already handle their own zero-row cases.        */
    SET @old = ISNULL(@old,0); SET @new = ISNULL(@new,0);
    SET @c79 = ISNULL(@c79,0); SET @c17 = ISNULL(@c17,0); SET @c18 = ISNULL(@c18,0);

    INSERT @results VALUES ('G-2', N'Overdue definition invariant (old→new reconciles)',
        CASE WHEN @new = @old - @c79 - @c17 + @c18 THEN 1 ELSE 0 END,
        CONCAT(N'new=', @new, N' old=', @old, N' pastdue(7,9)=', @c79,
               N' pastdue(17)=', @c17, N' pastdue(18)=', @c18,
               N' | expected new=', @old - @c79 - @c17 + @c18));

    /*───────────────────────────────────────────────────────────────────────
      G-3  COMPLETED ITEMS ARE NEVER OVERDUE
           No schedule may be both closure_class='completed' and overdue.
    ───────────────────────────────────────────────────────────────────────*/
    DECLARE @completed_overdue INT = (
        SELECT COUNT(*)
        FROM dbo.tvfInsightsOverdueSchedules(@CustomerID, @AsOf) o
        JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = o.StatusId
        WHERE d.ClosureClass = 'completed'
    );
    INSERT @results VALUES ('G-3', N'No completed item is counted overdue',
        CASE WHEN @completed_overdue = 0 THEN 1 ELSE 0 END,
        CONCAT(N'Completed-but-overdue rows = ', @completed_overdue, N' (must be 0)'));

    /*───────────────────────────────────────────────────────────────────────
      G-4  RESOLVED-TERMINAL EXCLUDED FROM THE ON-TIME DENOMINATOR
           Statuses 15/17 (NA and not-complied, reviewer-final) must never
           enter the timeliness denominator. ~1.1M NA schedules exist
           system-wide; counting them would inflate every tenant's on-time%.
    ───────────────────────────────────────────────────────────────────────*/
    DECLARE @terminal_with_timeliness INT = (
        SELECT COUNT(*) FROM dbo.vInsightsStatusCurrent
        WHERE ClosureClass = 'resolved_terminal' AND Timeliness IS NOT NULL
    );
    INSERT @results VALUES ('G-4', N'resolved_terminal carries no timeliness',
        CASE WHEN @terminal_with_timeliness = 0 THEN 1 ELSE 0 END,
        CONCAT(N'resolved_terminal rows with timeliness = ', @terminal_with_timeliness, N' (must be 0)'));

    /*───────────────────────────────────────────────────────────────────────
      G-5  RECURSIVE ENTITY ROLLUP TIES TO THE TENANT CONTROL TOTAL
           [TRAP] Instances live on INTERMEDIATE nodes too. A leaf-only rollup
           silently drops them — observed at 211 instances on one tenant's
           non-leaf node and 141 on another's. This test is the tripwire.
    ───────────────────────────────────────────────────────────────────────*/
    DECLARE @control INT, @rollup INT;

    SELECT @control = COUNT(*)
    FROM ComplianceInstance i
    JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND i.IsDeleted = 0;

    ;WITH tree AS (
        SELECT ID, ParentID FROM CustomerBranch
        WHERE CustomerID = @CustomerID AND IsDeleted = 0 AND ParentID IS NULL
        UNION ALL
        SELECT c.ID, c.ParentID FROM CustomerBranch c
        JOIN tree t ON c.ParentID = t.ID
        WHERE c.CustomerID = @CustomerID AND c.IsDeleted = 0
    )
    SELECT @rollup = COUNT(i.ID)
    FROM tree t
    LEFT JOIN ComplianceInstance i ON i.CustomerBranchID = t.ID AND i.IsDeleted = 0;

    INSERT @results VALUES ('G-5', N'Recursive rollup ties to tenant control total',
        CASE WHEN @control = @rollup THEN 1 ELSE 0 END,
        CONCAT(N'control=', @control, N' rollup=', @rollup,
               N' | gap=', @control - @rollup, N' (a gap means a node was dropped)'));

    /*───────────────────────────────────────────────────────────────────────
      G-6  RISK MAPPING SANITY
           RiskType 3 must carry the overwhelming majority of imprisonment-
           bearing compliances. If this inverts, the mapping has been edited
           wrongly (design-time measurement: 1,419 of 1,424 = 99.6%).

           [TRAP — found empirically, 2026-08-18] The 90% figure is a SYSTEM-
           WIDE statistic (it describes the RiskType↔Imprisonment MAPPING
           itself, a dictionary-level fact — not any one tenant's instance
           mix). The original version of this test applied it PER TENANT and
           broke on every real tenant checked: 1490 (2/122), 5 (20/1446),
           29 (38/888) — all genuine, all failing, none a data problem. A
           small tenant's own imprisonment items can easily skew away from
           the system-wide average by chance; that says nothing about
           whether the mapping itself is sane. Test the mapping the way it
           was actually measured — system-wide, independent of @CustomerID —
           same principle as the peer-relative rule used everywhere else in
           this project (never an absolute per-tenant threshold).
    ───────────────────────────────────────────────────────────────────────*/
    /*  DECISION 2026-08-18: kept PER TENANT, instance-weighted.

        A system-wide variant over the Compliance master was trialled. It does not
        resolve the failure - it makes it larger (0.09% system-wide vs 1.4-4.3%
        per tenant), because it applies a threshold that was measured
        instance-weighted on ONE production tenant (1,419 of 1,424) to a different
        population entirely. That is the "correct by luck on the data you measured"
        error this project documents four times over.

        This assertion is therefore restored to the form in which the 99.6% figure
        was actually validated. It is EXPECTED TO FAIL on this database, and that
        failure is the finding: imprisonment-bearing compliances here are NOT
        RiskType 3. Resolve by correcting the data or the InsightsEnumPolarity seed
        in sql/01 (with a BA ruling) - never by re-scoping this test until it reads
        green. See CLAUDE.md 13 and docs/GOLDEN_FIXTURES.md.                        */
    /*  The RiskType value meaning Critical is READ FROM THE DICTIONARY, never
        hardcoded. A literal `RiskType = 3` here would be an enum literal in a
        WHERE clause - non-negotiable #4. It would also make this test agree with
        a wrong seed by construction, which defeats its entire purpose.

        The test also reports the value the DATA actually favours, so a failure
        names the correct mapping instead of just saying "no".                    */
    DECLARE @criticalRisk INT = (
        SELECT TRY_CAST(p.RawValue AS INT)
        FROM dbo.InsightsEnumPolarity p
        JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
        WHERE p.Semantic = 'RiskType' AND p.Meaning LIKE N'Critical%');

    DECLARE @imp_total INT, @imp_crit INT, @imp_topValue INT, @imp_topCount INT;

    SELECT @imp_total = COUNT(*),
           @imp_crit  = SUM(CASE WHEN c.RiskType = @criticalRisk THEN 1 ELSE 0 END)
    FROM ComplianceInstance i
    JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID
    JOIN Compliance c      ON c.ID = i.ComplianceID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0
      AND i.IsDeleted = 0 AND c.IsDeleted = 0 AND c.Imprisonment = 1;

    -- which RiskType do imprisonment items ACTUALLY concentrate on for this tenant?
    SELECT TOP 1 @imp_topValue = c.RiskType, @imp_topCount = COUNT(*)
    FROM ComplianceInstance i
    JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID
    JOIN Compliance c      ON c.ID = i.ComplianceID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0
      AND i.IsDeleted = 0 AND c.IsDeleted = 0 AND c.Imprisonment = 1
    GROUP BY c.RiskType
    ORDER BY COUNT(*) DESC;

    INSERT @results VALUES ('G-6', N'Dictionary Critical RiskType carries imprisonment items',
        CASE WHEN ISNULL(@imp_total,0) = 0
                  OR (@imp_crit * 100.0 / @imp_total) >= 90.0 THEN 1 ELSE 0 END,
        CONCAT(N'dictionary Critical = RiskType ', ISNULL(@criticalRisk,-1),
               N' -> ', ISNULL(@imp_crit,0), N'/', ISNULL(@imp_total,0),
               N' imprisonment items (expect >=90%). Data favours RiskType ',
               ISNULL(@imp_topValue,-1), N' with ', ISNULL(@imp_topCount,0),
               N'. If these disagree, the InsightsEnumPolarity seed in sql/01 is wrong.'));

    /*───────────────────────────────────────────────────────────────────────
      G-7  SCOPE IS TWO-DIMENSIONAL
           Every EntitiesAssignment row must carry a category. If category is
           ever null/zero, branch-only filtering would leak across categories
           WITHIN the tenant.
    ───────────────────────────────────────────────────────────────────────*/
    DECLARE @scope_rows INT, @scope_no_cat INT;
    SELECT @scope_rows   = COUNT(*),
           @scope_no_cat = SUM(CASE WHEN ea.ComplianceCatagoryID IS NULL
                                     OR ea.ComplianceCatagoryID = 0 THEN 1 ELSE 0 END)
    FROM EntitiesAssignment ea
    JOIN CustomerBranch cb ON cb.ID = ea.BranchID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0;

    INSERT @results VALUES ('G-7', N'All scope rows are category-specific (2-D)',
        CASE WHEN @scope_rows = 0 OR @scope_no_cat = 0 THEN 1 ELSE 0 END,
        CONCAT(N'scope rows=', @scope_rows, N' without category=', @scope_no_cat, N' (must be 0)'));

    /*───────────────────────────────────────────────────────────────────────
      G-8  2-D SCOPE CONSTRAINT IS ACTUALLY PROTECTIVE
           Compares correct 2-D scoping against branch-only scoping and reports
           how many instances branch-only would have leaked.

           This test does not FAIL when leakage is 0 — some tenants legitimately
           have non-restrictive categories. It fails only if 2-D returns MORE
           than branch-only, which would mean the constraint is inverted.

           ── WHY THIS MATTERS ───────────────────────────────────────────────
           Measured across production: branch-only scoping would have leaked
             • 119,797 instances across 81 users on one tenant
             •  31,659 instances to a SINGLE user on another
           The first tenant checked during design showed ZERO leakage, which
           almost led to the category axis being treated as unimportant.
           Third instance in this project of "correct by luck on one tenant".
    ───────────────────────────────────────────────────────────────────────*/
    DECLARE @scoped2D INT, @branchOnly INT;

    ;WITH pairs AS (
        SELECT DISTINCT ea.UserID, ea.BranchID, ea.ComplianceCatagoryID AS CategoryId
        FROM EntitiesAssignment ea
        JOIN CustomerBranch cb ON cb.ID = ea.BranchID
        WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0
    ), inst AS (
        SELECT i.ID, i.CustomerBranchID AS BranchID, a.ComplianceCategoryId AS CategoryId
        FROM ComplianceInstance i
        JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID AND cb.IsDeleted = 0
        JOIN Compliance c      ON c.ID  = i.ComplianceID     AND c.IsDeleted  = 0
        JOIN Act a             ON a.ID  = c.ActID
        WHERE cb.CustomerID = @CustomerID AND i.IsDeleted = 0
    )
    SELECT
        @scoped2D = (SELECT COUNT(DISTINCT i.ID) FROM pairs p
                     JOIN inst i ON i.BranchID = p.BranchID AND i.CategoryId = p.CategoryId),
        @branchOnly = (SELECT COUNT(DISTINCT i.ID)
                       FROM (SELECT DISTINCT BranchID FROM pairs) p
                       JOIN inst i ON i.BranchID = p.BranchID);

    INSERT @results VALUES ('G-8', N'2-D scope never returns more than branch-only',
        CASE WHEN ISNULL(@scoped2D,0) <= ISNULL(@branchOnly,0) THEN 1 ELSE 0 END,
        CONCAT(N'2-D=', ISNULL(@scoped2D,0), N' branch-only=', ISNULL(@branchOnly,0),
               N' | branch-only would leak ', ISNULL(@branchOnly,0) - ISNULL(@scoped2D,0),
               N' instances'));


    /*───────────────────────────────────────────────────────────────────────
      G-9  UNKNOWN-STATUS ROWS ARE DECLARED, NOT SILENTLY DROPPED
           NULL ComplianceStatusID exists in production (10 rows system-wide,
           9 of them past-due and active, across 5 tenants). The overdue TVF
           inner-joins the dictionary, so these are excluded — safe, but silent.
           This test makes the count VISIBLE so it can be declared on the
           report as a data_quality entry (see usp_Insights_StatusDataQuality).

           Fails only if the unknown volume is material (>0.10% of past-due),
           which would make the overdue figure unreliable rather than merely
           imperfect.
    ───────────────────────────────────────────────────────────────────────*/
    DECLARE @pastdueAll INT, @nullStatus INT;
    SELECT @pastdueAll = COUNT(*),
           @nullStatus = SUM(CASE WHEN rct.ComplianceStatusID IS NULL THEN 1 ELSE 0 END)
    FROM ComplianceScheduleOn cso
    JOIN ComplianceInstance i ON i.ID = cso.ComplianceInstanceID
    JOIN CustomerBranch cb    ON cb.ID = i.CustomerBranchID
    JOIN RecentComplianceTransactionView rct ON rct.ComplianceScheduleOnID = cso.ID
    WHERE cb.CustomerID = @CustomerID AND cb.IsDeleted = 0 AND i.IsDeleted = 0
      AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn <= @AsOf;

    INSERT @results VALUES ('G-9', N'Unknown-status volume is immaterial and declared',
        CASE WHEN ISNULL(@pastdueAll,0) = 0
                  OR ISNULL(@nullStatus,0) * 100.0 / @pastdueAll <= 0.10 THEN 1 ELSE 0 END,
        CONCAT(N'NULL-status past-due rows = ', ISNULL(@nullStatus,0), N' of ',
               ISNULL(@pastdueAll,0), N' — MUST be declared as data_quality on the report'));

    /*── report ──────────────────────────────────────────────────────────*/
    SELECT
        TestId, TestName,
        CASE WHEN Passed = 1 THEN 'PASS' ELSE '*** FAIL ***' END AS Result,
        Detail
    FROM @results
    ORDER BY TestId;

    IF EXISTS (SELECT 1 FROM @results WHERE Passed = 0)
        THROW 51002, N'GOLDEN REGRESSION FAILED — do not ship. See result set for the failing assertion.', 1;
END
GO


/*═══════════════════════════════════════════════════════════════════════════
  §B — TIER 2: FROZEN FIXTURE DESIGN (for a CI test database)

  Tier 1 proves the definitions are self-consistent against whatever data
  exists. Tier 2 proves they produce the RIGHT ABSOLUTE NUMBERS, which needs
  data that never moves.

  Build a small seeded test database containing exactly the cases below, with
  hand-verified expected values. These are the cases that actually broke, or
  nearly broke, during design.

  ┌────┬──────────────────────────────────────────┬──────────────────────────┐
  │ F# │ Fixture                                   │ Expected                 │
  ├────┼──────────────────────────────────────────┼──────────────────────────┤
  │ F-1│ 10 past-due schedules in status 7         │ overdue = 0              │
  │    │ (completed BEFORE due date)               │ on_time completions = 10 │
  ├────┼──────────────────────────────────────────┼──────────────────────────┤
  │ F-2│ 10 past-due schedules in status 9         │ overdue = 0              │
  │    │ (completed AFTER due date)                │ delayed completions = 10 │
  │    │  ★ THE LUCKY-ESCAPE CASE — the reference   │                          │
  │    │    tenants had none of these, so the old   │                          │
  │    │    definition was right by luck            │                          │
  ├────┼──────────────────────────────────────────┼──────────────────────────┤
  │ F-3│ 10 past-due schedules in status 2         │ overdue = 10             │
  │    │ (complied, pending review)                │ completions = 0          │
  ├────┼──────────────────────────────────────────┼──────────────────────────┤
  │ F-4│ 10 past-due in 15, 10 past-due in 17      │ overdue = 0              │
  │    │ (reviewer-final NA / not-complied)        │ on-time DENOMINATOR      │
  │    │                                           │ excludes all 20          │
  ├────┼──────────────────────────────────────────┼──────────────────────────┤
  │ F-5│ 10 past-due in 18, 10 past-due in 16      │ overdue = 20             │
  │    │ (performer-proposed NA / not-complied)    │ (proposed ⇒ still open)  │
  ├────┼──────────────────────────────────────────┼──────────────────────────┤
  │ F-6│ Entity tree: apex → intermediate(holding  │ rollup = 25, NOT 15      │
  │    │ 10 instances) → 2 leaves (10 + 5)         │ ★ leaf-only rollup fails │
  ├────┼──────────────────────────────────────────┼──────────────────────────┤
  │ F-7│ A status ID present in data but ABSENT    │ usp_..AssertStatus       │
  │    │ from the dictionary                       │ Coverage THROWS 51001    │
  ├────┼──────────────────────────────────────────┼──────────────────────────┤
  │ F-8│ User with EA rows for branch B category C │ query for (B, other cat) │
  │    │ only                                      │ returns 0 rows           │
  ├────┼──────────────────────────────────────────┼──────────────────────────┤
  │ F-9│ Soft-deleted branch (IsDeleted=1) holding │ excluded from all cuts;  │
  │    │ 1 instance                                │ reported as orphan       │
  ├────┼──────────────────────────────────────────┼──────────────────────────┤
  │F-10│ Deactivated user (IsActive=0,IsDeleted=0) │ user APPEARS, flagged;   │
  │    │ holding 5 live assignments                │ NOT hidden               │
  └────┴──────────────────────────────────────────┴──────────────────────────┘

  Aggregate expected values for the fixture tenant:
      total past-due schedules      = 80
      overdue (dictionary)          = 40   (F-3: 10, F-5: 20, plus F-6 as configured)
      completed                     = 20   (F-1: 10 on_time, F-2: 10 delayed)
      resolved_terminal             = 20   (F-4)
      on-time %                     = 50.0 (10 of 20 completed — NOT 10 of 40)
      entity rollup                 = 25   (F-6)

  NOTE the on-time% assertion: 50% is only correct if resolved_terminal is
  excluded from the denominator. If a future edit lets 15/17 in, this becomes
  25% and the test fails — which is exactly the point.
═══════════════════════════════════════════════════════════════════════════*/

PRINT 'Golden regression suite installed. Run: EXEC dbo.usp_Insights_GoldenInvariants @CustomerID = <tenant>;';
GO
