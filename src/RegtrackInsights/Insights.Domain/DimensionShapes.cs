namespace Insights.Domain;

/*  The control_totals and rows shapes for the nine dimensions. Everything else in the
    six-result-set contract is shared - see DimensionContract.cs.

    CONVENTION: property names match the SQL column names exactly, because Dapper maps
    positionally-declared record parameters BY NAME. Renaming one here to read better in C#
    silently produces a default value instead of a mapping error, so don't.

    Columns carrying a closed vocabulary (RootKind, NodeType, TenantShape, ComparisonGrain)
    are parsed into the enums that already exist in this namespace, fail-closed, by the
    repository - see SqlDimensionRepository. Free-text labels that the SQL does not constrain
    (EngagementBand, QuadrantOverlay) stay strings deliberately: inventing an enum for them
    here would fail closed on a value the SQL is free to add.

    [TRAP] DECLARED WITH init PROPERTIES, NOT A POSITIONAL CONSTRUCTOR - deliberately.
    Every control_totals/rows SELECT in sql/05, 07-14 carries a leading `'xxx' AS ResultSet`
    label column (for a human reading raw output in SSMS) that none of these shapes declare.
    A positional record has ONLY the all-args constructor, so Dapper's strict constructor
    matching fails outright the moment the reader has one column the type does not - the exact
    bug that failed all 47 cases of DimensionRepositoryTests on 2026-08-20 (confirmed via a
    real UAT run, not a guess: "InvalidOperationException: A parameterless default constructor
    or one matching signature (...) is required"). An init-property record gets an implicit
    parameterless constructor, so Dapper falls back to set-by-name and silently ignores any
    reader column - ResultSet included - that has no matching property. Same fields, same
    types, same immutability; only the declaration shape changed. Keep new dimension shapes in
    this style, not positional, or the same bug returns silently.                             */

// ── Location (sql/05) ──────────────────────────────────────────────────────────────────

public sealed record LocationControlTotals
{
    public int ScopedInstances { get; init; }
    public int SumOfRows { get; init; }
    public bool Reconciled { get; init; }
    public int OverdueInstances { get; init; }
    public decimal TenantOverduePct { get; init; }
    public int BranchesReported { get; init; }
    public int ActiveBranchesInTenant { get; init; }
    public int BranchesWithNoObligations { get; init; }
    public int GhostEntities { get; init; }
    public decimal? TenantMedianClosureRatio { get; init; }
    public bool TenantIsOnboarding { get; init; }
    public int TenantCompletedEvents { get; init; }
    public int TenantOnTimeEvents { get; init; }
    public decimal? TenantOnTimePct { get; init; }
}

public sealed record LocationRow
{
    public int BranchID { get; init; }
    public string? BranchName { get; init; }
    public EntityNodeType? NodeType { get; init; }
    public EntityRootKind? RootKind { get; init; }
    public string? ApexName { get; init; }
    public int Instances { get; init; }
    public int Overdue { get; init; }
    public int Ownerless { get; init; }
    public int ImprisonmentInstances { get; init; }
    public int ImprisonmentOverdue { get; init; }
    public int CriticalInstances { get; init; }
    public int DistinctPerformers { get; init; }
    public int DistinctReviewers { get; init; }
    public int ClosureEventsLifetime { get; init; }
    public int ActiveChildren { get; init; }
    public decimal? OverduePct { get; init; }
    public decimal? OwnerlessPct { get; init; }
    public decimal? ClosureRatio { get; init; }
    public int? OverdueRank { get; init; }
    // [FIX] StateID/StateName/PeerStateOverduePct/VsPeerStateNormPP: sql/05_dimension_location.sql
    // has computed these for a while (the peer_coverage_gap detector), but this record never
    // carried them - Dapper silently dropped the columns. Same class of gap as TenantOnTimePct.
    public int? StateID { get; init; }
    public string? StateName { get; init; }
    public decimal? PeerStateOverduePct { get; init; }
    public decimal? VsPeerStateNormPP { get; init; }
    public string? Flags { get; init; }
}

// ── Entity (sql/07) ────────────────────────────────────────────────────────────────────

/// <summary>
/// Constructed manually by SqlDimensionRepository.ReadEntityControlTotalsAsync from the raw
/// EntityControlTotalsRow (TenantShape/ComparisonGrain need enum parsing Dapper cannot do), so
/// this one stays a plain positional record - it is never Dapper-materialized directly.
/// </summary>
public sealed record EntityControlTotals(
    int ScopedInstances, int SumOfRows, bool Reconciled,
    int OverdueInstances, decimal TenantOverduePct,
    int NodesReported, int ActiveBranchesInTenant, int ApexEntityCount,
    EntityCountShape TenantShape, decimal LargestApexSharePct,
    ComparisonGrain ComparisonGrain, string GrainReason);

/// <summary>
/// SubtreeInstances DOUBLE-COUNTS across ancestor levels by design - an instance appears in the
/// subtree of every node above it. Reconcile and sum on <see cref="DirectInstances"/> only.
/// </summary>
public sealed record EntityRow
{
    public int BranchID { get; init; }
    public string? BranchName { get; init; }
    public int? ParentID { get; init; }
    public int? ApexId { get; init; }
    public string? ApexName { get; init; }
    public EntityRootKind? RootKind { get; init; }
    public EntityNodeType? NodeType { get; init; }
    public int? Depth { get; init; }
    public int DirectInstances { get; init; }
    public int SubtreeInstances { get; init; }
    public int SubtreeOverdue { get; init; }
    public int SubtreeOwnerless { get; init; }
    public int SubtreeImprisonment { get; init; }
    public int ActiveChildren { get; init; }
    public decimal? SubtreeOverduePct { get; init; }
    public decimal? ApexSharePct { get; init; }
    public int? SubtreeOverdueRank { get; init; }
    public string? Flags { get; init; }
}

// ── Risk (sql/08) ──────────────────────────────────────────────────────────────────────

public sealed record RiskControlTotals
{
    public int ScopedInstances { get; init; }
    public int SumOfRows { get; init; }
    public bool Reconciled { get; init; }
    public int OverdueInstances { get; init; }
    public decimal TenantOverduePct { get; init; }
    public int RiskLevelsReported { get; init; }
    public int RiskLevelsWithObligations { get; init; }
    public int CriticalRiskType { get; init; }
    public int ImprisonmentInstances { get; init; }
    public decimal? ImprisonmentOnCriticalPct { get; init; }
}

public sealed record RiskRow
{
    public int RiskType { get; init; }
    public string? RiskLabel { get; init; }
    public int Instances { get; init; }
    public int Overdue { get; init; }
    public decimal? OverduePct { get; init; }
    public int Ownerless { get; init; }
    public int ImprisonmentInstances { get; init; }
    public int ImprisonmentOverdue { get; init; }
    public int BranchesCovered { get; init; }
    public decimal? VsTenantPP { get; init; }
    public string? Flags { get; init; }
}

// ── Nature (sql/09) ────────────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="SumOfRows"/> plus <see cref="UntaggedInstances"/> equals <see cref="ScopedInstances"/>;
/// the rows alone do NOT. <see cref="UncategorisedInstances"/> is the Others bucket PLUS the
/// untagged - quoting either half alone understates the blindness by about half.
/// </summary>
public sealed record NatureControlTotals
{
    public int ScopedInstances { get; init; }
    public int SumOfRows { get; init; }
    public bool Reconciled { get; init; }
    public int OverdueInstances { get; init; }
    public decimal TenantOverduePct { get; init; }
    public decimal TenantImprisonmentSharePct { get; init; }
    public int NaturesReported { get; init; }
    public int NaturesWithObligations { get; init; }
    public int RetiredNaturesStillInUse { get; init; }
    public int OthersBucketInstances { get; init; }
    public int UntaggedInstances { get; init; }
    public int UncategorisedInstances { get; init; }
    public decimal UncategorisedPct { get; init; }
}

public sealed record NatureRow
{
    public int NatureId { get; init; }
    public string? NatureName { get; init; }
    public bool IsRetired { get; init; }
    public int Instances { get; init; }
    public int Overdue { get; init; }
    public decimal? OverduePct { get; init; }
    public int Ownerless { get; init; }
    public int ImprisonmentInstances { get; init; }
    public int ImprisonmentOverdue { get; init; }
    public int CriticalInstances { get; init; }
    public int BranchesCovered { get; init; }
    public int PenaltyBearingInstances { get; init; }
    public int FinancialPenaltyInstances { get; init; }
    public int ClosureRiskInstances { get; init; }
    public decimal? ImprisonmentSharePct { get; init; }
    public int? OverdueRank { get; init; }
    public string? Flags { get; init; }
}

// ── Departments (sql/10) ───────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="SumOfRows"/> plus <see cref="UnassignedInstances"/> equals <see cref="ScopedInstances"/>.
/// Obligations carrying no department appear in no row, so every per-department figure excludes them.
/// </summary>
public sealed record DepartmentsControlTotals
{
    public int ScopedInstances { get; init; }
    public int SumOfRows { get; init; }
    public bool Reconciled { get; init; }
    public int OverdueInstances { get; init; }
    public decimal TenantOverduePct { get; init; }
    public int DepartmentsReported { get; init; }
    public int DepartmentsWithObligations { get; init; }
    public int UnassignedInstances { get; init; }
    public decimal UnassignedPct { get; init; }
    public decimal TenantOwnerlessPct { get; init; }
}

public sealed record DepartmentsRow
{
    public int DepartmentID { get; init; }
    public string? DepartmentName { get; init; }
    public int Instances { get; init; }
    public int Overdue { get; init; }
    public decimal? OverduePct { get; init; }
    public int Ownerless { get; init; }
    public decimal? OwnerlessPct { get; init; }
    public int ImprisonmentInstances { get; init; }
    public int CriticalInstances { get; init; }
    public int DistinctUsers { get; init; }
    public int BranchesCovered { get; init; }
    public int? OverdueRank { get; init; }
    public string? Flags { get; init; }
}

// ── Act (sql/11) ───────────────────────────────────────────────────────────────────────

public sealed record ActControlTotals
{
    public int ScopedInstances { get; init; }
    public int SumOfRows { get; init; }
    public bool Reconciled { get; init; }
    public int OverdueInstances { get; init; }
    public decimal TenantOverduePct { get; init; }
    public int ActsReported { get; init; }
    public int DistinctActNames { get; init; }
    public int StatesCovered { get; init; }
    public int ActsSpanningMultipleStates { get; init; }
    public int UnlinkedInstances { get; init; }
    public decimal UnlinkedPct { get; init; }
    public int? LargestRegulatorId { get; init; }
    public decimal? LargestRegulatorSharePct { get; init; }
}

/// <summary>
/// One row is one Act IN ONE STATE - <see cref="State"/> is a column on the Act itself, so the
/// same law across states is several rows sharing <see cref="ActName"/>. That is what the
/// state-divergence detector groups on. <see cref="StartDate"/> is the "emerging law" proxy and
/// is NOT a confirmed classification - see the data_quality note before narrating adoption lag.
/// </summary>
public sealed record ActRow
{
    public int ActID { get; init; }
    public string? ActName { get; init; }
    public string? State { get; init; }
    public int? RegulatorID { get; init; }
    public int? CategoryId { get; init; }
    public int Instances { get; init; }
    public int Overdue { get; init; }
    public decimal? OverduePct { get; init; }
    public int ImprisonmentInstances { get; init; }
    public int BranchesCovered { get; init; }
    public DateTime? StartDate { get; init; }
    public int? OverdueRank { get; init; }
    public string? Flags { get; init; }
}

// ── Users (sql/12) ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Users do NOT partition the instances: one obligation carries a performer AND a reviewer, so
/// <see cref="SumOfPerUserInstances"/> legitimately exceeds <see cref="ScopedInstances"/>.
/// Reconcile on <see cref="AssignedInstancesDistinct"/> plus <see cref="UnassignedInstances"/>.
/// Summing per-user counts is what produced an impossible 155% concentration during design -
/// never derive a share from the row sum.
/// </summary>
public sealed record UsersControlTotals
{
    public int ScopedInstances { get; init; }
    public int AssignedInstancesDistinct { get; init; }
    public bool Reconciled { get; init; }
    public int UnassignedInstances { get; init; }
    public int OverdueInstances { get; init; }
    public decimal TenantOverduePct { get; init; }
    public int UsersReported { get; init; }
    public int SumOfPerUserInstances { get; init; }
    public decimal? TenantMedianOnTimePct { get; init; }
    public decimal? TenantMedianPerformerLoad { get; init; }
    public int InstancesWithSoleReviewer { get; init; }
}

/// <summary>
/// <see cref="IsActive"/> is NOT IsDeleted. A deactivated account still holding live assignments
/// is the continuity risk this dimension exists to surface - never filter these out.
/// <see cref="EngagementBand"/> measures ADOPTION, never quality: on the reference tenant the
/// never-login band had the LOWEST overdue rate because those users are nominal reviewers.
/// </summary>
public sealed record UsersRow
{
    public long UserID { get; init; }
    public string? UserName { get; init; }
    public bool? IsActive { get; init; }
    public int Instances { get; init; }
    public int PerformerInstances { get; init; }
    public int ReviewerInstances { get; init; }
    /// <summary>
    /// RoleID outside {3,4} - e.g. RoleID 6, confirmed live on tenant 1403, not yet in
    /// DIMENSION_SPECS.md. Never blended into Performer/Reviewer - see sql/12's own trap note.
    /// </summary>
    public int OtherRoleInstances { get; init; }
    public int Overdue { get; init; }
    public decimal? OverduePct { get; init; }
    public int ImprisonmentInstances { get; init; }
    public int BranchesCovered { get; init; }
    public int Logins12m { get; init; }
    public string? EngagementBand { get; init; }
    public int CompletedEvents { get; init; }
    public int OnTimeEvents { get; init; }
    public decimal? OnTimePct { get; init; }
    public string? QuadrantOverlay { get; init; }
    public string? Flags { get; init; }
}

// ── Internal (sql/13) ──────────────────────────────────────────────────────────────────

/// <summary>
/// TWO populations, reconciled independently: statutory (<see cref="ScopedInstances"/> /
/// <see cref="SumOfRows"/>) and internal (<see cref="InternalInstances"/> /
/// <see cref="SumOfInternalRows"/>). They are not comparable totals - internal obligations are
/// scoped on the branch axis only.
/// </summary>
public sealed record InternalControlTotals
{
    public int ScopedInstances { get; init; }
    public int SumOfRows { get; init; }
    public bool Reconciled { get; init; }
    public int InternalInstances { get; init; }
    public int SumOfInternalRows { get; init; }
    public int StatutoryOverdueInstances { get; init; }
    public int InternalOverdueInstances { get; init; }
    public decimal? StatutoryOwnerlessPct { get; init; }
    public decimal? InternalOwnerlessPct { get; init; }
    public int BranchesWithStatutory { get; init; }
    public int BranchesWithInternal { get; init; }
    public bool InternalAbsentEntirely { get; init; }
    public int InternalUnmappedStatusRows { get; init; }
}

public sealed record InternalRow
{
    public int BranchID { get; init; }
    public string? BranchName { get; init; }
    public string? ApexName { get; init; }
    public int StatutoryInstances { get; init; }
    public int StatutoryOverdue { get; init; }
    public int StatutoryOwnerless { get; init; }
    public int InternalInstances { get; init; }
    public int InternalOverdue { get; init; }
    public int InternalOwnerless { get; init; }
    public decimal? StatutoryOwnerlessPct { get; init; }
    public decimal? InternalOwnerlessPct { get; init; }
    public string? Flags { get; init; }
}

// ── Event (sql/14) ─────────────────────────────────────────────────────────────────────

/// <summary>
/// This dimension counts EVENT instances, not compliance obligations -
/// <see cref="ScopedInstances"/> is its own population and will not match the other eight.
/// Every dormancy figure here describes the DATABASE, not the business: events may legitimately
/// be tracked off-system, which is why the findings carry a narrative guard requiring them to be
/// put as a question.
/// </summary>
public sealed record EventControlTotals
{
    public int ScopedInstances { get; init; }
    public int SumOfRows { get; init; }
    public bool Reconciled { get; init; }
    public int EventTypesReported { get; init; }
    public int InstancesActiveInWindow { get; init; }
    public int ActivityWindowMonths { get; init; }
    public int BranchesInScope { get; init; }
    public int BranchesWithEventCoverage { get; init; }
    public int BranchesWithoutEventCoverage { get; init; }
    public bool EventModuleDormant { get; init; }
}

public sealed record EventRow
{
    public long EventID { get; init; }
    public string? EventName { get; init; }
    public int InstanceCount { get; init; }
    public int BranchesCovered { get; init; }
    public DateTime? EarliestStart { get; init; }
    public DateTime? LatestStart { get; init; }
    public int InstancesSinceCutoff { get; init; }
    public int DistinctStartDates { get; init; }
    public string? Flags { get; init; }
}

// ── Licence (sql/21) ───────────────────────────────────────────────────────────────────

/// <summary>
/// Grain is LICENCE TYPE, not branch - <see cref="LicenceControlTotals.ScopedLicences"/> counts
/// Lic_tbl_LicenseInstance rows, a different population from every other dimension's
/// ComplianceInstance-based ScopedInstances. Scope here is BRANCH-ONLY, not the full 2-D
/// (branch, category) pair every other dimension enforces - see the proc's own header for why.
/// </summary>
public sealed record LicenceControlTotals
{
    public int ScopedLicences { get; init; }
    public int TypedLicences { get; init; }
    public bool Reconciled { get; init; }
    public decimal TenantLapsedCorroboratedPct { get; init; }
    public int LicenceTypesReported { get; init; }
    public int LicenceTypesWithLicences { get; init; }
    public int UntypedLicences { get; init; }
    public int UncorroboratedLapsedLicences { get; init; }
}

public sealed record LicenceRow
{
    public int LicenseTypeID { get; init; }
    public string? LicenseTypeName { get; init; }
    public bool IsRetired { get; init; }
    public int TotalLicences { get; init; }
    public int ActiveLicences { get; init; }
    public int LapsedCorroborated { get; init; }
    public int LapsedUncorroborated { get; init; }
    public int LapsingNext30 { get; init; }
    public int BranchesCovered { get; init; }
    public decimal? LapsedCorroboratedPct { get; init; }
    public int? OverdueRank { get; init; }
    public string? Flags { get; init; }
}

// ── Backlog aging (sql/22) ─────────────────────────────────────────────────────────────

/// <summary>
/// Overdue schedules bucketed by the FY they fell due in - always exactly 3 rows
/// (current_fy / previous_fy / older). Grain is the SCHEDULE (ComplianceScheduleOnID), not the
/// instance - one instance can carry overdue schedules in more than one bucket, so
/// <see cref="DistinctOverdueSchedules"/> is the reconciling total, never a distinct-instance count.
/// Overdue is a FLOW metric (RecentComplianceTransactionView drifts between runs) - never compare
/// this figure against a different run.
/// </summary>
public sealed record BacklogAgingControlTotals
{
    public int CustomerID { get; init; }
    public DateTime AsOfUtc { get; init; }
    public string CurrentFyLabel { get; init; } = string.Empty;
    public string PreviousFyLabel { get; init; } = string.Empty;
    public int SumOfRows { get; init; }
    public int DistinctOverdueSchedules { get; init; }
}

public sealed record BacklogAgingRow
{
    public string Bucket { get; init; } = string.Empty;
    public string? FYLabel { get; init; }
    public int OverdueCount { get; init; }
    public DateTime? OldestDueDate { get; init; }
    public DateTime? NewestDueDate { get; init; }
    public decimal? SharePct { get; init; }
}

// ── Timeliness by fiscal year (sql/23) ─────────────────────────────────────────────────

/// <summary>
/// Current-FY vs previous-FY on-time closure rate, anchored on ScheduleOn (due date), not the
/// completion date. Tenant-wide single fact, not a per-member breakdown - always exactly 2 rows
/// (current_fy / previous_fy). <see cref="OnTimePctCurrentFY"/>/<see cref="OnTimePctPreviousFY"/>
/// are NULL, never 0%, when that FY has zero completed events with a known Timeliness
/// classification - see CLAUDE.md non-negotiable #5, never a fabricated comparative.
/// </summary>
public sealed record TimelinessFYControlTotals
{
    public int CustomerID { get; init; }
    public DateTime AsOfUtc { get; init; }
    public string CurrentFyLabel { get; init; } = string.Empty;
    public string PreviousFyLabel { get; init; } = string.Empty;
    public int ClosuresCurrentFY { get; init; }
    public int ClosuresPreviousFY { get; init; }
    public decimal? OnTimePctCurrentFY { get; init; }
    public decimal? OnTimePctPreviousFY { get; init; }
    public decimal? YoyChangePP { get; init; }
    public string? FyTrend { get; init; }
}

public sealed record TimelinessFYRow
{
    public string FyBucket { get; init; } = string.Empty;
    public string FYLabel { get; init; } = string.Empty;
    public int CompletedEvents { get; init; }
    public int OnTimeEvents { get; init; }
    public decimal? OnTimePct { get; init; }
}

// ── Forward pipeline (sql/24) ──────────────────────────────────────────────────────────

/// <summary>
/// Schedules due in the next 90 days, bucketed into 5 fixed day-windows - real COUNTS only.
/// `predicted_at_risk` (design doc Sec.3.8's other field) is deliberately NOT built - it is a
/// projection with no defined model yet, never fabricated as a byproduct of these real counts.
/// Grain is the SCHEDULE (ComplianceScheduleOnID), same reconciliation trap as BacklogAging.
/// </summary>
public sealed record ForwardPipelineControlTotals
{
    public int CustomerID { get; init; }
    public DateTime AsOfUtc { get; init; }
    public int DueNext90d { get; init; }
    public int SumOfRows { get; init; }
}

public sealed record ForwardPipelineRow
{
    public string WindowLabel { get; init; } = string.Empty;
    public int MinDaysOut { get; init; }
    public int MaxDaysOut { get; init; }
    public int DueCount { get; init; }
}

// ── Evidence integrity (sql/25) ────────────────────────────────────────────────────────

/// <summary>
/// Closures with a real multi-step review trail in ComplianceTransaction (more than one recorded
/// row), a PROXY for a review step having happened - never for document evidence actually being
/// attached. <see cref="EvidenceInSql"/> is always false: document evidence lives in blob storage,
/// not this database, per design doc Sec.3.7's own explicit words. Always exactly 2 rows
/// (has_trail / single_row_only).
/// </summary>
public sealed record EvidenceIntegrityControlTotals
{
    public int CustomerID { get; init; }
    public DateTime AsOfUtc { get; init; }
    public int SumOfRows { get; init; }
    public int DistinctClosedSchedules { get; init; }
    public decimal? ClosuresWithReviewTrailPct { get; init; }
    public bool EvidenceInSql { get; init; }
}

public sealed record EvidenceIntegrityRow
{
    public string TrailBucket { get; init; } = string.Empty;
    public int ScheduleCount { get; init; }
}
