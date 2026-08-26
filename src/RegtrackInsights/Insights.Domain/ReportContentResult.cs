namespace Insights.Domain;

/// <summary>API_CONTRACTS.md §5's response shape for GET /api/insights/reports/{reportId}/content.</summary>
public sealed record ReportContentResult(Uri ContentUrl, DateTimeOffset ExpiresUtc, bool SandboxRequired = true);
