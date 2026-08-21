namespace Insights.Domain;

/// <summary>
/// What a caller asked for (API_CONTRACTS.md 3: <c>{ "scope": { "type": "tenant" } }</c>), before
/// GatherScopeActivity resolves it into the real <see cref="ScopePair"/> list via
/// IScopeRepository. Not the same thing as ScopePair - that is the resolved output, this is the
/// unresolved request. The contract only documents the "tenant" case explicitly; EntityId is here
/// for the entity-scoped case implied by the report-history endpoint's scopeDescriptor field, and
/// is null for a tenant-wide request.
/// </summary>
public sealed record InsightsScopeRequest(string Type, int? EntityId);
