namespace Scheduler.Core.Services;

/// <summary>
/// Every scheduling rule lives behind this interface. The MCP tools and the Blazor pages are thin
/// wrappers over it, so agents and people always get the same behavior. <c>eventRef</c> parameters accept
/// either the event's ID or its short code. Organizer edits take the <c>Version</c> from the view the
/// caller acted on, and fail with a conflict if the event changed since.
/// </summary>
public interface ISchedulingService
{
    /// <summary>Creates and opens an event, and queues invitation emails. Caller must be an allowlisted organizer.</summary>
    Task<EventView> CreateEventAsync(CallerIdentity caller, CreateEventRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The caller a voting link stands for: that participant, for that event only
    /// (<see cref="CallerIdentity.MagicLinkEventId"/>). Null if the token isn't a current link for the event.
    /// </summary>
    Task<CallerIdentity?> ResolveVotingLinkAsync(string eventRef, string token, CancellationToken cancellationToken);

    Task<IReadOnlyList<EventSummary>> ListMyEventsAsync(CallerIdentity caller, EventListFilter filter, CancellationToken cancellationToken);

    /// <param name="viewerTimeZone">IANA zone to add a viewer-local time to each slot, if given.</param>
    Task<EventView> GetEventAsync(CallerIdentity caller, string eventRef, string? viewerTimeZone, CancellationToken cancellationToken);

    /// <summary>Records or overwrites the caller's votes. Safe to retry.</summary>
    /// <param name="note">Note to the organizer: null leaves it unchanged, empty clears it.</param>
    Task<EventView> VoteAsync(CallerIdentity caller, string eventRef, IReadOnlyList<VoteInput> votes, string? note, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a round of slots and emails participants about them. Reopens an expired or finalized event
    /// (rescheduling). <paramref name="respondBy"/> sets a new deadline; a deadline already past is cleared.
    /// </summary>
    Task<EventView> ProposeMoreSlotsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<SlotInput> slots, string? respondBy, uint expectedVersion, CancellationToken cancellationToken);

    Task<EventView> WithdrawSlotAsync(CallerIdentity caller, string eventRef, Guid slotId, uint expectedVersion, CancellationToken cancellationToken);

    Task<FinalizeResult> FinalizeAsync(CallerIdentity caller, string eventRef, Guid slotId, uint expectedVersion, CancellationToken cancellationToken);

    Task<EventView> CancelAsync(CallerIdentity caller, string eventRef, string? reason, CancellationToken cancellationToken);

    /// <summary>Stores the iCalendar UID of the meeting the organizer's agent created after finalizing.</summary>
    Task RecordCalendarEventAsync(CallerIdentity caller, string eventRef, string calendarEventUid, CancellationToken cancellationToken);
}
