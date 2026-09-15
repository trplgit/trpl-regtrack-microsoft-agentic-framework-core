using System.Security.Cryptography;
using System.Text;

namespace Insights.Domain;

/// <summary>
/// The Durable Task instance id for a paid report run, and the runId the API puts on the wire.
///
/// Format: <c>insights-{tenantId}-{keyHash}</c>, where keyHash is SHA-256 over the canonical
/// <c>(scopeDescriptor, reportType, period)</c> key.
///
/// It does two jobs at once, deliberately:
///
///  1. <b>The one-active-run-per-key lock (spec §4.5), for free.</b> Two enqueues of the same key
///     produce the same instance id, and DTFx attaches the second to the running instance instead
///     of starting a duplicate. A double-click costs nothing. No lock table, no lease, no cleanup
///     job - the same trick the free digest uses with <c>freedigest-{customerId}-{weekEnding}</c>.
///
///  2. <b>It carries the tenant.</b> API_CONTRACTS.md §4's URL is
///     <c>GET /api/insights/runs/{runId}/stream</c> - runId only, no tenantId. But cross-cutting
///     rule 1 says the server re-derives eligibility on EVERY request, and it cannot do that
///     without knowing which tenant the run belongs to.
///
/// [TRAP] Because the id is derived, it is GUESSABLE - anyone who knows a tenant id and a report
/// key can construct it. That is fine, and it is why <see cref="TryParse"/> exists: the tenant id
/// it returns is an ASSERTION TO BE CHECKED, never a grant. The caller must run it through
/// ITenantDirectoryRepository.IsEligibleAsync before returning one byte of run state. Treating a
/// parsed run id as proof of access turns a convenience into an IDOR hole.
/// </summary>
public static class InsightsRunId
{
    private const string Prefix = "insights";

    /// <summary>
    /// Builds the instance id for a report key. Deterministic: the same key always yields the
    /// same id, which is exactly what makes it the idempotency lock.
    /// </summary>
    public static string For(int tenantId, string scopeDescriptor, string reportType, string period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeDescriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportType);
        ArgumentException.ThrowIfNullOrWhiteSpace(period);

        /*  Ordinal, lower-cased, pipe-separated. The separator must be a character that cannot
            appear in any component, or "a|b" and "a" + "|b" would collide into one cooldown
            bucket - two different reports sharing a 30-day lock.                               */
        var canonical = string.Join('|',
            tenantId.ToString(),
            scopeDescriptor.Trim().ToLowerInvariant(),
            reportType.Trim().ToLowerInvariant(),
            period.Trim().ToLowerInvariant());

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        return $"{Prefix}-{tenantId}-{hash}";
    }

    /// <summary>
    /// Recovers the tenant id a run id claims to belong to. Returns false on anything malformed.
    ///
    /// The name says <c>TryParse</c> and not <c>Authorize</c> on purpose: a true return means the
    /// string is well-formed, nothing more.
    /// </summary>
    public static bool TryParse(string? runId, out int tenantId)
    {
        tenantId = 0;

        if (string.IsNullOrWhiteSpace(runId))
            return false;

        var parts = runId.Split('-');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            return false;

        if (!int.TryParse(parts[1], out var parsed) || parsed <= 0)
            return false;

        // The hash half must look like a hash. Without this, "insights-23-" parses happily and
        // every malformed id collapses onto tenant 23's namespace.
        if (parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit))
            return false;

        tenantId = parsed;
        return true;
    }
}
