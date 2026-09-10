namespace Insights.Domain;

/// <summary>
/// The identity a report blob's PATH is built from. Passed to <see cref="Insights.Data.IReportBlobWriter"/>
/// so the blob lands under a browsable, lifecycle-manageable prefix
/// (<c>&lt;tenantId&gt;/&lt;reportType&gt;/&lt;yyyy&gt;/&lt;mm&gt;/&lt;reportId&gt;.html.enc</c>) rather
/// than a flat opaque GUID at the container root.
///
/// None of these values are free text - <see cref="TenantId"/> is an integer, <see cref="ReportType"/>
/// is a fixed enum-like slug, <see cref="ReportId"/> is a random GUID - so the path carries no tenant
/// NAME, user identity, or PII. The <see cref="Insights.Domain.GeneratedReport"/> SQL row is still the
/// authoritative index; the path is derived from the row, never parsed back into one.
/// </summary>
public sealed record BlobPathContext(int TenantId, string ReportType, DateTime GeneratedAtUtc, Guid ReportId);
