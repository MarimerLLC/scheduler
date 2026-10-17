using Scheduler.Core.Model;

namespace Scheduler.Core.Services;

/// <summary>
/// Who is calling. Passed explicitly to every service method rather than read from ambient state,
/// because the MCP endpoint (an HTTP request) and Blazor (a long-lived circuit) learn identity differently.
/// </summary>
/// <param name="UserId">The signed-in account, if the identity layer resolved one.</param>
/// <param name="VerifiedEmails">Normalized addresses the caller has proven they own.</param>
/// <param name="Channel">How the request arrived.</param>
/// <param name="MagicLinkEventId">
/// Set when the caller arrived by a voting link instead of signing in. Such a caller is a participant of
/// that one event and nothing else: never an organizer, never resolved to an account.
/// </param>
public sealed record CallerIdentity(
    Guid? UserId,
    IReadOnlyList<string> VerifiedEmails,
    Channel Channel,
    Guid? MagicLinkEventId = null);

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
    string? RespondBy,
    string? OrganizerName = null);

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

/// <summary>
/// One event in a list. <see cref="Responded"/> and <see cref="Invited"/> are for the organizer only: how many
/// participants have answered every proposed time, out of how many were invited.
/// </summary>
public sealed record EventSummary(
    Guid Id,
    string ShortCode,
    string Title,
    EventState State,
    bool IsOrganizer,
    bool NeedsMyVote,
    string? RespondBy,
    int? Responded = null,
    int? Invited = null);

/// <summary>Per-slot totals. Shown to every participant; who voted what is shown only to the organizer.</summary>
public sealed record SlotTally(int Preferred, int IfNecessary, int No, int Pending);

public sealed record SlotView(
    Guid SlotId,
    string StartUtc,
    string OrganizerLocal,
    string? ViewerLocal,
    SlotTally Tally,
    VoteValue? MyVote,
    // Organizer only: position in the ranked recommendation (1 = best) and whether no required person said no.
    int? Rank,
    bool? Viable);

/// <summary>
/// A participant as the caller may see them. Participants see each other's names and addresses (as with a
/// normal invite) but not each other's votes, response status or notes; those fields are null for them.
/// </summary>
public sealed record ParticipantView(
    string Email,
    string? DisplayName,
    AttendeeRole Role,
    bool? Responded,
    string? Note,
    IReadOnlyDictionary<Guid, VoteValue>? Votes);

public sealed record EventView(
    Guid Id,
    string ShortCode,
    string Title,
    string? Description,
    int DurationMinutes,
    EventState State,
    string OrganizerEmail,
    string? OrganizerName,
    string OrganizerTimeZone,
    string? RespondBy,
    Guid? FinalizedSlotId,
    uint Version,
    bool IsOrganizer,
    IReadOnlyList<SlotView> Slots,
    IReadOnlyList<ParticipantView> Participants,
    // Things the caller should know about how the request was applied, e.g. a dropped duplicate address.
    IReadOnlyList<string>? Notices = null);

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
