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

    /// <summary>
    /// The event's milestones, with the same access check as <see cref="GetEventAsync"/>. Organizer-only fields
    /// are null for participants. See docs/design.md, "Push updates".
    /// </summary>
    Task<EventStatusView> GetEventStatusAsync(CallerIdentity caller, string eventRef, CancellationToken cancellationToken);

    /// <summary>Records or overwrites the caller's votes. Safe to retry.</summary>
    /// <param name="note">Note to the organizer: null leaves it unchanged, empty clears it.</param>
    /// <param name="emailReceipt">
    /// Email the caller a copy of their answer to every current time, to each of their invited addresses. Not to an
    /// address that is unsubscribed or bounced, and not again when the vote changes nothing and the same copy went
    /// out minutes ago, so a retry stays safe. The result's notices say where a copy went, or why it didn't.
    /// </param>
    Task<EventView> VoteAsync(CallerIdentity caller, string eventRef, IReadOnlyList<VoteInput> votes, string? note, bool emailReceipt, CancellationToken cancellationToken);

    /// <summary>
    /// A participant suggests times that aren't on the poll, and the organizer is emailed about them. The organizer
    /// decides: <see cref="ProposeMoreSlotsAsync"/> with the suggestions' IDs adds them, <see cref="DismissSuggestionsAsync"/>
    /// doesn't. Safe to retry: a time the caller already suggested isn't suggested again (a notice says so), and only new
    /// suggestions email the organizer. Votes don't change, and neither does the event's version.
    /// </summary>
    Task<EventView> SuggestSlotsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<SlotInput> slots, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a round of slots and emails participants about them. Reopens an expired or finalized event
    /// (rescheduling). <paramref name="respondBy"/> sets a new deadline; a deadline already past is cleared.
    /// The round is <paramref name="slots"/> plus the times of the suggestions in <paramref name="suggestionIds"/>,
    /// which are marked added, as is any waiting suggestion of a time the round adds.
    /// </summary>
    Task<EventView> ProposeMoreSlotsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<SlotInput>? slots, IReadOnlyList<Guid>? suggestionIds, string? respondBy, uint expectedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Organizer only: decides not to add the suggested times in <paramref name="suggestionIds"/>. No email; the person
    /// who suggested a time sees on their voting page that it wasn't added. Dismissing one again changes nothing.
    /// </summary>
    Task<EventView> DismissSuggestionsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<Guid> suggestionIds, uint expectedVersion, CancellationToken cancellationToken);

    Task<EventView> WithdrawSlotAsync(CallerIdentity caller, string eventRef, Guid slotId, uint expectedVersion, CancellationToken cancellationToken);

    Task<FinalizeResult> FinalizeAsync(CallerIdentity caller, string eventRef, Guid slotId, uint expectedVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Emails the invitation again, with the same voting link, to the participants named in
    /// <paramref name="emails"/>, or to everyone invited if it is null or empty. Organizer only, while the event
    /// is open. Skips (with a notice) anyone who unsubscribed or bounced, or was sent it within the cooldown.
    /// </summary>
    Task<ResendResult> ResendInvitationsAsync(CallerIdentity caller, string eventRef, IReadOnlyList<string>? emails, CancellationToken cancellationToken);

    Task<EventView> CancelAsync(CallerIdentity caller, string eventRef, string? reason, CancellationToken cancellationToken);

    /// <summary>Stores the iCalendar UID of the meeting the organizer's agent created after finalizing.</summary>
    Task RecordCalendarEventAsync(CallerIdentity caller, string eventRef, string calendarEventUid, CancellationToken cancellationToken);
}
