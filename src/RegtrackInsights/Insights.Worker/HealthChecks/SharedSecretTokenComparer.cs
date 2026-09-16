using System.Security.Cryptography;
using System.Text;

namespace Insights.Worker.HealthChecks;

/// <summary>
/// Same shape as the sibling RegTrack API's SharedSecretTokenComparer (trpl-regtrack-dot-net-core-api,
/// Middleware/SharedSecretTokenComparer.cs) - kept byte-for-byte identical on purpose so the two
/// codebases' shared-secret gates behave identically and any future change to one is an obvious
/// prompt to check the other.
///
/// Encodes a configured token once and compares candidates to it in constant time, so the secret
/// cannot be recovered by timing a byte-by-byte comparison.
/// </summary>
internal static class SharedSecretTokenComparer
{
    /// <summary>
    /// Encodes a configured token for later comparison, or null when none is configured (missing,
    /// blank, or whitespace-only) - the null case is what a caller uses to decide whether its gate
    /// is enabled at all. Trimmed: a Kubernetes Secret or an operator-typed literal commonly
    /// carries incidental leading/trailing whitespace that was never meant to be part of the value.
    /// </summary>
    internal static byte[]? Encode(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : Encoding.UTF8.GetBytes(token.Trim());

    /// <summary>
    /// True when <paramref name="candidate"/> equals the token <paramref name="expectedToken"/> was
    /// encoded from. A null <paramref name="expectedToken"/> or an empty <paramref name="candidate"/>
    /// is never a match. FixedTimeEquals returns false on a length mismatch rather than throwing.
    /// </summary>
    internal static bool Matches(byte[]? expectedToken, string? candidate)
    {
        if (expectedToken is null || string.IsNullOrEmpty(candidate))
            return false;

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), expectedToken);
    }
}
