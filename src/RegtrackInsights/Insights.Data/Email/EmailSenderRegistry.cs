namespace Insights.Data.Email;

/// <summary>The sender for a resolved <see cref="EmailGateway"/>. Each one is already rate-limited.</summary>
public interface IEmailSenderRegistry
{
    IEmailSender For(EmailGateway gateway);
}

/// <summary>
/// One long-lived, individually rate-limited sender per provider (FreeDigestRegistration builds
/// them). The limiter sits on EACH provider, never in front of the registry - otherwise SendGrid
/// traffic would spend the shared Elastic Email account's good-citizen share, and vice versa.
/// </summary>
public sealed class EmailSenderRegistry(IReadOnlyDictionary<EmailGateway, IEmailSender> senders) : IEmailSenderRegistry, IDisposable
{
    public IEmailSender For(EmailGateway gateway) =>
        senders.TryGetValue(gateway, out var sender)
            ? sender
            : throw new InvalidOperationException($"No email sender is registered for gateway {gateway} ({(int)gateway}).");

    public void Dispose()
    {
        foreach (var sender in senders.Values)
            (sender as IDisposable)?.Dispose();
    }
}
