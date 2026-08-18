/*═══════════════════════════════════════════════════════════════════════════
  RegTrack Insights — Phase 1c
  FREE WEEKLY DIGEST — entitlement gate + the ~15 aggregates

  Spec reference : RegTrack_Insights_System_Design_v1.md §10
  Purpose        : Product 18 (RegInsights Basic). A weekly email to every
                   entitled tenant's designated management users.

  ── THE COST INSIGHT THAT SHAPES THIS WHOLE DESIGN ─────────────────────────
  The analysis WINDOW does not drive LLM cost — the ARCHITECTURE does.

  Deterministic SQL collapses the entire window to ~15 integers; the LLM sees
  ONLY those integers and writes ~250 words. A 30-day forward analysis therefore
  costs exactly the same as a 7-day one.

  So: choose the window for VALUE, not for cost. Hard cap ~1,500 tokens/email.
  Across ~600 entitled tenants weekly that is roughly 30M tokens/year for the
  entire free tier — small, predictable, and trivially cappable.

  ── RECENCY SAFETY — NON-NEGOTIABLE (§10.6) ────────────────────────────────
  NEVER compute a completion RATIO over the last 7 days. Raw "missed last week"
  figures look catastrophic (one tenant showed 272 of 322 not closed, ~84%) but
  that is RECENCY LAG, not failure — an item due three days ago and still inside
  its normal review cycle has not been "missed".

  Backward-looking content is ABSOLUTE COMPLETED-COUNT ONLY.
  The word "overdue" as a level is reserved for the PAID engine.

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
═══════════════════════════════════════════════════════════════════════════*/

SET NOCOUNT ON;
GO

/*───────────────────────────────────────────────────────────────────────────
  1. WEEKLY DIGEST GATE  (§5.3)

  Cheapest-first, short-circuit BEFORE any spend. An unentitled tenant costs
  literally nothing: no aggregation, no LLM call, no email.

  [TRAP] ProductMapping.IsActive is INVERTED — 0 = ENABLED.

  Entitlement is evaluated at JOB EXECUTION TIME, never cached at schedule
  time. That is what makes mid-cycle transitions correct: a tenant upgraded on
  Wednesday does not receive Thursday's free digest.
───────────────────────────────────────────────────────────────────────────*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestGate', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestGate;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestGate
    @CustomerID INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @decision VARCHAR(24) = 'PROCEED',
            @reason   NVARCHAR(300) = NULL,
            @recips   INT = 0;

    -- Step 0: tenant active?
    IF NOT EXISTS (SELECT 1 FROM Customer WHERE ID = @CustomerID AND IsDeleted = 0)
        SELECT @decision = 'EXIT_ZERO_COST',
               @reason   = N'Tenant is disabled (Customer.IsDeleted = 1).';

    -- Step 1: free product mapped AND enabled?
    IF @decision = 'PROCEED'
       AND NOT EXISTS (SELECT 1 FROM ProductMapping
                       WHERE CustomerID = @CustomerID AND ProductID = 18 AND IsActive = 0)
        SELECT @decision = 'EXIT_ZERO_COST',
               @reason   = N'Product 18 (RegInsights Basic) is not mapped-and-enabled.';

    -- Step 2: supersession — paid subsumes free
    IF @decision = 'PROCEED'
       AND EXISTS (SELECT 1 FROM ProductMapping
                   WHERE CustomerID = @CustomerID AND ProductID = 19 AND IsActive = 0)
        SELECT @decision = 'EXIT_SUPERSEDED',
               @reason   = N'Paid tier active — free digest self-skips. Also covers the '
                         + N'non-atomic window where both products are briefly mapped.';

    -- Step 3: recipients, minus durable opt-outs
    IF @decision = 'PROCEED'
    BEGIN
        SELECT @recips = COUNT(DISTINCT ucm.UserID)
        FROM UserCustomerMapping ucm
        JOIN [User] u ON u.ID = ucm.UserID
        WHERE ucm.CustomerID = @CustomerID
          AND ucm.ProductID  = 18
          AND ucm.IsActive   = 0        -- INVERTED
          AND u.IsDeleted    = 0;
        -- TODO: subtract per-recipient opt-outs once that store exists (§5.4).
        --       Opt-out is DURABLE and must SURVIVE tier changes — otherwise an
        --       upgrade/downgrade cycle silently re-subscribes someone who asked
        --       to stop.

        IF @recips = 0
            SELECT @decision = 'EXIT_NO_RECIPIENTS',
                   @reason   = N'No enabled recipients — exit before aggregation or LLM spend.';
    END

    SELECT @CustomerID AS CustomerID,
           @decision   AS Decision,
           @recips     AS RecipientCount,
           ISNULL(@reason, N'Entitled, not superseded, recipients present.') AS Reason,
           CASE WHEN @decision = 'PROCEED' THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS ShouldProceed;
END
GO

/*───────────────────────────────────────────────────────────────────────────
  2. THE ~15 AGGREGATES

  Everything the LLM will ever see. Raw rows are NEVER passed to the model.

  Windows are anchored on ScheduleOn (stable, drift-free, snapshot-free):
      • next 7 days  — "this week"      (the actionable core)
      • next 30 days — "severity radar" (the paid-tier conversion hook)
      • last 7 days  — completed COUNT  (momentum; absolute only)

  Scope: pass @UserID to constrain to a recipient's authorised scope. Pass NULL
  for a tenant-wide digest ONLY when the recipient is verified tenant-wide.
───────────────────────────────────────────────────────────────────────────*/
IF OBJECT_ID('dbo.usp_Insights_FreeDigestAggregates', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_FreeDigestAggregates;
GO
CREATE PROCEDURE dbo.usp_Insights_FreeDigestAggregates
    @CustomerID INT,
    @UserID     INT = NULL,
    @AsOf       DATETIME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AsOf IS NULL SET @AsOf = GETDATE();

    EXEC dbo.usp_Insights_AssertStatusCoverage;   -- fail closed on dictionary gap

    /*  The RiskType value meaning Critical comes from the DICTIONARY, never from a
        literal (non-negotiable #4). This matters more here than anywhere: a wrong
        literal makes the free digest tell a compliance manager they have 2 critical
        obligations due when they have 140 - silently, with no error, in an email
        that goes to 600 tenants.                                                    */
    DECLARE @criticalRisk INT = (
        SELECT TRY_CAST(p.RawValue AS INT)
        FROM dbo.InsightsEnumPolarity p
        JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
        WHERE p.Semantic = 'RiskType' AND p.Meaning LIKE N'Critical%');

    IF @criticalRisk IS NULL
        THROW 51040, N'DICTIONARY GAP - no RiskType value is mapped to Critical in InsightsEnumPolarity. Refusing to send a digest carrying a critical-risk count.', 1;

    /* Scoped instance base. Same 2-D scope rules as the paid engine — a free
       recipient must never receive numbers outside their authorised scope. */
    IF OBJECT_ID('tempdb..#i') IS NOT NULL DROP TABLE #i;
    SELECT s.ComplianceInstanceID, s.BranchID, c.Imprisonment, c.RiskType, c.ComplianceType
    INTO #i
    FROM (
        SELECT si.ComplianceInstanceID, si.BranchID, si.ComplianceID
        FROM dbo.tvfInsightsScopedInstances(ISNULL(@UserID, 0), @CustomerID) si
        WHERE @UserID IS NOT NULL
        UNION ALL
        SELECT i2.ID, i2.CustomerBranchID, i2.ComplianceID
        FROM ComplianceInstance i2
        JOIN CustomerBranch cb ON cb.ID = i2.CustomerBranchID
        WHERE @UserID IS NULL AND cb.CustomerID = @CustomerID
          AND cb.IsDeleted = 0 AND i2.IsDeleted = 0
    ) s
    JOIN Compliance c ON c.ID = s.ComplianceID AND c.IsDeleted = 0;

    /* Forward-looking schedule window. Only ACTIVE, not-yet-closed obligations
       count as "due" — an item already completed is not a deadline. */
    IF OBJECT_ID('tempdb..#due') IS NOT NULL DROP TABLE #due;
    SELECT cso.ID AS SchedId, i.ComplianceInstanceID, cso.ScheduleOn,
           i.Imprisonment, i.RiskType, i.ComplianceType
    INTO #due
    FROM #i i
    JOIN ComplianceScheduleOn cso ON cso.ComplianceInstanceID = i.ComplianceInstanceID
    LEFT JOIN RecentComplianceTransactionView rct ON rct.ComplianceScheduleOnID = cso.ID
    LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = rct.ComplianceStatusID
    WHERE cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1
      AND cso.ScheduleOn > @AsOf
      AND cso.ScheduleOn <= DATEADD(DAY, 30, @AsOf)
      AND (d.ClosureClass IS NULL OR d.ClosureClass = 'open');

    /* Backward window: ABSOLUTE completed count only. See recency warning above. */
    DECLARE @completedLast7 INT = (
        SELECT COUNT(DISTINCT t.ComplianceScheduleOnID)
        FROM ComplianceTransaction t
        JOIN ComplianceScheduleOn cso ON cso.ID = t.ComplianceScheduleOnID
        JOIN #i i ON i.ComplianceInstanceID = cso.ComplianceInstanceID
        JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = t.StatusId
        WHERE d.ClosureClass = 'completed'
          AND t.StatusChangedOn >= DATEADD(DAY, -7, @AsOf)
          AND t.StatusChangedOn <  @AsOf);

    /*  THE ~15 NUMBERS — the complete LLM input.  */
    SELECT
        @CustomerID AS CustomerID,
        @AsOf       AS GeneratedAt,

        -- context (1)
        (SELECT COUNT(*) FROM #i)                                        AS TotalActiveObligations,

        -- this week: next 7 days (4)
        (SELECT COUNT(*) FROM #due WHERE ScheduleOn <= DATEADD(DAY,7,@AsOf))                       AS DueNext7,
        (SELECT COUNT(*) FROM #due WHERE ScheduleOn <= DATEADD(DAY,7,@AsOf) AND RiskType = @criticalRisk) AS CriticalDueNext7,
        (SELECT COUNT(*) FROM #due WHERE ScheduleOn <= DATEADD(DAY,7,@AsOf) AND Imprisonment = 1)  AS ImprisonmentDueNext7,
        (SELECT COUNT(DISTINCT BranchID) FROM #i)                                                  AS BranchesInScope,

        -- severity radar: next 30 days — THE CONVERSION HOOK (4)
        (SELECT COUNT(*) FROM #due)                                                                AS DueNext30,
        (SELECT COUNT(*) FROM #due WHERE Imprisonment = 1)                                         AS ImprisonmentDueNext30,
        (SELECT COUNT(*) FROM #due WHERE ComplianceType = 2)                                       AS LicencesLapsingNext30,
        (SELECT COUNT(*) FROM #due WHERE RiskType = @criticalRisk)                                  AS CriticalDueNext30,

        -- momentum: backward, ABSOLUTE COUNT ONLY (1)
        @completedLast7                                                                            AS CompletedLast7,

        -- shape hints for the email template (3)
        (SELECT COUNT(*) FROM #due WHERE ScheduleOn <= DATEADD(DAY,14,@AsOf))                      AS DueNext14,
        (SELECT COUNT(DISTINCT ComplianceInstanceID) FROM #due WHERE Imprisonment = 1)             AS DistinctImprisonmentObligations,
        (SELECT COUNT(DISTINCT BranchID) FROM #i i2
          WHERE EXISTS (SELECT 1 FROM #due dd WHERE dd.ComplianceInstanceID = i2.ComplianceInstanceID)) AS BranchesWithUpcoming;

    DROP TABLE #i; DROP TABLE #due;
END
GO

/*───────────────────────────────────────────────────────────────────────────
  3. LLM CONTRACT  (implemented in .NET, documented here so it stays with the SQL)

  INPUT  : exactly the ~15 integers above. NEVER raw rows.
  OUTPUT : ~250 words, plain prose, no markup.
  CAP    : ~1,500 tokens total. Over budget ⇒ SKIP the LLM and send the
           deterministic templated version. The email NEVER fails to go out.

  ALLOWED CLAIMS — forward counts, absolute completed count, severity counts.
  BANNED CLAIMS  — any completion RATIO over a recent window (recency lag);
                   the word "overdue" as a level (reserved for the paid tier);
                   any per-location / per-user / per-Act attribution.

  ── THE CONVERSION BOUNDARY (§10.7) ────────────────────────────────────────
  The free email shows the WHAT and never the WHERE / WHO / WHY.

      FREE : "183 items carrying personal liability are due in the next 30 days."
      PAID : "...and here are the 3 locations, the 2 users, and the Acts driving
              them, with what to fix first."

  The gap between the number and its explanation IS the sales pitch. End the
  email on that teased depth.
───────────────────────────────────────────────────────────────────────────*/

PRINT 'Free weekly digest gate + aggregates installed.';
GO
