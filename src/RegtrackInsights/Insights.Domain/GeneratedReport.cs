namespace Insights.Domain;

/// <summary>
/// The SQL half of the two-store persistence model (design doc Sec.9.1; sql/18_generated_report.sql).
/// Metadata only - no PII. The rendered HTML lives encrypted in blob storage; this row is how it
/// gets found and decrypted, never the content itself.
/// </summary>
public sealed class GeneratedReport
{
    public Guid Id { get; init; }
    public required int CustomerId { get; init; }
    public required string ScopeDescriptor { get; init; }
    public required string ReportType { get; init; }
    public required string Period { get; init; }
    public DateTime GeneratedAtUtc { get; init; }
    public required int GeneratedByUserId { get; init; }
    public required string BlobContainer { get; init; }
    public required string BlobPath { get; init; }
    public required string Status { get; init; }

    /// <summary>The per-blob AES key, wrapped (RSA-OAEP) via the tenant's Key Vault KEK.</summary>
    public required byte[] EncryptedAesKey { get; init; }
    public required string KeyVaultObjectName { get; init; }
    public required string KeyVaultObjectVersion { get; init; }

    /// <summary>
    /// Always the literal "0" - matches DocAI's real metadata shape (ComplianceFileUploadService.cs),
    /// where this is a vestigial field, never an actual cryptographic salt (this scheme uses an IV,
    /// prepended to the blob content, not a KDF).
    /// </summary>
    public string KeyVaultObjectSalt { get; init; } = "0";
}
