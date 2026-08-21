using System.Security.Cryptography;
using System.Text;

namespace Insights.Domain;

/// <summary>
/// The token on an unsubscribe link - the only thing proving the click came from the person the
/// email was sent to.
///
/// -- WHY THIS EXISTS ------------------------------------------------------------------------
/// An unsubscribe URL of the form ?c=23&amp;u=357 is ENUMERABLE: anyone can increment the numbers
/// and unsubscribe every recipient in the estate. Unsubscribing is durable and survives tier
/// changes (spec 5.4), so a sweep is not something an admin can undo by re-enabling a product
/// mapping - the suppression rows have to be found and deleted one by one.
///
/// So the link carries an HMAC over the identity it claims. No lookup, no state, no expiry: a
/// recipient may unsubscribe from a six-month-old email, which is what a reader expects and what
/// regulators require.
///
/// -- THE ENDPOINT LIVES IN THE REGTRACK API --------------------------------------------------
/// This worker has no ingress (CLAUDE.md 6). Whoever hosts the endpoint validates with Verify
/// using the same signing key, then calls IFreeDigestRepository.SuppressAsync. This type is the
/// contract between the two.
/// </summary>
public static class UnsubscribeToken
{
    /// <summary>Signs (customerId, userId) and returns a URL-safe token.</summary>
    public static string Create(string signingKey, int customerId, long userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(Payload(customerId, userId)));

        // URL-safe base64: the token sits in a query string that email clients rewrite freely.
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// True when the token was issued for this recipient with this key.
    ///
    /// Compared in CONSTANT TIME. An ordinary string comparison returns as soon as two bytes
    /// differ, and that timing difference is enough to recover a valid token byte by byte.
    /// </summary>
    public static bool Verify(string signingKey, int customerId, long userId, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var expected = Create(signingKey, customerId, userId);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token));
    }

    /// <summary>
    /// Colon-delimited so (1, 23) and (12, 3) cannot produce the same signed payload - a
    /// separator-free concatenation would sign "123" for both.
    /// </summary>
    private static string Payload(int customerId, long userId) => $"unsubscribe:{customerId}:{userId}";
}
