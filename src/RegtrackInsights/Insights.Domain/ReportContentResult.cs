namespace Insights.Domain;

/// <param name="ReasoningContentUrl">
/// [ADDED 2026-09-26] A short-lived SAS to the real reasoning-trace Markdown for this report, when
/// one exists - null for a report generated before this feature shipped, or when the trace write
/// failed at generation time and was swallowed (fail-soft, same stance the orchestrator already
/// takes on tenant-memory writes). Same lifetime/mechanism as ContentUrl - a fresh throwaway copy,
/// never a SAS against the permanent trace blob.
/// </param>
/// <summary>API_CONTRACTS.md §5's response shape for GET /api/insights/reports/{reportId}/content.</summary>
public sealed record ReportContentResult(Uri ContentUrl, DateTimeOffset ExpiresUtc, bool SandboxRequired = true, Uri? ReasoningContentUrl = null);
