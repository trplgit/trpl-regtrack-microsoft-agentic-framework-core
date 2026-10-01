using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// [ADDED 2026-09-26] Stores the real reasoning-trace Markdown (see ReasoningExplainerAgent) as a
/// PERMANENT, plain-text sibling of the report's own encrypted blob - same container as
/// IReportBlobWriter (no new container), a deterministic path derived from the same
/// BlobPathContext. Deliberately plain, not envelope-encrypted like the report itself: this is an
/// internal QA/tester artifact, not the customer-facing document, and its own read path
/// (ReportContentService) never mints a SAS against it directly either way - it always republishes
/// a fresh throwaway copy first, same "no long-lived SAS against the permanent artifact" rule
/// IReportViewPublisher's own doc comment already states for the report.
/// </summary>
public interface IReasoningTraceStore
{
    Task<BlobLocation> WriteAsync(string markdown, BlobPathContext pathContext, CancellationToken cancellationToken = default);

    /// <summary>Null when no reasoning trace was ever written for this location - not every report has one (generated before this feature shipped, or the explainer call failed and was swallowed).</summary>
    Task<string?> ReadIfExistsAsync(BlobLocation location, CancellationToken cancellationToken = default);
}
