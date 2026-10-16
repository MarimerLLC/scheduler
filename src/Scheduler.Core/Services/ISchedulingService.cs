namespace Scheduler.Core.Services;

/// <summary>
/// Every scheduling rule lives behind this interface. The MCP tools and the Blazor pages are thin
/// wrappers over it, so agents and people always get the same behavior. <c>eventRef</c> parameters accept
/// either the event's ID or its short code.
/// </summary>
public interface ISchedulingService
{
    /// <summary>Creates and opens an event, and queues invitation emails. Caller must be an allowlisted organizer.</summary>
    Task<EventView> CreateEventAsync(CallerIdentity caller, CreateEventRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<EventSummary>> ListMyEventsAsync(CallerIdentity caller, EventListFilter filter, CancellationToken cancellationToken);

    /// <param name="viewerTimeZone">IANA zone to add a viewer-local time to each slot, if given.</param>
    Task<EventView> GetEventAsync(CallerIdentity caller, string eventRef, string? viewerTimeZone, CancellationToken cancellationToken);

    /// <summary>Records or overwrites the caller's votes. Safe to retry.</summary>
    Task<EventView> VoteAsync(CallerIdentity caller, string eventRef, IReadOnlyList<VoteInput> votes, string? note, CancellationToken cancellationToken);

    /// <summary>Adds a round of slots and notifies only participants who haven't voted on them.</summary>
    Task<EventView> ProposeMoreSlotsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<SlotInput> slots, uint expectedVersion, CancellationToken cancellationToken);

    Task<EventView> WithdrawSlotAsync(CallerIdentity caller, string eventRef, Guid slotId, uint expectedVersion, CancellationToken cancellationToken);

    Task<FinalizeResult> FinalizeAsync(CallerIdentity caller, string eventRef, Guid slotId, uint expectedVersion, CancellationToken cancellationToken);

    Task<EventView> CancelAsync(CallerIdentity caller, string eventRef, string? reason, CancellationToken cancellationToken);

    /// <summary>Stores the iCalendar UID of the meeting the organizer's agent created after finalizing.</summary>
    Task RecordCalendarEventAsync(CallerIdentity caller, string eventRef, string calendarEventUid, CancellationToken cancellationToken);
}
