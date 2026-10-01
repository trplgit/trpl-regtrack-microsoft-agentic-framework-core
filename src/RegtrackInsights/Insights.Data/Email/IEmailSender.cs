namespace Insights.Data.Email;

public sealed record EmailMessage(string ToAddress, string? ToName, string Subject, string HtmlBody, string FromAddress, string FromName);

public sealed record EmailSendResult(string ProviderUsed);

/// <summary>Provider-agnostic send. Which provider a tenant uses is decided per tenant - see IEmailGatewayResolver and IEmailSenderRegistry.</summary>
public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
