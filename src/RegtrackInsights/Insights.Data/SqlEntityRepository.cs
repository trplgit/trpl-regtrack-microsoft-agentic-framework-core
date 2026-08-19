using System.Data;
using Dapper;
using Insights.Domain;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IEntityRepository"/>
public sealed class SqlEntityRepository(string connectionString) : IEntityRepository
{
    /// <summary>Error raised by usp_Insights_EntityRollup when subtree sums do not tie to the control total.</summary>
    private const int EntityRollupReconciliationFailedErrorNumber = 51020;

    public async Task<IReadOnlyList<EntityTreeNode>> GetEntityTreeAsync(int customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        var rows = await connection.QueryAsync<TreeRow>(
            new CommandDefinition(
                "SELECT * FROM dbo.tvfInsightsEntityTree(@CustomerID)",
                new { CustomerID = customerId },
                cancellationToken: cancellationToken));

        return rows.Select(ToEntityTreeNode).ToList();
    }

    public async Task<EntityRollup> GetEntityRollupAsync(int customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        try
        {
            using var multi = await connection.QueryMultipleAsync(
                new CommandDefinition(
                    "dbo.usp_Insights_EntityRollup",
                    new { CustomerID = customerId },
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: cancellationToken));

            var orphans = (await multi.ReadAsync<OrphanDeclaration>()).ToList();
            var roots = (await multi.ReadAsync<RootSummaryRow>())
                .Select(r => new EntityRootSummary(r.ApexId, r.ApexName, ParseRootKind(r.RootKind),
                    r.NodesInSubtree, r.SubtreeInstances, r.InstancesOnIntermediateNodes, r.InstancesOnLeaves))
                .ToList();
            var nodes = (await multi.ReadAsync<RollupNodeRow>()).Select(ToEntityRollupNode).ToList();

            return new EntityRollup(orphans, roots, nodes);
        }
        catch (SqlException ex) when (ex.Number == EntityRollupReconciliationFailedErrorNumber)
        {
            // The proc THROWs before selecting anything on this failure - there is no
            // result set to lose, so a plain QueryMultipleAsync is safe here (unlike
            // usp_Insights_GoldenInvariants, which selects first, then throws).
            throw new EntityRollupReconciliationException(customerId, ex);
        }
    }

    public async Task<TenantShapeResult> GetTenantShapeAsync(int customerId, decimal dominanceThreshold = 70.00m, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        using var multi = await connection.QueryMultipleAsync(
            new CommandDefinition(
                "dbo.usp_Insights_TenantShape",
                new { CustomerID = customerId, DominanceThreshold = dominanceThreshold },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        var summary = await multi.ReadSingleAsync<ShapeSummaryRow>();
        var apexes = (await multi.ReadAsync<ApexShape>()).ToList();

        return new TenantShapeResult(
            summary.CustomerID, summary.ApexEntityCount, ParseShape(summary.TenantShape),
            summary.LargestApexSharePct, ParseGrain(summary.ComparisonGrain), summary.GrainReason, apexes);
    }

    private static EntityTreeNode ToEntityTreeNode(TreeRow r) =>
        new(r.BranchID, r.BranchName, r.ParentID, r.ApexId, r.ApexName, ParseRootKind(r.RootKind), r.Depth, ParseNodeType(r.NodeType));

    private static EntityRollupNode ToEntityRollupNode(RollupNodeRow r) =>
        new(r.BranchID, r.BranchName, r.ParentID, r.ApexId, r.ApexName, ParseRootKind(r.RootKind), r.Depth, ParseNodeType(r.NodeType), r.DirectInstances);

    private static EntityRootKind ParseRootKind(string value) => value switch
    {
        "apex" => EntityRootKind.Apex,
        "orphan" => EntityRootKind.Orphan,
        _ => throw new InvalidOperationException($"Unknown RootKind '{value}' from tvfInsightsEntityTree."),
    };

    private static EntityNodeType ParseNodeType(string value) => value switch
    {
        "leaf" => EntityNodeType.Leaf,
        "intermediate" => EntityNodeType.Intermediate,
        _ => throw new InvalidOperationException($"Unknown NodeType '{value}' from tvfInsightsEntityTree."),
    };

    private static EntityCountShape ParseShape(string value) => value switch
    {
        "single_entity" => EntityCountShape.SingleEntity,
        "multi_entity" => EntityCountShape.MultiEntity,
        _ => throw new InvalidOperationException($"Unknown TenantShape '{value}' from usp_Insights_TenantShape."),
    };

    private static ComparisonGrain ParseGrain(string value) => value switch
    {
        "locations" => ComparisonGrain.Locations,
        "descend_one_level" => ComparisonGrain.DescendOneLevel,
        "apex" => ComparisonGrain.Apex,
        _ => throw new InvalidOperationException($"Unknown ComparisonGrain '{value}' from usp_Insights_TenantShape."),
    };

    private sealed record TreeRow(int BranchID, string BranchName, int? ParentID, int ApexId, string ApexName, string RootKind, int Depth, string NodeType);

    private sealed record RollupNodeRow(int BranchID, string BranchName, int? ParentID, int ApexId, string ApexName, string RootKind, int Depth, string NodeType, int DirectInstances);

    private sealed record RootSummaryRow(int ApexId, string ApexName, string RootKind, int NodesInSubtree, int SubtreeInstances, int InstancesOnIntermediateNodes, int InstancesOnLeaves);

    private sealed record ShapeSummaryRow(int CustomerID, int ApexEntityCount, string TenantShape, decimal LargestApexSharePct, string ComparisonGrain, string GrainReason);
}
