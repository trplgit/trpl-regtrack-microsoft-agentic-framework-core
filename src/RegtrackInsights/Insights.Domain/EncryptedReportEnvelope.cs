namespace Insights.Domain;

/// <summary>
/// The output of envelope-encrypting one report (design doc Sec.9.2, DocAI pattern). Content is
/// [16-byte IV][AES-256-CBC ciphertext] - ready to write to blob storage verbatim, IV included, no
/// separate IV field to carry alongside it (matches the real DocAI convention).
/// </summary>
public sealed record EncryptedReportEnvelope(
    byte[] Content,
    byte[] EncryptedAesKey,
    string KeyVaultObjectName,
    string KeyVaultObjectVersion);
