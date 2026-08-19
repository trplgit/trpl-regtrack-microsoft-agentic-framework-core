namespace Insights.Data.Email;

/// <summary>
/// Elastic Email is primary; SendGrid is the failover when Elastic Email throws (user
/// decision - Elastic Email primary, SendGrid on outage). Only a genuine send failure
/// triggers failover - a rejected/invalid message should fail the same way on both
/// providers, so this does not retry validation errors differently from outages.
/// </summary>
public sealed class FailoverEmailSender(IEmailSender primary, IEmailSender secondary) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            return await primary.SendAsync(message, cancellationToken);
        }
        catch (Exception primaryEx) when (primaryEx is not OperationCanceledException)
        {
            try
            {
                return await secondary.SendAsync(message, cancellationToken);
            }
            catch (Exception secondaryEx) when (secondaryEx is not OperationCanceledException)
            {
                throw new AggregateException("Both email providers failed to send the free digest.", primaryEx, secondaryEx);
            }
        }
    }
}
