using Insights.Domain;
using Microsoft.AspNetCore.Http;

namespace Insights.Api;

/// <summary>
/// Emits the contract's error envelope. Endpoints call these instead of hand-rolling a body, so
/// the shape, the code spelling and the HTTP status can never disagree between two endpoints.
/// </summary>
internal static class InsightsResults
{
    public static IResult Error(InsightsErrorCode code, string message) =>
        Results.Json(
            new InsightsApiErrorEnvelope(InsightsApiError.For(code, message)),
            statusCode: InsightsApiError.ToStatusCode(code));

    /// <summary>
    /// The single refusal used for both "this tenant is not yours" and "this tenant does not
    /// exist".
    ///
    /// [TRAP] They MUST be indistinguishable. A distinct 404 for a non-existent customer turns
    /// this endpoint into an oracle for which customer ids are real, which is half of the
    /// reconnaissance an IDOR attempt needs.
    /// </summary>
    public static IResult TenantNotEligible() =>
        Error(InsightsErrorCode.TenantNotEligible, "You do not have access to Insights for this tenant.");
}
