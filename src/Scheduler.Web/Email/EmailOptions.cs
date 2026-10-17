using MailKit.Security;

namespace Scheduler.Web.Email;

/// <summary>The "Email" configuration section.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Sender address on the dedicated sending subdomain (SPF, DKIM and DMARC set up there).</summary>
    public string From { get; set; } = "scheduler@localhost";
    public string FromName { get; set; } = "Scheduler";

    /// <summary>The SMTP relay production sends through. Required outside Development.</summary>
    public SmtpOptions Smtp { get; set; } = new();
}

/// <summary>"Email:Smtp": a relay that accepts mail for the sending domain. See docs/deployment.md.</summary>
public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;

    /// <summary>
    /// <c>StartTls</c> (port 587, the default), <c>SslOnConnect</c> (465), <c>Auto</c>, or <c>None</c> (only for a
    /// local test server such as Mailpit).
    /// </summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    public string? Username { get; set; }
    public string? Password { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
