namespace Scheduler.Web.Email;

/// <summary>The "Email" configuration section.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Sender address on the dedicated sending subdomain (SPF, DKIM and DMARC set up there).</summary>
    public string From { get; set; } = "scheduler@localhost";
    public string FromName { get; set; } = "Scheduler";

    /// <summary>Public base URL used to build voting and unsubscribe links in emails.</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:7140";
}
