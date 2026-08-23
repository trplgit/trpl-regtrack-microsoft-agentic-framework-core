namespace Insights.Api;

/// <summary>
/// The authenticated caller, as the host sees them.
///
/// This is a SEAM, not an auth system. RegTrack already authenticates; the paid-tier endpoints
/// need one thing from it - who is asking - and this is the smallest surface that expresses that
/// without dragging a second identity stack into the feature (CLAUDE.md §6).
///
/// The host implements it over its own principal, typically:
///
///     services.AddHttpContextAccessor();
///     services.AddScoped&lt;IInsightsCaller, RegTrackInsightsCaller&gt;();
///
/// [TRAP] UserId MUST come from the validated token or session, never from a query string, header
/// or request body. Every eligibility decision in this feature is derived from it, so a
/// client-supplied user id would make the IDOR guard authorise the attacker as their own victim.
/// </summary>
public interface IInsightsCaller
{
    /// <summary>The authenticated user's <c>User.ID</c>.</summary>
    int UserId { get; }
}
