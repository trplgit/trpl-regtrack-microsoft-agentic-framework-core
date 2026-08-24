using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Writes an already-encrypted report to blob storage. Never sees plaintext - encryption happens
/// upstream in IReportEncryptor, so this seam cannot accidentally persist an unencrypted report.
/// </summary>
public interface IReportBlobWriter
{
    Task<BlobLocation> WriteAsync(EncryptedReportEnvelope envelope, CancellationToken cancellationToken = default);
}
