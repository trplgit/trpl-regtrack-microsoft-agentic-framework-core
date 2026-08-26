using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Item 14's read half, design doc Sec.9.3 steps 3-4: takes DECRYPTED plaintext HTML (never the
/// permanent encrypted blob) and publishes it somewhere the client's sandboxed iframe can fetch it
/// from directly, for a short window only. The permanent encrypted blob written by IReportBlobWriter
/// NEVER gets a SAS minted against it - a fresh, separate, short-lived plaintext copy is what gets
/// published per view, so "the blob is never directly reachable" holds for the artifact that
/// actually matters (the one that outlives this request).
/// </summary>
public interface IReportViewPublisher
{
    Task<ReportViewLocation> PublishAsync(string plaintextHtml, TimeSpan ttl, CancellationToken cancellationToken = default);
}
