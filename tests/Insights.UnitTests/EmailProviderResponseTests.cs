using System.Net;
using Insights.Data.Email;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// [REGRESSION] ElasticEmailSender/SendGridEmailSender used to call plain
/// response.EnsureSuccessStatusCode() on a failed send, discarding the provider's response BODY -
/// exactly where "invalid API key", "recipient blocked", "quota exceeded" etc. actually live. A
/// send failure was undiagnosable beyond a bare HTTP status code. These tests pin that the
/// provider's error body now survives into the thrown exception.
/// </summary>
public sealed class EmailProviderResponseTests
{
    [Fact]
    public async Task EnsureSuccessAsync_OnSuccess_DoesNothing()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        await EmailProviderResponse.EnsureSuccessAsync(response, "ElasticEmail", CancellationToken.None);
        // No exception - reaching here is the assertion.
    }

    [Fact]
    public async Task EnsureSuccessAsync_OnFailure_ThrowsWithTheProvidersOwnResponseBody()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"Invalid recipient address"}"""),
        };

        var ex = await Assert.ThrowsAsync<EmailProviderException>(
            () => EmailProviderResponse.EnsureSuccessAsync(response, "ElasticEmail", CancellationToken.None));

        Assert.Equal("ElasticEmail", ex.ProviderName);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("Invalid recipient address", ex.ResponseBody);
        Assert.Contains("Invalid recipient address", ex.Message);
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public async Task EnsureSuccessAsync_TruncatesAnUnexpectedlyLargeErrorBody()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(new string('x', 5000)),
        };

        var ex = await Assert.ThrowsAsync<EmailProviderException>(
            () => EmailProviderResponse.EnsureSuccessAsync(response, "SendGrid", CancellationToken.None));

        Assert.True(ex.ResponseBody.Length < 1100, $"expected the body to be truncated, got {ex.ResponseBody.Length} chars");
        Assert.Contains("truncated", ex.ResponseBody);
    }
}
