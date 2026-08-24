namespace Insights.Domain;

/// <summary>
/// What a caller asked for (API_CONTRACTS.md 3: <c>{ "scope": { "type": "tenant" } }</c>), before
/// GatherScopeActivity resolves it into the real <see cref="ScopePair"/> list via
/// IScopeRepository. Not the same thing as ScopePair - that is the resolved output, this is the
/// unresolved request. The contract only documents the "tenant" case explicitly; EntityId is here
/// for the entity-scoped case implied by the report-history endpoint's scopeDescriptor field, and
/// is null for a tenant-wide request.
/// </summary>
public sealed record InsightsScopeRequest(string Type, int? EntityId)
{
    /// <summary>
    /// The canonical scope descriptor, matching the API contract spelling ("tenant" or
    /// "entity:{id}"). One place, because it is half of the run id / cooldown key - two
    /// spellings of the same scope would become two separate 30-day buckets.
    /// </summary>
    public string ToDescriptor() => EntityId is int entityId ? $"entity:{entityId}" : "tenant";
}
