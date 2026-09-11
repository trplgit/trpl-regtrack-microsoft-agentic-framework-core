using System.Data;
using Dapper;
using Insights.Domain;
using Microsoft.Data.SqlClient;

namespace Insights.Data;

/// <inheritdoc cref="IFreeDigestArtifactRepository"/>
public sealed class SqlFreeDigestArtifactRepository(string connectionString) : IFreeDigestArtifactRepository
{
    public async Task<FreeDigestArtifactClaimResult> ClaimAsync(
        int customerId, DateOnly weekEnding, string scopeSignature, int representativeUserId, string tenantName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        var row = await connection.QuerySingleAsync<ClaimRow>(
            new CommandDefinition(
                "dbo.usp_Insights_FreeDigestArtifactClaim",
                new
                {
                    CustomerID = customerId,
                    WeekEnding = weekEnding.ToDateTime(TimeOnly.MinValue),
                    ScopeSignature = scopeSignature,
                    RepresentativeUserID = representativeUserId,
                    TenantName = tenantName,
                },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return new FreeDigestArtifactClaimResult(row.Claimed, row.ArtifactID);
    }

    public async Task CompleteAsync(
        Guid artifactId, DateTime asOfUtc, string source, int recipientCount,
        string blobContainer, string blobPath, byte[] encryptedAesKey, string keyVaultObjectName, string keyVaultObjectVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "dbo.usp_Insights_FreeDigestArtifactComplete",
                new
                {
                    ArtifactID = artifactId,
                    AsOfUtc = asOfUtc,
                    Source = source,
                    RecipientCount = recipientCount,
                    BlobContainer = blobContainer,
                    BlobPath = blobPath,
                    EncryptedAesKey = encryptedAesKey,
                    KeyVaultObjectName = keyVaultObjectName,
                    KeyVaultObjectVersion = keyVaultObjectVersion,
                },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }

    public async Task ReleaseAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "dbo.usp_Insights_FreeDigestArtifactRelease",
                new { ArtifactID = artifactId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<FreeDigestArtifact>> GetForDispatchAsync(
        int customerId, int maxAgeDays, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        var rows = await connection.QueryAsync<ArtifactRow>(
            new CommandDefinition(
                "dbo.usp_Insights_FreeDigestArtifactsForDispatch",
                new { CustomerID = customerId, MaxAgeDays = maxAgeDays },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return rows.Select(r => new FreeDigestArtifact(
            r.ArtifactID, r.CustomerID, DateOnly.FromDateTime(r.WeekEnding), r.ScopeSignature, r.RepresentativeUserID,
            r.TenantName, r.AsOfUtc, r.GeneratedAtUtc, r.Source, r.BlobContainer, r.BlobPath,
            r.EncryptedAesKey, r.KeyVaultObjectName, r.KeyVaultObjectVersion)).ToList();
    }

    public async Task MarkDispatchedAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "dbo.usp_Insights_FreeDigestArtifactMarkDispatched",
                new { ArtifactID = artifactId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<(Guid ArtifactId, string BlobContainer, string BlobPath)>> GetForPurgeAsync(
        int retentionDays, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        var rows = await connection.QueryAsync<PurgeRow>(
            new CommandDefinition(
                "dbo.usp_Insights_FreeDigestArtifactsForPurge",
                new { RetentionDays = retentionDays },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));

        return rows.Select(r => (r.ArtifactID, r.BlobContainer, r.BlobPath)).ToList();
    }

    public async Task DeleteAsync(Guid artifactId, int retentionDays, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "dbo.usp_Insights_FreeDigestArtifactDelete",
                new { ArtifactID = artifactId, RetentionDays = retentionDays },
                commandType: CommandType.StoredProcedure,
                cancellationToken: cancellationToken));
    }

    private sealed record ClaimRow(bool Claimed, Guid? ArtifactID);
    private sealed record PurgeRow(Guid ArtifactID, string BlobContainer, string BlobPath);
    private sealed record ArtifactRow(
        Guid ArtifactID, int CustomerID, DateTime WeekEnding, string ScopeSignature, int RepresentativeUserID,
        string TenantName, DateTime AsOfUtc, DateTime GeneratedAtUtc, string Source,
        string BlobContainer, string BlobPath, byte[] EncryptedAesKey, string KeyVaultObjectName, string KeyVaultObjectVersion);
}
