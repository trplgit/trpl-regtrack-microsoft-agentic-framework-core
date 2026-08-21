using System.Text.Json.Serialization;

namespace Insights.Contracts;

/// <summary>
/// Bounce notification from the mail provider, posted to the RegTrack API's bounce webhook
/// (design doc 10.8).
///
/// Lives in Contracts because that is what the API references - a copied DTO would drift the
/// moment either side changed, and this one is a wire contract with a third party.
///
/// Property names are PascalCase to match this repo; the JSON names are snake_case to match the
/// API's existing request DTOs and the provider's payload.
///
/// [VERIFY] The shape is provisional - check it against the provider's webhook documentation
/// before wiring up. If they do not echo custom fields, CustomerId and UserId have to be attached
/// to the outbound message as custom headers and read back out here.
/// </summary>
public sealed class DigestBounceRequest
{
    [JsonPropertyName("customer_id")]
    public int CustomerId { get; set; }

    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    /// <summary>"hard" or "soft". ONLY "hard" suppresses - a soft bounce is a full mailbox or a temporary outage.</summary>
    [JsonPropertyName("bounce_type")]
    public string BounceType { get; set; } = string.Empty;

    /// <summary>Provider diagnostic, e.g. "550 mailbox unavailable". Stored against the suppression for support.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
