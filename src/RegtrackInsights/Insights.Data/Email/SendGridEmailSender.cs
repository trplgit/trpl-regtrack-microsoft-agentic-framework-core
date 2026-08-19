using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Insights.Data.Email;

/// <summary>Failover provider (Email:SendGrid:ApiKeySecretName - Key Vault, never appsettings). SendGrid v3 mail/send.</summary>
public sealed class SendGridEmailSender(HttpClient httpClient, string apiKey) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            personalizations = new[] { new { to = new[] { new { email = message.ToAddress } } } },
            from = new { email = message.FromAddress, name = message.FromName },
            subject = message.Subject,
            content = new[] { new { type = "text/html", value = message.HtmlBody } },
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return new EmailSendResult("SendGrid");
    }
}
