using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;
using Scheduler.Web.Identity;

namespace Scheduler.Web.Mcp;

public sealed record McpSlot(
    [property: Description("Local wall time, ISO 8601 without an offset, e.g. 2026-11-03T10:00.")] string Start,
    [property: Description("IANA time zone, e.g. America/Chicago. Abbreviations like CST are rejected.")] string TimeZone);

public sealed record McpParticipant(
    [property: Description("The address to invite. Votes are tied to it.")] string Email,
    [property: Description("Optional display name.")] string? Name = null,
    [property: Description("True if this person's 'no' should not rule a slot out.")] bool Optional = false);

public sealed record McpVote(
    [property: Description("slotId from get_event.")] string SlotId,
    [property: Description("preferred, if_necessary or no.")] string Vote);

/// <summary>
/// The MCP surface. Each tool resolves the caller, delegates to <see cref="ISchedulingService"/>, and turns
/// a <see cref="SchedulingException"/> into an <see cref="McpException"/> so its message reaches the agent.
/// </summary>
[McpServerToolType]
public sealed class SchedulingTools(ISchedulingService scheduling, IAdminService admin, IHttpContextAccessor http)
{
    [McpServerTool(Name = "get_my_access", Title = "What can I do here?", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows the user's verified addresses on this server and whether they may create scheduling events (canOrganize) or administer the server (isAdmin). Anyone invited to an event can vote without being an organizer.")]
    public Task<string> GetMyAccess(CancellationToken cancellationToken = default) =>
        Run(caller => admin.GetAccessAsync(caller, cancellationToken));

    [McpServerTool(Name = "create_event", Title = "Create scheduling event", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Start scheduling a meeting: proposes candidate times that already work for the organizer and emails each participant a request to vote. Only allowlisted organizers can create events. Returns the event, including its id and shortCode.")]
    public Task<string> CreateEvent(
        [Description("Short meeting title, at most 120 characters.")] string title,
        [Description("Meeting length in minutes, 5 to 480.")] int durationMinutes,
        [Description("The organizer's IANA time zone, e.g. America/Chicago.")] string organizerTimeZone,
        [Description("People to invite, at most 50. Don't include the organizer.")] McpParticipant[] participants,
        [Description("Candidate start times, 1 to 20, each local wall time plus IANA zone.")] McpSlot[] slots,
        [Description("Optional agenda or context, at most 1000 characters.")] string? description = null,
        [Description("Optional voting deadline as local wall time in the organizer's zone, e.g. 2026-10-30T17:00. Must be before the last proposed time.")] string? respondBy = null,
        [Description("Optional: the organizer's name as participants should see it.")] string? organizerName = null,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.CreateEventAsync(caller, new CreateEventRequest(
            title,
            description,
            durationMinutes,
            organizerTimeZone,
            participants.Select(p => new ParticipantInput(p.Email, p.Name, p.Optional ? AttendeeRole.Optional : AttendeeRole.Required)).ToList(),
            slots.Select(s => new SlotInput(s.Start, s.TimeZone)).ToList(),
            respondBy,
            organizerName), cancellationToken));

    [McpServerTool(Name = "list_my_events", Title = "List my scheduling events", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists scheduling events visible to the user. By default returns only events waiting for the user's vote.")]
    public Task<string> ListMyEvents(
        [Description("needs_my_vote (default), organizing, or all.")] string filter = "needs_my_vote",
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.ListMyEventsAsync(caller, ParseFilter(filter), cancellationToken));

    [McpServerTool(Name = "get_event", Title = "Get scheduling event", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gets one event: its slots with per-slot vote totals and the user's own votes. The organizer also sees each participant's votes and a ranked recommendation.")]
    public Task<string> GetEvent(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("Optional IANA zone to add a viewer-local time to every slot.")] string? timeZone = null,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.GetEventAsync(caller, eventId, timeZone, cancellationToken));

    [McpServerTool(Name = "vote", Title = "Vote on proposed times", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Records the user's answer for one or more slots. Voting again replaces earlier answers, so retrying is safe. Confirm the answers with the user first.")]
    public Task<string> Vote(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("One entry per slot answered.")] McpVote[] votes,
        [Description("Optional short note to the organizer, at most 280 characters.")] string? note = null,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.VoteAsync(
            caller,
            eventId,
            votes.Select(v => new VoteInput(ToolJson.ParseId(v.SlotId, "slot"), ToolJson.ParseVote(v.Vote))).ToList(),
            note,
            cancellationToken));

    [McpServerTool(Name = "propose_more_slots", Title = "Propose more times", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Organizer only: adds another round of candidate times, e.g. when none of the first round works. Also reopens an expired event, or a finalized one that needs rescheduling. Emails participants about the new times.")]
    public Task<string> ProposeMoreSlots(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("New candidate start times, each local wall time plus IANA zone.")] McpSlot[] slots,
        [Description("The event's version from the get_event result you are acting on.")] uint version,
        [Description("Optional new voting deadline as local wall time in the organizer's zone. A deadline that has already passed is cleared.")] string? respondBy = null,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.ProposeMoreSlotsAsync(
            caller, eventId, slots.Select(s => new SlotInput(s.Start, s.TimeZone)).ToList(), respondBy, version, cancellationToken));

    [McpServerTool(Name = "withdraw_slot", Title = "Withdraw a proposed time", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Organizer only: removes a candidate time that no longer works for the organizer. Its votes are discarded.")]
    public Task<string> WithdrawSlot(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("slotId from get_event.")] string slotId,
        [Description("The event's version from the get_event result you are acting on.")] uint version,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.WithdrawSlotAsync(caller, eventId, ToolJson.ParseId(slotId, "slot"), version, cancellationToken));

    [McpServerTool(Name = "finalize_event", Title = "Pick the meeting time", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Organizer only: picks the final slot and emails everyone. Re-check the organizer's calendar first. Then create the meeting in the organizer's own calendar using the returned localStart, timeZone and attendeeEmails, and call record_calendar_event.")]
    public Task<string> FinalizeEvent(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("slotId of the chosen time.")] string slotId,
        [Description("The event's version from the get_event result you are acting on.")] uint version,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.FinalizeAsync(caller, eventId, ToolJson.ParseId(slotId, "slot"), version, cancellationToken));

    [McpServerTool(Name = "cancel_event", Title = "Cancel scheduling event", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Organizer only: calls the meeting off and emails everyone.")]
    public Task<string> CancelEvent(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("Optional short reason included in the email.")] string? reason = null,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.CancelAsync(caller, eventId, reason, cancellationToken));

    [McpServerTool(Name = "record_calendar_event", Title = "Record the calendar event", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Organizer only: after finalize_event, stores the iCalendar UID of the meeting created in the organizer's calendar so a later reschedule or cancel can refer to it.")]
    public async Task<string> RecordCalendarEvent(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("The calendar event's iCalendar UID.")] string calendarEventUid,
        CancellationToken cancellationToken = default) =>
        await Run(async caller =>
        {
            await scheduling.RecordCalendarEventAsync(caller, eventId, calendarEventUid, cancellationToken);
            return new { recorded = true };
        });

    private async Task<string> Run<T>(Func<CallerIdentity, Task<T>> action)
    {
        var caller = CallerResolver.FromHttpContext(http.HttpContext, Channel.Mcp)
            ?? throw new McpException("You are not signed in to the scheduling server.");
        try
        {
            return ToolJson.Serialize(await action(caller));
        }
        catch (SchedulingException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static EventListFilter ParseFilter(string? filter) => filter?.Trim().ToLowerInvariant() switch
    {
        null or "" or "needs_my_vote" => EventListFilter.NeedsMyVote,
        "organizing" => EventListFilter.Organizing,
        "all" => EventListFilter.All,
        _ => throw new McpException($"'{filter}' isn't a filter. Use needs_my_vote, organizing or all.")
    };
}
