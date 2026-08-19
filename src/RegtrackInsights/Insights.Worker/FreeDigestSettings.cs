namespace Insights.Worker;

/// <summary>
/// Everything the digest needs from configuration, resolved once at startup instead of being
/// passed as literals at each call site. Bound from Budget:*, Email:* and Agents:* - see
/// FreeDigestRegistration.
/// </summary>
public sealed class FreeDigestSettings
{
    /// <summary>Budget:FreeDigestTokenCap. Over budget = skip the LLM and send the template; the email still goes out.</summary>
    public required int TokenCap { get; init; }

    /// <summary>Email:FromAddress. Must be a sender verified with the provider, or every send is rejected.</summary>
    public required string FromAddress { get; init; }

    /// <summary>Email:FromName.</summary>
    public required string FromName { get; init; }

    /// <summary>Email:UpgradeUrl - the conversion link. The gap between a number and its explanation is the pitch.</summary>
    public required string UpgradeUrl { get; init; }

    /// <summary>Email:UnsubscribeBaseUrl.</summary>
    public required string UnsubscribeBaseUrl { get; init; }

    /// <summary>
    /// NON-PRODUCTION SAFETY VALVE. When set, every digest is delivered to this address instead
    /// of the recipient the database resolved.
    ///
    /// UAT is typically a copy of production, so its User table holds REAL customer addresses.
    /// Without this, a single RunWeeklyAsync against UAT emails real people a test digest
    /// carrying placeholder upgrade links. Recipients are still resolved, scoped and rendered
    /// exactly as in production - only the destination changes, so the run still proves the
    /// per-recipient path.
    ///
    /// MUST be empty in production. FreeDigestService logs a warning on every run while it is set.
    /// </summary>
    public string? RecipientOverride { get; init; }

    /// <summary>The address a digest should actually be delivered to, honouring <see cref="RecipientOverride"/>.</summary>
    public string ResolveDeliveryAddress(string resolvedRecipientEmail) =>
        string.IsNullOrWhiteSpace(RecipientOverride) ? resolvedRecipientEmail : RecipientOverride;

    /// <summary>
    /// Per-recipient unsubscribe link.
    ///
    /// TODO (spec Section 5.4): this is currently an addressing scheme only - there is no durable
    /// opt-out store behind it yet, and sql/06 still has the TODO where opt-outs should be
    /// subtracted from the recipient count. Opt-out MUST survive tier changes, or an
    /// upgrade/downgrade cycle silently re-subscribes someone who asked to stop. Replace the
    /// query string with a signed, non-guessable token when that store lands.
    /// </summary>
    public string BuildUnsubscribeUrl(int customerId, long userId)
    {
        if (string.IsNullOrWhiteSpace(UnsubscribeBaseUrl))
            return string.Empty;

        var separator = UnsubscribeBaseUrl.Contains('?') ? "&" : "?";
        return $"{UnsubscribeBaseUrl}{separator}c={customerId}&u={userId}";
    }
}

