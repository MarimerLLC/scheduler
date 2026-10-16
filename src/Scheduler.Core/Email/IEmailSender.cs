namespace Scheduler.Core.Email;

/// <summary>One message as handed to the delivery provider.</summary>
public sealed record OutboundEmail(
    string To,
    string? ReplyTo,
    string Subject,
    string TextBody,
    string? HtmlBody,
    string? ListUnsubscribeUrl);

/// <summary>
/// Outbound delivery. The server never receives mail: bounces and complaints come back from the provider
/// as webhooks. Implementations: SMTP (Mailpit in development) now; a hosted provider for production.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends <paramref name="message"/> and returns the provider's message ID, if it gives one.</summary>
    Task<string?> SendAsync(OutboundEmail message, CancellationToken cancellationToken);
}
