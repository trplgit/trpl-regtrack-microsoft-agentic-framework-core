namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-09-29] One (branch, licence type) pair a user holds in LIC_EntitiesAssignment - the
/// LICENCE scope, separate from the compliance (branch, category) scope in <see cref="ScopePair"/>.
/// The free digest's licence figures (sql/35) are read through this scope, so recipients may only
/// share one email when their licence scopes match too (<see cref="ScopeSignature"/>).
/// </summary>
public sealed record LicenceScopePair(long BranchId, long LicenseTypeId);
