using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>
/// Someone invited to vote, identified by the email address the organizer used. Votes belong to that
/// address; a signed-in user may vote for any participant whose email is one of their verified addresses.
/// </summary>
public sealed class Participant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }

    /// <summary>Normalized (trimmed, lower-case) address.</summary>
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public AttendeeRole Role { get; set; } = AttendeeRole.Required;

    /// <summary>
    /// Random value the participant's voting-link token is derived from (see <c>MagicLinks</c>). The token
    /// itself is never stored; replacing the nonce revokes the link.
    /// </summary>
    public required byte[] TokenNonce { get; set; }

    public Instant InvitedAt { get; set; }
    public Instant? LastRemindedAt { get; set; }
    public Instant? RespondedAt { get; set; }

    /// <summary>Optional short note to the organizer ("Tuesdays are hard for me").</summary>
    public string? Note { get; set; }

    public List<Vote> Votes { get; } = [];
}
