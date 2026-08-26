using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// Item 14's read half (design doc Sec.9.3, API_CONTRACTS.md §5). Re-authorises against the
/// viewer's CURRENT scope - never the scope that existed at generation time - decrypts, and mints a
/// short-lived view location. Returns null for every refusal reason (report doesn't exist, wrong
/// tenant, scope no longer covers it) so the endpoint can return one indistinguishable 404 for all
/// of them, same "do not reveal existence" rule RunEndpoints.cs already applies to run ids.
/// </summary>
public interface IReportContentService
{
    Task<ReportContentResult?> OpenAsync(Guid reportId, int tenantId, int viewerUserId, CancellationToken cancellationToken = default);
}
