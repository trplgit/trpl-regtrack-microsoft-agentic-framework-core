namespace Insights.Data.Email;

/// <summary>
/// [BUG FOUND LIVE, 2026-09-11] Both ElasticEmailSender and SendGridEmailSender used to call
/// plain <c>response.EnsureSuccessStatusCode()</c> on a failed send, which throws
/// <c>HttpRequestException: Response status code does not indicate success: 400 (Bad Request).</c>
/// - discarding the provider's own response BODY, which is exactly where "invalid API key",
/// "recipient blocked/suppressed", "quota exceeded" or a field-validation message actually lives.
/// A free-digest send failure landed in FreeDigestSendOrchestrator's retry/DLQ path completely
/// undiagnosable - the status code alone cannot distinguish a transient provider blip (worth
/// retrying, which the caller's RetryOptions already does) from a permanently bad request (retrying
/// three times wastes time and still fails the same way). Reading the body on failure and folding
/// it into the exception message costs nothing on the success path (the body is only read when
/// IsSuccessStatusCode is false) and turns "why did this recipient's send fail" from a guess into
/// something visible in the very first log line.
/// </summary>
internal static class EmailProviderResponse
{
    private const int MaxBodyCharsInMessage = 1000; // enough for any provider's validation-error JSON; a huge body (unexpected HTML error page, etc.) must not blow up the exception message itself.

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string providerName, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > MaxBodyCharsInMessage)
            body = body[..MaxBodyCharsInMessage] + "... (truncated)";

        throw new EmailProviderException(providerName, response.StatusCode, body);
    }
}

/// <summary>Carries the provider's own error body, not just the HTTP status code - see EmailProviderResponse's doc comment for why that distinction matters here.</summary>
public sealed class EmailProviderException(string providerName, System.Net.HttpStatusCode statusCode, string responseBody)
    : Exception($"{providerName} send failed with {(int)statusCode} {statusCode}: {responseBody}")
{
    public string ProviderName { get; } = providerName;
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}
