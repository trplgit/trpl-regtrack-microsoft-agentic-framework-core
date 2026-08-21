namespace Insights.Domain;

/// <summary>
/// Why a recipient no longer receives the digest.
///
/// Unsubscribe and hard bounce share one store because they have the same effect - stop sending -
/// and differ only in provenance. Two stores would mean two places to check, and a recipient
/// missing from one of them still gets mail.
/// </summary>
public enum DigestSuppressionReason
{
    /// <summary>The recipient asked to stop. Durable, and survives tier changes (spec 5.4).</summary>
    Unsubscribed,

    /// <summary>The provider reported the address as permanently undeliverable (spec 10.8).</summary>
    HardBounce,

    /// <summary>Suppressed by ops.</summary>
    Manual,
}

/// <summary>One suppressed recipient.</summary>
public sealed record DigestSuppression(
    int CustomerId,
    long UserId,
    string? Email,
    DigestSuppressionReason Reason,
    DateTime SuppressedAtUtc,
    string? Detail);

public static class DigestSuppressionReasonExtensions
{
    /// <summary>The values the CHECK constraint on InsightsDigestSuppression accepts.</summary>
    public static string ToSqlValue(this DigestSuppressionReason reason) => reason switch
    {
        DigestSuppressionReason.Unsubscribed => "unsubscribed",
        DigestSuppressionReason.HardBounce => "hard_bounce",
        DigestSuppressionReason.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
