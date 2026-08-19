using System.Net.Http.Json;

namespace Insights.Data.Email;

/// <summary>Primary provider (Email:ElasticEmail:ApiKeySecretName - Key Vault, never appsettings). Elastic Email v4 REST API.</summary>
public sealed class ElasticEmailSender(HttpClient httpClient, string apiKey) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elasticemail.com/v4/emails");
        request.Headers.Add("X-ElasticEmail-ApiKey", apiKey);
        request.Content = JsonContent.Create(new
        {
            Recipients = new[] { new { Email = message.ToAddress } },
            Content = new
            {
                Body = new[] { new { ContentType = "HTML", Content = message.HtmlBody } },
                Subject = message.Subject,
                From = $"{message.FromName} <{message.FromAddress}>",
            },
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return new EmailSendResult("ElasticEmail");
    }
}
