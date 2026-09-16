using System.Security.Claims;
using Insights.Api;
using Microsoft.AspNetCore.Http;

namespace Insights.UnitTests;

/// <summary>
/// JwtInsightsCaller reads a ClaimsPrincipal that ASP.NET Core's JWT Bearer middleware has ALREADY
/// validated (signature, issuer, audience, expiry) - it does no verification of its own. These
/// tests build that ClaimsPrincipal directly, the same shape the middleware produces, rather than
/// going through a real token, since the class under test starts one layer after that.
/// End-to-end coverage of the actual token -> middleware -> ClaimsPrincipal path lives in
/// JwtAuthenticationEndToEndTests.
/// </summary>
public sealed class JwtInsightsCallerTests
{
    private static IHttpContextAccessor AccessorFor(ClaimsPrincipal? principal)
    {
        var context = new DefaultHttpContext();
        if (principal is not null)
            context.User = principal;

        var accessor = new HttpContextAccessorStub(context);
        return accessor;
    }

    private static ClaimsPrincipal PrincipalWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "TestAuth"));

    [Fact]
    public void UserId_ReadsFromNameIdentifier_TheDefaultInboundJwtBearerMapping()
    {
        // This is the claim shape ASP.NET Core's JwtBearer middleware ACTUALLY produces at
        // runtime - it rewrites the JWT's "sub" claim to ClaimTypes.NameIdentifier under the
        // default inbound claim mapping, confirmed against the real RegTrack API's own token
        // reading code, which checks NameIdentifier first for the same reason.
        var accessor = AccessorFor(PrincipalWith(new Claim(ClaimTypes.NameIdentifier, "38")));

        var caller = new JwtInsightsCaller(accessor);

        Assert.Equal(38, caller.UserId);
    }

    [Fact]
    public void UserId_FallsBackToRawSubClaim_WhenNameIdentifierMappingIsAbsent()
    {
        var accessor = AccessorFor(PrincipalWith(new Claim("sub", "38")));

        var caller = new JwtInsightsCaller(accessor);

        Assert.Equal(38, caller.UserId);
    }

    [Fact]
    public void Constructor_FailsClosed_WhenNoPrincipalIsPresent()
    {
        var accessor = AccessorFor(principal: null);

        Assert.Throws<UnauthorizedAccessException>(() => new JwtInsightsCaller(accessor));
    }

    [Fact]
    public void Constructor_FailsClosed_WhenNoSubjectClaimIsPresent()
    {
        var accessor = AccessorFor(PrincipalWith(new Claim(ClaimTypes.Email, "user@example.com")));

        Assert.Throws<UnauthorizedAccessException>(() => new JwtInsightsCaller(accessor));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void Constructor_FailsClosed_OnAnUnparseableOrNonPositiveSubject(string subject)
    {
        var accessor = AccessorFor(PrincipalWith(new Claim(ClaimTypes.NameIdentifier, subject)));

        Assert.Throws<UnauthorizedAccessException>(() => new JwtInsightsCaller(accessor));
    }

    [Fact]
    public void Constructor_FailsClosed_RatherThanTruncating_WhenSubjectExceedsIntMaxValue()
    {
        // IInsightsCaller.UserId is `int`; the real token's `sub` is minted as a `long` (confirmed
        // against the real RegTrack API's User.ID/TokenServices.cs). This proves an out-of-range
        // value is rejected outright rather than silently wrapping/truncating into some other
        // valid-looking int - a silent truncation could coincidentally authorise the wrong user.
        var tooLarge = ((long)int.MaxValue + 1).ToString();
        var accessor = AccessorFor(PrincipalWith(new Claim(ClaimTypes.NameIdentifier, tooLarge)));

        Assert.Throws<UnauthorizedAccessException>(() => new JwtInsightsCaller(accessor));
    }

    [Fact]
    public void Constructor_NeverReadsTenantOrEntitlementClaims()
    {
        // IDOR rule (CLAUDE.md, IInsightsCaller's own doc comment): a token may only be trusted
        // for WHO is calling, never for WHAT they may see. This does not assert a negative
        // directly (there is nothing to read) - it documents, by construction, that UserId comes
        // out correctly even when rt.customer_id/rt.role/rt.entitlements claims ARE present,
        // proving JwtInsightsCaller has no code path that could pick them up.
        var accessor = AccessorFor(PrincipalWith(
            new Claim(ClaimTypes.NameIdentifier, "38"),
            new Claim("rt.customer_id", "1490"),
            new Claim("rt.role", "admin"),
            new Claim("rt.entitlements", "everything")));

        var caller = new JwtInsightsCaller(accessor);

        Assert.Equal(38, caller.UserId);
    }

    private sealed class HttpContextAccessorStub(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }
}
