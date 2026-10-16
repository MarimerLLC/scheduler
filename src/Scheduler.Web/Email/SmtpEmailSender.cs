using System.Data.Common;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using Scheduler.Core.Email;

namespace Scheduler.Web.Email;

/// <summary>
/// Plain SMTP delivery, used against Mailpit in development. The endpoint comes from the Aspire
/// connection string <c>ConnectionStrings:mailpit</c> (<c>Endpoint=smtp://host:port</c>). Production should
/// use a hosted provider's sender so bounces and complaints come back as webhooks.
/// </summary>
internal sealed class SmtpEmailSender(Uri endpoint, IOptions<EmailOptions> options) : IEmailSender
{
    public static Uri? EndpointFromConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return builder.TryGetValue("Endpoint", out var value) && Uri.TryCreate(value?.ToString(), UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    public async Task<string?> SendAsync(OutboundEmail message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var mail = new MailMessage
        {
            From = new MailAddress(settings.From, settings.FromName),
            Subject = message.Subject,
            Body = message.TextBody,
            IsBodyHtml = false,
        };
        mail.To.Add(message.To);
        if (message.ReplyTo is not null)
            mail.ReplyToList.Add(message.ReplyTo);

        if (message.HtmlBody is not null)
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.HtmlBody, null, "text/html"));

        if (message.ListUnsubscribeUrl is not null)
        {
            // RFC 8058 one-click unsubscribe: an HTTPS endpoint, not a mailbox.
            mail.Headers.Add("List-Unsubscribe", $"<{message.ListUnsubscribeUrl}>");
            mail.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }

        using var client = new SmtpClient(endpoint.Host, endpoint.Port);
        await client.SendMailAsync(mail, cancellationToken);
        return null;
    }
}

/// <summary>
/// Used when no delivery is configured. Fails every send, so messages stay in the outbox (and eventually
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
