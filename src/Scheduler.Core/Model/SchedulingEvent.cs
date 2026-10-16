using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>
/// One meeting being scheduled: the organizer's candidate slots, the people invited to vote on them,
/// and where the poll stands. <see cref="Id"/> correlates everything about it over time — votes, emails,
/// audit entries, and the calendar event created when it is finalized.
/// </summary>
public sealed class SchedulingEvent
{
    /// <summary>Unguessable primary key (a version 7 GUID). Appears in URLs and tool results.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// Short, human-friendly code (e.g. <c>7KQ2M9XD</c>) used in email subjects as <c>[sched:CODE]</c> so
    /// agents reading mail can match a message to its event. Not a secret: access is still checked.
    /// </summary>
    public required string ShortCode { get; set; }

    public required string Title { get; set; }
    public string? Description { get; set; }
    public Duration Duration { get; set; }

    public Guid OrganizerId { get; set; }

    /// <summary>The organizer's IANA zone, used to show slots the way the organizer meant them.</summary>
    public required string OrganizerTimeZone { get; set; }

    public EventState State { get; private set; } = EventState.Draft;
    public Instant? RespondBy { get; set; }
    public Instant CreatedAt { get; set; }

    /// <summary>When the event left the voting states (finalized, cancelled, expired). Drives retention.</summary>
    public Instant? ClosedAt { get; private set; }

    public Guid? FinalizedSlotId { get; private set; }

    /// <summary>The iCalendar UID of the meeting the organizer's agent created, once it reports it.</summary>
    public string? CalendarEventUid { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; set; }

    public List<ProposedSlot> Slots { get; } = [];
    public List<Participant> Participants { get; } = [];

    /// <summary>Slots that haven't been withdrawn.</summary>
    public IEnumerable<ProposedSlot> ActiveSlots => Slots.Where(s => s.WithdrawnAt is null);

    public void Open() => TransitionTo(EventState.Open);

    public void Finalize(Guid slotId, Instant now)
    {
        if (!ActiveSlots.Any(s => s.Id == slotId))
            throw new SchedulingValidationException("That slot isn't one of this event's proposed times.");

        TransitionTo(EventState.Finalized);
        FinalizedSlotId = slotId;
        ClosedAt = now;
    }

    public void Cancel(Instant now)
    {
        TransitionTo(EventState.Cancelled);
        ClosedAt = now;
    }

    public void Expire(Instant now)
    {
        TransitionTo(EventState.Expired);
        ClosedAt = now;
    }

    /// <summary>
    /// Back to voting: rescheduling a finalized event, or proposing more slots after it expired. The
    /// event keeps its ID, so emails and the calendar UID stay correlated.
    /// </summary>
    public void Reopen()
    {
        TransitionTo(EventState.Open);
        FinalizedSlotId = null;
        ClosedAt = null;
    }

    public void EnsureAcceptingVotes()
    {
        if (State != EventState.Open)
            throw new SchedulingConflictException($"This event is {State.ToString().ToLowerInvariant()} and isn't taking votes.");
    }

    public void EnsureEditable()
    {
        if (State is not (EventState.Draft or EventState.Open))
            throw new SchedulingConflictException($"This event is {State.ToString().ToLowerInvariant()} and can't be changed.");
    }

    public static bool CanTransition(EventState from, EventState to) => (from, to) switch
    {
        (EventState.Draft, EventState.Open) => true,
        (EventState.Draft, EventState.Cancelled) => true,
        (EventState.Open, EventState.Finalized) => true,
        (EventState.Open, EventState.Cancelled) => true,
        (EventState.Open, EventState.Expired) => true,
        (EventState.Finalized, EventState.Open) => true,
        (EventState.Finalized, EventState.Cancelled) => true,
        (EventState.Expired, EventState.Open) => true,
        _ => false
    };

    private void TransitionTo(EventState next)
    {
        if (!CanTransition(State, next))
            throw new SchedulingConflictException(
                $"Can't move this event from {State.ToString().ToLowerInvariant()} to {next.ToString().ToLowerInvariant()}.");
        State = next;
    }
}
