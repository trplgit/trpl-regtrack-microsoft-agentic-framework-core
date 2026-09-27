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
    /// <param name="extension">
    /// [ADDED 2026-09-26] Trailing optional, defaults to "html"/"text/html; charset=utf-8" - every
    /// existing caller (the report itself) keeps its exact current behaviour. The reasoning-trace
    /// view (ReportContentService, real Markdown text) is the first caller to pass "md"/
    /// "text/markdown; charset=utf-8" - same throwaway-copy-then-SAS mechanism, just a different
    /// real content type for a genuinely different kind of plaintext.
    /// </param>
    Task<ReportViewLocation> PublishAsync(
        string plaintextContent, TimeSpan ttl, string extension = "html",
        string contentType = "text/html; charset=utf-8", CancellationToken cancellationToken = default);
}
