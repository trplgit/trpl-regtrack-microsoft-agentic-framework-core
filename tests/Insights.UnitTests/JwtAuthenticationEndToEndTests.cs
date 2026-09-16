using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Insights.Api;
using Insights.Worker.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Insights.UnitTests;

/// <summary>
/// Proves the WHOLE inbound auth path - AddInsightsAuthentication's real AddJwtBearer wiring,
/// through to JwtInsightsCaller reading the result - works correctly using an obviously-fake
/// signing key, exactly as the real host will once the real Jwt:Key/Issuer/Audience are supplied.
/// Only the config VALUES differ between this test and production; the code path is identical.
///
/// This is what makes it possible to build and prove the auth feature correct before management
/// supplies the real RegTrack signing key - swapping the fake key for the real one is a config
/// change, not a code milestone.
/// </summary>
public sealed class JwtAuthenticationEndToEndTests
{
    private const string FakeSigningKey = "unit-test-only-fake-signing-key-not-real-38fj2xyz9k";
    private const string Issuer = "LoginPage";
    private const string Audience = "Users";

    private static async Task<HttpClient> StartHostAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = FakeSigningKey,
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
            })
            .Build();

        var hostBuilder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton<IConfiguration>(configuration);
                services.AddInsightsAuthentication(configuration);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    // Stand-in for a real Insights endpoint: reads IInsightsCaller.UserId exactly
                    // the way RunEndpoints/TenantEndpoints do, protected by nothing beyond the
                    // fallback policy AddInsightsAuthentication registers.
                    endpoints.MapGet("/whoami", (IInsightsCaller caller) =>
                        Results.Ok(new { userId = caller.UserId }));
                });
            });
        });
        var host = await hostBuilder.StartAsync();

        return host.GetTestClient();
    }

    private static string MintToken(
        string signingKey, string issuer, string audience, string subject, TimeSpan? lifetime = null)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: [new Claim(JwtRegisteredClaimNames.Sub, subject)],
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(60)),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task ValidToken_IsAccepted_AndUserIdIsReadCorrectly()
    {
        var client = await StartHostAsync();
        var token = MintToken(FakeSigningKey, Issuer, Audience, subject: "38");
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"userId\":38", body);
    }

    [Fact]
    public async Task NoToken_IsRejected_ByTheFallbackAuthorizationPolicy()
    {
        var client = await StartHostAsync();

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenSignedWithAWrongKey_IsRejected()
    {
        var client = await StartHostAsync();
        var forgedToken = MintToken("a-completely-different-key-nobody-agreed-to", Issuer, Audience, subject: "38");
        client.DefaultRequestHeaders.Authorization = new("Bearer", forgedToken);

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        var client = await StartHostAsync();
        // Comfortably past JwtBearerOptions' default 5-minute ClockSkew tolerance (kept
        // deliberately unset - see JwtAuthRegistration's own comment - to match the real RegTrack
        // API's configuration), so this genuinely exercises expiry rejection rather than landing
        // inside the tolerance window and passing for the wrong reason.
        var expiredToken = MintToken(FakeSigningKey, Issuer, Audience, subject: "38", lifetime: TimeSpan.FromMinutes(-10));
        client.DefaultRequestHeaders.Authorization = new("Bearer", expiredToken);

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenWithTheWrongAudience_IsRejected()
    {
        var client = await StartHostAsync();
        var wrongAudienceToken = MintToken(FakeSigningKey, Issuer, audience: "SomeOtherApp", subject: "38");
        client.DefaultRequestHeaders.Authorization = new("Bearer", wrongAudienceToken);

        var response = await client.GetAsync("/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
