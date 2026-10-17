namespace Scheduler.Core.Email;

/// <summary>The receiving side refused the address for good (an SMTP 5xx to RCPT TO): don't retry, suppress it.</summary>
public sealed class PermanentDeliveryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One message as handed to the delivery provider.</summary>
public sealed record OutboundEmail(
    string To,
    string? ReplyTo,
    string Subject,
    string TextBody,
    string? HtmlBody,
    string? ListUnsubscribeUrl);

/// <summary>
/// Outbound delivery: SMTP through a relay in production, Mailpit in development. The server never receives
/// mail, so only refusals during sending are seen as bounces: an implementation throws
/// <see cref="PermanentDeliveryException"/> for an address refused for good, and anything else is retried.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends <paramref name="message"/> and returns the provider's message ID, if it gives one.</summary>
    Task<string?> SendAsync(OutboundEmail message, CancellationToken cancellationToken);
}
