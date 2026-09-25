using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Insights.Data.Email;

/// <summary>SendGrid v3 mail/send (EmailGateway.SendGrid, EmailGatewayMaster ID 2). Key: Email:SendGrid:ApiKey. The FromAddress must be a verified SendGrid sender or every send is rejected.</summary>
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
        await EmailProviderResponse.EnsureSuccessAsync(response, "SendGrid", cancellationToken);
        return new EmailSendResult("SendGrid");
    }
}
