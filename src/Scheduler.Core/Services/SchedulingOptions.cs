namespace Scheduler.Core.Services;

/// <summary>The "Scheduler" configuration section.</summary>
public sealed class SchedulingOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>
    /// Who may create events: exact addresses (<c>rocky@lhotka.net</c>), whole domains (<c>*@marimer.llc</c>),
    /// or <c>*</c> for anyone (development only). An account's <c>CanOrganize</c> flag also grants it.
    /// See docs/design.md, "Abuse prevention".
    /// </summary>
    public List<string> Organizers { get; set; } = [];

    /// <summary>Public base URL of the web app, for links in emails.</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:7140";

    /// <summary>Base64 HMAC key for voting links, at least 32 bytes. Rotating it invalidates every link.</summary>
    public string? MagicLinkKey { get; set; }

    public bool IsAllowlistedOrganizer(string email)
    {
        foreach (var entry in Organizers)
        {
            var pattern = entry.Trim().ToLowerInvariant();
            if (pattern == "*")
                return true;
            if (pattern.StartsWith("*@", StringComparison.Ordinal) && email.EndsWith(pattern[1..], StringComparison.Ordinal))
                return true;
            if (pattern == email)
                return true;
        }
        return false;
    }
}
