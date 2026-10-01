using System.Text;
using Insights.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Insights.Worker.Auth;

/// <summary>
/// "Who is asking" for the Insights HTTP surface - JWT Bearer validation against the SAME signing
/// configuration the real RegTrack API already uses (Jwt:Key/Issuer/Audience), so a token minted
/// by RegTrack's existing login is understood here without RegTrack changing anything. Confirmed
/// against the actual RegTrack API repo (trpl-regtrack-dot-net-core-api,
/// Common/ServiceRegistration.cs): same three ValidationParameters, same "sub" claim, same
/// symmetric key shape.
///
/// This one method is the entire "who is asking" story for this host - registered as its own
/// block, with no dependency on and no dependent among the other AddInsights* calls, so the whole
/// authentication surface reads together rather than being scattered across Program.cs.
///
/// [DEFERRED 2026-09-16] Token REVOCATION is not checked. RegTrack's own revocation store
/// (RedisSqlTokenRevocationStore) falls back to a SQL table (RevokedTokens) in vitComplianceSystem -
/// the same database this repo reads - so wiring it in is a real, in-scope option later, but it
/// is a new DB read path and needs explicit sign-off first (this codebase's DB-change rule), not
/// something to add quietly alongside an unrelated hosting change. Redis itself is not wired into
/// this repo at all today either. The bounded residual risk this defers: a user whose TOKEN was
/// revoked keeps Insights access for at most Jwt:ExpiryInMinutes (60) longer. A user whose
/// ENTITLEMENTS changed loses access immediately regardless, because every endpoint already
/// re-queries eligibility live (ITenantDirectoryRepository.IsEligibleAsync) on every request. Flag
/// this as CLAUDE.md Sec.12 open item candidate (O-12) for the product owner to accept or escalate.
/// </summary>
public static class JwtAuthRegistration
{
    public static IServiceCollection AddInsightsAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Fails CLOSED at startup, unlike HealthCheckAuth:Token below - an unauthenticated report
        // endpoint is a breach, not a leak, and a breach must never be reachable by leaving a
        // config key unset. Never written to appsettings.json in a real environment; Key Vault in
        // production (this repo's existing Llm:ApiKeySecretName pattern), user-secrets or an
        // obviously-fake placeholder in appsettings.Development.json locally.
        var jwtKey = Require(configuration, "Jwt:Key");
        var jwtIssuer = Require(configuration, "Jwt:Issuer");
        var jwtAudience = Require(configuration, "Jwt:Audience");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtIssuer,
                    ValidAudience = jwtAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    // Default ClockSkew (5 min) kept deliberately - matching the real RegTrack API's
                    // own configuration rather than tightening it unilaterally on one side only.
                };
            });

        // Deny by default: any endpoint mapped on this host without an explicit [AllowAnonymous]
        // requires a valid token, including one added later by someone who forgets to think about
        // auth. The three /health paths and the digest unsubscribe link are the only intended
        // exceptions - both call .AllowAnonymous() explicitly where they are mapped.
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        services.AddHttpContextAccessor();
        services.AddScoped<IInsightsCaller, JwtInsightsCaller>();

        return services;
    }

    private static string Require(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set it via Key Vault / environment configuration in a real " +
                $"environment, or an obviously-fake placeholder in appsettings.Development.json for local work. " +
                $"This must never be a real secret checked into source.");
}
