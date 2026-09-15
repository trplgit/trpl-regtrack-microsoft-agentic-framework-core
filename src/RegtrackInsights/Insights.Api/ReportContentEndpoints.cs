using Insights.Data;
using Insights.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Insights.Api;

/// <summary>
/// Open a report (API_CONTRACTS.md §5, design doc Sec.9.3). Mapped with one line:
///
///     app.MapInsightsReportContentEndpoints();
/// </summary>
public static class ReportContentEndpoints
{
    public static IEndpointRouteBuilder MapInsightsReportContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/insights/reports/{reportId}/content", async (
            Guid reportId,
            [FromQuery] int tenantId,
            [FromServices] IInsightsCaller caller,
            [FromServices] ITenantDirectoryRepository tenants,
            [FromServices] IReportContentService content,
            CancellationToken cancellationToken) =>
        {
            // Step 1 (API_CONTRACTS.md §5): tenant eligibility, same ordering and IDOR reasoning as
            // every other endpoint here - the AUTHENTICATED caller's eligible set, re-derived every
            // request, never a client-supplied claim trusted on its own.
            var tenant = await tenants.IsEligibleAsync(caller.UserId, tenantId, cancellationToken);
            if (tenant is null)
                return InsightsResults.TenantNotEligible();

            // Steps 2-5 live in IReportContentService: re-resolve scope NOW, enforce
            // report_scope subset-of viewer_scope, decrypt, mint a short-lived view location,
            // audit-log. A null result covers every refusal reason (no such report, wrong tenant,
            // scope no longer covers it) - all map to the SAME 404, so this endpoint cannot be used
            // to probe which report ids exist (§4's REPORT_NOT_VISIBLE precedent).
            var result = await content.OpenAsync(reportId, tenantId, caller.UserId, cancellationToken);
            if (result is null)
                return InsightsResults.Error(InsightsErrorCode.ReportNotVisible, "No such report, or you no longer have access to it.");

            return Results.Json(new
            {
                contentUrl = result.ContentUrl.ToString(),
                expiresUtc = result.ExpiresUtc,
                sandboxRequired = result.SandboxRequired,
            });
        });

        return app;
    }
}
