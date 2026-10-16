using Scheduler.Core.Model;

namespace Scheduler.Core.Services;

/// <summary>
/// Who is calling. Passed explicitly to every service method rather than read from ambient state,
/// because the MCP endpoint (an HTTP request) and Blazor (a long-lived circuit) learn identity differently.
/// </summary>
/// <param name="UserId">The signed-in account, or null for a magic-link voter.</param>
/// <param name="VerifiedEmails">Normalized addresses the caller has proven they own.</param>
/// <param name="Channel">How the request arrived.</param>
public sealed record CallerIdentity(Guid? UserId, IReadOnlyList<string> VerifiedEmails, Channel Channel);

/// <summary>A candidate start: local wall time (e.g. <c>2026-11-03T10:00</c>) plus an IANA zone.</summary>
public sealed record SlotInput(string Start, string TimeZone);

public sealed record ParticipantInput(string Email, string? DisplayName = null, AttendeeRole Role = AttendeeRole.Required);

public sealed record CreateEventRequest(
    string Title,
    string? Description,
    int DurationMinutes,
    string OrganizerTimeZone,
    IReadOnlyList<ParticipantInput> Participants,
    IReadOnlyList<SlotInput> Slots,
    string? RespondBy);

public sealed record VoteInput(Guid SlotId, VoteValue Value);

public enum EventListFilter
{
    /// <summary>Open events where the caller is a participant who hasn't voted on every slot.</summary>
    NeedsMyVote,
    /// <summary>Events the caller organized, any state.</summary>
    Organizing,
    /// <summary>Everything visible to the caller.</summary>
    All
}

public sealed record EventSummary(
    Guid Id,
    string ShortCode,
    string Title,
    EventState State,
    bool IsOrganizer,
    bool NeedsMyVote,
    string? RespondBy);

/// <summary>Per-slot totals. Shown to every participant; who voted what is shown only to the organizer.</summary>
public sealed record SlotTally(int Preferred, int IfNecessary, int No, int Pending);

public sealed record SlotView(
    Guid SlotId,
    string StartUtc,
    string OrganizerLocal,
    string? ViewerLocal,
    SlotTally Tally,
    VoteValue? MyVote,
    int? Rank);

public sealed record ParticipantView(
    string Email,
    string? DisplayName,
    AttendeeRole Role,
    bool Responded,
    string? Note,
    IReadOnlyDictionary<Guid, VoteValue> Votes);

public sealed record EventView(
    Guid Id,
    string ShortCode,
    string Title,
    string? Description,
    int DurationMinutes,
    EventState State,
    string OrganizerTimeZone,
    string? RespondBy,
    Guid? FinalizedSlotId,
    uint Version,
    IReadOnlyList<SlotView> Slots,
    // Null unless the caller is the organizer: participants see only per-slot totals.
    IReadOnlyList<ParticipantView>? Participants);

/// <summary>
/// What the organizer's agent needs to create the real meeting in the organizer's own calendar.
/// Uses local time plus zone, not UTC, so the calendar event stays anchored to the zone.
/// </summary>
public sealed record FinalizeResult(
    EventView Event,
    string LocalStart,
    string TimeZone,
    int DurationMinutes,
    IReadOnlyList<string> AttendeeEmails);
