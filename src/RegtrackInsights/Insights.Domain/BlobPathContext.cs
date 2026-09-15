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
///
/// <see cref="PartitionDate"/> is a <see cref="DateOnly"/>, not a timestamp: each caller derives it
/// from whichever of its OWN stored columns the path should be organised by - the paid pipeline uses
/// the report's generation date, the free digest uses the week the digest covers (see
/// Insights.Domain.DigestArtifactIdentity) - so the value handed in must already be a calendar date,
/// never a UTC instant needing conversion. This is why the writer no longer calls
/// <c>DateTime.ToUniversalTime()</c> on it.
/// </summary>
public sealed record BlobPathContext(int TenantId, string ReportType, DateOnly PartitionDate, Guid ReportId);
