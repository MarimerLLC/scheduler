using System.Data.Common;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Scheduler.Core.Email;

namespace Scheduler.Web.Email;

/// <summary>
/// SMTP delivery with MailKit: to the relay in <see cref="SmtpOptions"/> in production, or to Mailpit under Aspire
/// (connection string <c>ConnectionStrings:mailpit</c>, <c>Endpoint=smtp://host:port</c>, no TLS or login). A recipient
/// the relay refuses outright (a 5xx reply to RCPT TO) is reported as <see cref="PermanentDeliveryException"/>,
/// so the address is suppressed instead of retried. Bounces that arrive later, by mail, aren't seen: nothing
/// reads the sending domain's inbox. See docs/design.md, "Delivery".
/// </summary>
internal sealed class SmtpEmailSender(SmtpOptions smtp, IOptions<EmailOptions> options) : IEmailSender
{
    /// <summary>Mailpit's endpoint from the Aspire connection string, as SMTP options; null if there isn't one.</summary>
    public static SmtpOptions? FromConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return builder.TryGetValue("Endpoint", out var value) && Uri.TryCreate(value?.ToString(), UriKind.Absolute, out var uri)
            ? new SmtpOptions { Host = uri.Host, Port = uri.Port, Security = SecureSocketOptions.None }
            : null;
    }

    public async Task<string?> SendAsync(OutboundEmail message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var mail = new MimeMessage();
        mail.From.Add(new MailboxAddress(settings.FromName, settings.From));
        mail.To.Add(MailboxAddress.Parse(message.To));
        if (message.ReplyTo is not null)
            mail.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
        mail.Subject = message.Subject;
        mail.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(DomainOf(settings.From));
        if (message.ListUnsubscribeUrl is not null)
        {
            // RFC 8058 one-click unsubscribe: an HTTPS endpoint, not a mailbox.
            mail.Headers.Add("List-Unsubscribe", $"<{message.ListUnsubscribeUrl}>");
            mail.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }
        mail.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(smtp.Host!, smtp.Port, smtp.Security, cancellationToken);
        if (!string.IsNullOrEmpty(smtp.Username))
            await client.AuthenticateAsync(smtp.Username, smtp.Password ?? "", cancellationToken);
        try
        {
            await client.SendAsync(mail, cancellationToken);
        }
        catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted && (int)ex.StatusCode >= 500)
        {
            throw new PermanentDeliveryException($"{(int)ex.StatusCode} {ex.Message}", ex);
        }
        finally
        {
            await client.DisconnectAsync(quit: true, CancellationToken.None);
        }
        return mail.MessageId;
    }

    private static string DomainOf(string address) =>
        address.LastIndexOf('@') is var at and >= 0 ? address[(at + 1)..] : "localhost";
}

/// <summary>
/// Used in Development when there is no Mailpit. Fails every send, so messages stay in the outbox (and eventually
/// show as failed) instead of being reported as delivered.
/// </summary>
internal sealed class LogOnlyEmailSender(ILogger<LogOnlyEmailSender> logger) : IEmailSender
{
    public Task<string?> SendAsync(OutboundEmail message, CancellationToken cancellationToken)
    {
        logger.LogWarning("No email delivery configured; not sending '{Subject}' to {To}", message.Subject, message.To);
        throw new InvalidOperationException("No email delivery is configured.");
    }
}
