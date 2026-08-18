namespace Insights.Domain;

/// <summary>Whether a tenant has one apex entity or several.</summary>
public enum EntityCountShape
{
    SingleEntity,
    MultiEntity,
}

/// <summary>Which grain to compare at, chosen by usp_Insights_TenantShape's dominance rule.</summary>
public enum ComparisonGrain
{
    /// <summary>Single apex - skip entity comparison, lead with locations.</summary>
    Locations,

    /// <summary>Largest apex dominates the estate, or a childless holding shell exists - go one level down.</summary>
    DescendOneLevel,

    /// <summary>Apex entities are balanced - compare at apex level.</summary>
    Apex,
}

/// <summary>One apex entity's share of the tenant's estate.</summary>
public sealed record ApexShape(int ApexId, string ApexName, int SubtreeInstances, int DescendantNodes);

/// <summary>Result of usp_Insights_TenantShape - the comparison grain a dimension should use for this tenant.</summary>
public sealed class TenantShapeResult(
    int customerId, int apexEntityCount, EntityCountShape shape, decimal largestApexSharePct,
    ComparisonGrain grain, string grainReason, IReadOnlyList<ApexShape> apexes)
{
    public int CustomerId { get; } = customerId;
    public int ApexEntityCount { get; } = apexEntityCount;
    public EntityCountShape Shape { get; } = shape;
    public decimal LargestApexSharePct { get; } = largestApexSharePct;
    public ComparisonGrain Grain { get; } = grain;
    public string GrainReason { get; } = grainReason;
    public IReadOnlyList<ApexShape> Apexes { get; } = apexes;
}
