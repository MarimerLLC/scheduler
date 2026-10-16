using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>
/// Permission to create events, granted by an admin: an exact address (<c>bob@example.com</c>) or a whole
/// domain (<c>*@marimer.llc</c>). Together with the configured <c>Scheduler:Organizers</c> list, these are the
/// organizer allowlist. See docs/design.md, "Administration".
/// </summary>
public sealed class OrganizerGrant
{
    /// <summary>Normalized pattern; see <c>OrganizerPatterns.Normalize</c>.</summary>
    public required string Pattern { get; set; }

    /// <summary>Optional reminder of why ("Contoso partner team").</summary>
    public string? Note { get; set; }

    /// <summary>The admin address that added it.</summary>
    public required string GrantedBy { get; set; }
    public Instant GrantedAt { get; set; }
}
