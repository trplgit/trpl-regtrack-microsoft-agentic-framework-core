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
    here would fail closed on a value the SQL is free to add.                              */

// ── Location (sql/05) ──────────────────────────────────────────────────────────────────

public sealed record LocationControlTotals(
    int ScopedInstances, int SumOfRows, bool Reconciled,
    int OverdueInstances, decimal TenantOverduePct,
    int BranchesReported, int ActiveBranchesInTenant, int BranchesWithNoObligations,
    int GhostEntities, decimal? TenantMedianClosureRatio, bool TenantIsOnboarding);

public sealed record LocationRow(
    int BranchID, string? BranchName, EntityNodeType? NodeType, EntityRootKind? RootKind, string? ApexName,
    int Instances, int Overdue, int Ownerless,
    int ImprisonmentInstances, int ImprisonmentOverdue, int CriticalInstances,
    int DistinctPerformers, int DistinctReviewers, int ClosureEventsLifetime, int ActiveChildren,
    decimal? OverduePct, decimal? OwnerlessPct, decimal? ClosureRatio,
    int? OverdueRank, string? Flags);

// ── Entity (sql/07) ────────────────────────────────────────────────────────────────────

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
public sealed record EntityRow(
    int BranchID, string? BranchName, int? ParentID, int? ApexId, string? ApexName,
    EntityRootKind? RootKind, EntityNodeType? NodeType, int? Depth,
    int DirectInstances, int SubtreeInstances, int SubtreeOverdue, int SubtreeOwnerless,
    int SubtreeImprisonment, int ActiveChildren,
    decimal? SubtreeOverduePct, decimal? ApexSharePct, int? SubtreeOverdueRank, string? Flags);

// ── Risk (sql/08) ──────────────────────────────────────────────────────────────────────

public sealed record RiskControlTotals(
    int ScopedInstances, int SumOfRows, bool Reconciled,
    int OverdueInstances, decimal TenantOverduePct,
    int RiskLevelsReported, int RiskLevelsWithObligations,
    int CriticalRiskType, int ImprisonmentInstances, decimal? ImprisonmentOnCriticalPct);

public sealed record RiskRow(
    int RiskType, string? RiskLabel, int Instances, int Overdue, decimal? OverduePct,
    int Ownerless, int ImprisonmentInstances, int ImprisonmentOverdue, int BranchesCovered,
    decimal? VsTenantPP, string? Flags);

// ── Nature (sql/09) ────────────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="SumOfRows"/> plus <see cref="UntaggedInstances"/> equals <see cref="ScopedInstances"/>;
/// the rows alone do NOT. <see cref="UncategorisedInstances"/> is the Others bucket PLUS the
/// untagged - quoting either half alone understates the blindness by about half.
/// </summary>
public sealed record NatureControlTotals(
    int ScopedInstances, int SumOfRows, bool Reconciled,
    int OverdueInstances, decimal TenantOverduePct, decimal TenantImprisonmentSharePct,
    int NaturesReported, int NaturesWithObligations, int RetiredNaturesStillInUse,
    int OthersBucketInstances, int UntaggedInstances,
    int UncategorisedInstances, decimal UncategorisedPct);

public sealed record NatureRow(
    int NatureId, string? NatureName, bool IsRetired,
    int Instances, int Overdue, decimal? OverduePct, int Ownerless,
    int ImprisonmentInstances, int ImprisonmentOverdue, int CriticalInstances,
    int BranchesCovered, int PenaltyBearingInstances,
    int FinancialPenaltyInstances, int ClosureRiskInstances,
    decimal? ImprisonmentSharePct, int? OverdueRank, string? Flags);

// ── Departments (sql/10) ───────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="SumOfRows"/> plus <see cref="UnassignedInstances"/> equals <see cref="ScopedInstances"/>.
/// Obligations carrying no department appear in no row, so every per-department figure excludes them.
/// </summary>
public sealed record DepartmentsControlTotals(
    int ScopedInstances, int SumOfRows, bool Reconciled,
    int OverdueInstances, decimal TenantOverduePct,
    int DepartmentsReported, int DepartmentsWithObligations,
    int UnassignedInstances, decimal UnassignedPct, decimal TenantOwnerlessPct);

public sealed record DepartmentsRow(
    int DepartmentID, string? DepartmentName,
    int Instances, int Overdue, decimal? OverduePct,
    int Ownerless, decimal? OwnerlessPct,
    int ImprisonmentInstances, int CriticalInstances,
    int DistinctUsers, int BranchesCovered, int? OverdueRank, string? Flags);

// ── Act (sql/11) ───────────────────────────────────────────────────────────────────────

public sealed record ActControlTotals(
    int ScopedInstances, int SumOfRows, bool Reconciled,
    int OverdueInstances, decimal TenantOverduePct,
    int ActsReported, int DistinctActNames, int StatesCovered, int ActsSpanningMultipleStates,
    int UnlinkedInstances, decimal UnlinkedPct,
    int? LargestRegulatorId, decimal? LargestRegulatorSharePct);

/// <summary>
/// One row is one Act IN ONE STATE - <see cref="State"/> is a column on the Act itself, so the
/// same law across states is several rows sharing <see cref="ActName"/>. That is what the
/// state-divergence detector groups on. <see cref="StartDate"/> is the "emerging law" proxy and
/// is NOT a confirmed classification - see the data_quality note before narrating adoption lag.
/// </summary>
public sealed record ActRow(
    int ActID, string? ActName, string? State, int? RegulatorID, int? CategoryId,
    int Instances, int Overdue, decimal? OverduePct,
    int ImprisonmentInstances, int BranchesCovered,
    DateTime? StartDate, int? OverdueRank, string? Flags);

// ── Users (sql/12) ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Users do NOT partition the instances: one obligation carries a performer AND a reviewer, so
/// <see cref="SumOfPerUserInstances"/> legitimately exceeds <see cref="ScopedInstances"/>.
/// Reconcile on <see cref="AssignedInstancesDistinct"/> plus <see cref="UnassignedInstances"/>.
/// Summing per-user counts is what produced an impossible 155% concentration during design -
/// never derive a share from the row sum.
/// </summary>
public sealed record UsersControlTotals(
    int ScopedInstances, int AssignedInstancesDistinct, bool Reconciled,
    int UnassignedInstances, int OverdueInstances, decimal TenantOverduePct,
    int UsersReported, int SumOfPerUserInstances,
    decimal? TenantMedianOnTimePct, decimal? TenantMedianPerformerLoad,
    int InstancesWithSoleReviewer);

/// <summary>
/// <see cref="IsActive"/> is NOT IsDeleted. A deactivated account still holding live assignments
/// is the continuity risk this dimension exists to surface - never filter these out.
/// <see cref="EngagementBand"/> measures ADOPTION, never quality: on the reference tenant the
/// never-login band had the LOWEST overdue rate because those users are nominal reviewers.
/// </summary>
public sealed record UsersRow(
    long UserID, string? UserName, bool? IsActive,
    int Instances, int PerformerInstances, int ReviewerInstances,
    int Overdue, decimal? OverduePct, int ImprisonmentInstances, int BranchesCovered,
    int Logins12m, string? EngagementBand,
    int CompletedEvents, int OnTimeEvents, decimal? OnTimePct,
    string? QuadrantOverlay, string? Flags);

// ── Internal (sql/13) ──────────────────────────────────────────────────────────────────

/// <summary>
/// TWO populations, reconciled independently: statutory (<see cref="ScopedInstances"/> /
/// <see cref="SumOfRows"/>) and internal (<see cref="InternalInstances"/> /
/// <see cref="SumOfInternalRows"/>). They are not comparable totals - internal obligations are
/// scoped on the branch axis only.
/// </summary>
public sealed record InternalControlTotals(
    int ScopedInstances, int SumOfRows, bool Reconciled,
    int InternalInstances, int SumOfInternalRows,
    int StatutoryOverdueInstances, int InternalOverdueInstances,
    decimal? StatutoryOwnerlessPct, decimal? InternalOwnerlessPct,
    int BranchesWithStatutory, int BranchesWithInternal,
    bool InternalAbsentEntirely, int InternalUnmappedStatusRows);

public sealed record InternalRow(
    int BranchID, string? BranchName, string? ApexName,
    int StatutoryInstances, int StatutoryOverdue, int StatutoryOwnerless,
    int InternalInstances, int InternalOverdue, int InternalOwnerless,
    decimal? StatutoryOwnerlessPct, decimal? InternalOwnerlessPct, string? Flags);

// ── Event (sql/14) ─────────────────────────────────────────────────────────────────────

/// <summary>
/// This dimension counts EVENT instances, not compliance obligations -
/// <see cref="ScopedInstances"/> is its own population and will not match the other eight.
/// Every dormancy figure here describes the DATABASE, not the business: events may legitimately
/// be tracked off-system, which is why the findings carry a narrative guard requiring them to be
/// put as a question.
/// </summary>
public sealed record EventControlTotals(
    int ScopedInstances, int SumOfRows, bool Reconciled,
    int EventTypesReported, int InstancesActiveInWindow, int ActivityWindowMonths,
    int BranchesInScope, int BranchesWithEventCoverage, int BranchesWithoutEventCoverage,
    bool EventModuleDormant);

public sealed record EventRow(
    long EventID, string? EventName, int InstanceCount, int BranchesCovered,
    DateTime? EarliestStart, DateTime? LatestStart,
    int InstancesSinceCutoff, int DistinctStartDates, string? Flags);
