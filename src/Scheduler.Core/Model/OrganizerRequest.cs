using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>
/// A signed-in person asking an admin to let them organize. Approving it adds an <see cref="OrganizerGrant"/>
/// for <see cref="Email"/>; declining it stops the person asking again for a while. See docs/design.md,
/// "Requests to organize".
/// </summary>
public sealed class OrganizerRequest
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The verified address to grant: one of the requester's own.</summary>
    public required string Email { get; set; }

    /// <summary>The requester's note, already cleaned and checked by <c>RequestNotes.Clean</c>.</summary>
    public string? Note { get; set; }

    public OrganizerRequestStatus Status { get; set; } = OrganizerRequestStatus.Pending;
    public Instant CreatedAt { get; set; }

    /// <summary>The admin address that approved or declined it.</summary>
    public string? DecidedBy { get; set; }
    public Instant? DecidedAt { get; set; }
}
