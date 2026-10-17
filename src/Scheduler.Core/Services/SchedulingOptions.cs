namespace Scheduler.Core.Services;

/// <summary>The "Scheduler" configuration section.</summary>
public sealed class SchedulingOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>
    /// Fixed organizer entries: exact addresses (<c>rocky@lhotka.net</c>), whole domains (<c>*@marimer.llc</c>),
    /// or <c>*</c> for anyone (development only). Admins add and remove further entries at runtime; these
    /// can only be changed here. See docs/design.md, "Administration".
    /// </summary>
    public List<string> Organizers { get; set; } = [];

    /// <summary>
    /// Admin addresses (exact addresses only). Admins manage the runtime organizer list. Admin rights exist
    /// only here, so they can't be granted, or all removed, from inside the app.
    /// </summary>
    public List<string> Admins { get; set; } = [];

    /// <summary>Public base URL of the web app, for links in emails.</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:7140";

    /// <summary>Base64 HMAC key for voting links, at least 32 bytes. Rotating it invalidates every link.</summary>
    public string? MagicLinkKey { get; set; }

    /// <summary>Events one organizer may create in any 24 hours. See docs/design.md, "Abuse prevention".</summary>
    public int MaxEventsPerDay { get; set; } = 20;

    /// <summary>
    /// People one organizer may invite in any 24 hours, counted per participant. New-times, reminder and other
    /// emails to people already invited don't count.
    /// </summary>
    public int MaxInvitationsPerDay { get; set; } = 200;

    /// <summary>Whether a configured entry lets <paramref name="email"/> organize.</summary>
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

    /// <summary>Whether <paramref name="caller"/> is an admin: signed in (not by voting link) with a configured address.</summary>
    public bool IsAdmin(CallerIdentity caller) =>
        caller.MagicLinkEventId is null
        && caller.VerifiedEmails.Any(email => Admins.Any(admin => string.Equals(admin.Trim(), email, StringComparison.OrdinalIgnoreCase)));
}
