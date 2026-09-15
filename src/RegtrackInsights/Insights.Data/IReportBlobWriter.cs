using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Writes an already-encrypted report to blob storage. Never sees plaintext - encryption happens
/// upstream in IReportEncryptor, so this seam cannot accidentally persist an unencrypted report.
/// </summary>
public interface IReportBlobWriter
{
    /// <param name="pathContext">
    /// Drives the blob's PATH: <c>&lt;tenantId&gt;/&lt;reportType&gt;/&lt;yyyy&gt;/&lt;mm&gt;/&lt;reportId&gt;.html.enc</c>.
    /// Carries no PII (integer tenant id, fixed report-type slug, random GUID). The
    /// <see cref="Insights.Domain.GeneratedReport"/> row is still the index - the path is built from it.
    /// </param>
    Task<BlobLocation> WriteAsync(EncryptedReportEnvelope envelope, BlobPathContext pathContext, CancellationToken cancellationToken = default);
}
