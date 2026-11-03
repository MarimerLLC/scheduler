using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>
/// A time a participant suggests that isn't on the poll yet. Only the organizer can put it on the poll (as a
/// <see cref="ProposedSlot"/> in a new round) or dismiss it; the participant's suggestion alone changes nothing
/// anyone votes on. Like a slot, <see cref="LocalStart"/> in <see cref="TimeZoneId"/> is the source of truth and
/// <see cref="Start"/> is derived. See docs/design.md, "Suggested times".
/// </summary>
public sealed class SlotSuggestion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EventId { get; set; }

    /// <summary>The participant record of the person who suggested it.</summary>
    public Guid ParticipantId { get; set; }

    public LocalDateTime LocalStart { get; set; }

    /// <summary>The zone the participant gave the time in (their own, usually), not necessarily the organizer's.</summary>
    public required string TimeZoneId { get; set; }

    public Instant Start { get; set; }
    public Instant SuggestedAt { get; set; }

    public SuggestionStatus Status { get; set; } = SuggestionStatus.Pending;

    /// <summary>When the organizer added or dismissed it.</summary>
    public Instant? DecidedAt { get; set; }

    /// <summary>The proposed slot it became, once added.</summary>
    public Guid? SlotId { get; set; }
}
