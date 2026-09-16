using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Insights.Api;

/// <summary>
/// The real IInsightsCaller implementation, over the ClaimsPrincipal ASP.NET Core's JWT Bearer
/// middleware has already validated by the time any endpoint handler runs (signature, issuer,
/// audience, expiry - see Insights.Worker.Auth.JwtAuthRegistration for the validation parameters).
/// This class does not parse, decode or verify a token itself; it only reads claims middleware has
/// already proven genuine.
///
/// [TRAP - the one most likely to bite] ASP.NET Core's DEFAULT inbound claim mapping rewrites the
/// JWT "sub" claim to ClaimTypes.NameIdentifier before a handler ever sees it. Reading
/// FindFirst("sub") alone returns null against a real RegTrack token for exactly that reason -
/// confirmed against the actual RegTrack API's token-minting code (TokenServices.cs) and its own
/// controllers, which all read ClaimTypes.NameIdentifier first, "sub" second, for the same reason.
///
/// [TRAP] The token also carries rt.customer_id, rt.role and rt.entitlements claims. NONE of them
/// are read here, on purpose - IInsightsCaller's own doc comment and this codebase's IDOR rule
/// both say a token may only be trusted for WHO is calling, never for WHAT they may see. Tenant
/// eligibility is, and must stay, a live per-request database check
/// (ITenantDirectoryRepository.IsEligibleAsync) - adding TenantId or Entitlements to this class or
/// to IInsightsCaller would make that guard bypassable by whatever claims happen to be embedded in
/// a token at mint time.
///
/// Fails CLOSED: a missing claim, an unparseable value, or a value outside int's range throws
/// UnauthorizedAccessException rather than falling back to any default. There is no equivalent of
/// the dev harness's HeaderInsightsCaller.Insights:DevUserId fallback here, deliberately - that
/// fallback is exactly the shortcut this class exists to retire.
/// </summary>
public sealed class JwtInsightsCaller : IInsightsCaller
{
    public JwtInsightsCaller(IHttpContextAccessor accessor)
    {
        var principal = accessor.HttpContext?.User
            ?? throw new UnauthorizedAccessException("No authenticated principal on the current request.");

        // ClaimTypes.NameIdentifier first - see the class doc comment on the default inbound claim
        // mapping. "sub" is checked second only in case that mapping is ever disabled upstream.
        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(subject))
            throw new UnauthorizedAccessException("Token carries no subject (user id) claim.");

        // The real signing authority (RegTrack's token minting) declares this a `long`
        // (User.ID). IInsightsCaller.UserId stays `int` - every repository, SQL proc parameter
        // and run-id/enqueue path already commits to that width, and widening it cascades through
        // all of them for no present benefit. Parse as long and reject anything outside int's
        // range explicitly rather than truncating - a silent truncation could authorise the wrong
        // user by coincidence of arithmetic. If this exception is ever logged for real, it is
        // unambiguous evidence the `int` assumption has expired and needs revisiting deliberately,
        // not discovered as data corruption downstream.
        if (!long.TryParse(subject, out var userId64) || userId64 <= 0)
            throw new UnauthorizedAccessException($"Token subject '{subject}' is not a valid positive user id.");

        if (userId64 > int.MaxValue)
            throw new UnauthorizedAccessException(
                $"Token subject {userId64} exceeds int.MaxValue - IInsightsCaller.UserId can no longer safely be an int.");

        UserId = (int)userId64;
    }

    public int UserId { get; }
}
