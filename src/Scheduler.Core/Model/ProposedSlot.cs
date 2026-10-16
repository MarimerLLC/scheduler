using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>
/// One candidate start time. The source of truth is <see cref="LocalStart"/> in <see cref="TimeZoneId"/>,
/// what the organizer meant; <see cref="Start"/> is derived from it for sorting and display, and is
/// recomputed if the time-zone database changes. See docs/design.md, "Time handling".
/// </summary>
public sealed class ProposedSlot
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }

    public LocalDateTime LocalStart { get; set; }
    public required string TimeZoneId { get; set; }
    public Instant Start { get; set; }

    /// <summary>1 for the slots the event was created with; incremented by each "propose more".</summary>
    public int Round { get; set; } = 1;

    /// <summary>Set when the organizer withdraws the slot. Its votes are deleted.</summary>
    public Instant? WithdrawnAt { get; set; }
}
