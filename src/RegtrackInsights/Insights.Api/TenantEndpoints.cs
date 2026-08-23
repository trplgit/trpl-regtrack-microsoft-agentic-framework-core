using Insights.Data;
using Insights.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Insights.Api;

/// <summary>
/// The paid tier's tenant picker (API_CONTRACTS.md §1). Mapped with one line:
///
///     app.MapInsightsTenantEndpoints();
///
/// Apply the host's usual authentication to this group - unlike the digest's unsubscribe and
/// bounce endpoints, there is nothing here that a signed-out caller has any business reaching.
/// </summary>
public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapInsightsTenantEndpoints(this IEndpointRouteBuilder app)
    {
        /*  LIST ELIGIBLE TENANTS.

            Takes no parameters at all, and that is the whole point: the eligible set is derived
            from the authenticated identity and nothing else. There is no id here for a client to
            tamper with, which is what makes this the trustworthy source every other endpoint
            checks against.

            Client behaviour (spec §5.6.3): 0 tenants is the secure-deny state, 1 skips the picker
            entirely, more than 1 shows it. Never make a single-tenant user click.                */
        app.MapGet("/api/insights/tenants", async (
            [FromServices] IInsightsCaller caller,
            [FromServices] ITenantDirectoryRepository tenants,
            CancellationToken cancellationToken) =>
        {
            var eligible = await tenants.GetEligibleTenantsAsync(caller.UserId, cancellationToken);

            /*  An empty list is a 200 with an empty array, not a 403. The caller is legitimately
                authenticated and legitimately entitled to nothing; the client renders the
                secure-deny state from the count. Returning an error here would make "no Insights
                yet" indistinguishable from "something broke".                                   */
            return Results.Ok(new
            {
                tenants = eligible.Select(t => new
                {
                    tenantId = t.TenantId,
                    name = t.Name,
                    tier = ToWireTier(t.Tier),
                    scopeClass = t.ScopeClass.ToContractName(),
                }),
            });
        });

        return app;
    }

    /// <summary>
    /// EntitlementTier is the domain's Free/Paid; the contract's wire words are basic/pro.
    /// Mapped here rather than renaming the enum - the SQL, the gate and the free digest all
    /// speak Free/Paid, and one endpoint's vocabulary is not a reason to churn all of that.
    /// </summary>
    private static string ToWireTier(EntitlementTier tier) => tier switch
    {
        EntitlementTier.Free => "basic",
        EntitlementTier.Paid => "pro",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unmapped tier."),
    };
}
