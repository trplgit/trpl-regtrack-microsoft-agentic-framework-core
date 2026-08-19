namespace Insights.Domain;

/// <summary>Where the digest body came from - the fallback path must always be available (spec Section 10.5).</summary>
public enum FreeDigestSource
{
    Llm,
    Fallback,
}

/// <summary>
/// The finished free-digest email, ready to render into the HTML shell and send.
/// <paramref name="SkippedReason"/> is set only when <see cref="Source"/> is
/// <see cref="FreeDigestSource.Fallback"/> - e.g. "token budget exceeded" or a
/// validator failure - so the run is observable without leaking internals to the recipient.
/// </summary>
public sealed record FreeDigestEmail(string Body, FreeDigestSource Source, string? SkippedReason, int InputTokens, int OutputTokens);

/// <summary>
/// Result of validating an LLM-written digest body against the closed set of 15
/// aggregates it was given. Any failure means the fallback template is substituted -
/// see docs/templates/free_digest_email.md "Validation before send".
/// </summary>
public sealed record FreeDigestValidationResult(bool IsValid, IReadOnlyList<string> FailedChecks)
{
    public static readonly FreeDigestValidationResult Valid = new(true, []);
}

/// <summary>Outcome of one recipient's free-digest run - what the pipeline did and why, for insights.digest.sent_total/skipped_total.</summary>
public sealed record FreeDigestRunResult(bool Sent, FreeDigestSource? Source, string? Reason, string? ProviderUsed, string? Body = null, string? Html = null);
