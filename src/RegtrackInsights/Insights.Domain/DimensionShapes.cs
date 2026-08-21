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
