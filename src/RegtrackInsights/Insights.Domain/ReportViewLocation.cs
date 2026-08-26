namespace Insights.Domain;

/// <summary>Where a freshly-decrypted report can be read from, and until when. API_CONTRACTS.md §5's response shape.</summary>
public sealed record ReportViewLocation(Uri ContentUrl, DateTimeOffset ExpiresUtc);
