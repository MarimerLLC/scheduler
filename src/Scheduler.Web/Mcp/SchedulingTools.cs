using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
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
    [property: Description("preferred (a great time), yes (works without much trouble), if_necessary (possible but painful: moving other commitments, early or late hours) or no (can't make it).")] string Vote);

/// <summary>
/// The MCP surface. Each tool resolves the caller, delegates to <see cref="ISchedulingService"/>, and turns
/// a <see cref="SchedulingException"/> into an <see cref="McpException"/> so its message reaches the agent.
/// </summary>
[McpServerToolType]
public sealed class SchedulingTools(ISchedulingService scheduling, IAdminService admin, IHttpContextAccessor http)
{
    [McpServerTool(Name = "get_my_access", Title = "What can I do here?", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows the user's verified addresses on this server and whether they may create scheduling events (canOrganize) or administer the server (isAdmin). Anyone invited to an event can vote without being an organizer. organizerRequest is the user's request to organize that is waiting, or was declined too recently to ask again.")]
    public Task<string> GetMyAccess(CancellationToken cancellationToken = default) =>
        Run(caller => admin.GetAccessAsync(caller, cancellationToken));

    [McpServerTool(Name = "request_to_organize", Title = "Ask to organize", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Asks this server's admins to let the user create scheduling events, for someone whose get_my_access shows canOrganize false. An admin approves or declines it; the user gets an email either way. Asking again while a request waits returns that request. Confirm with the user first, and send only a note the user wrote or approved.")]
    public Task<string> RequestToOrganize(
        [Description("Optional short note to the admins saying who the user is and why, at most 280 characters, one line, no links.")] string? note = null,
        [Description("Optional: which of the user's verified addresses should organize. Defaults to the one they signed in with.")] string? email = null,
        CancellationToken cancellationToken = default) =>
        Run(caller => admin.RequestToOrganizeAsync(caller, email, note, cancellationToken));

    [McpServerTool(Name = "create_event", Title = "Create scheduling event", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Start scheduling a meeting: proposes candidate times that already work for the organizer and emails each participant a request to vote. Only allowlisted organizers can create events. Returns the event, including its id and shortCode. If your client supports MCP input requests (MRTR), the server asks the user for a missing title, time zone, invite list or candidate times, and, when the title is only a few words and there is no description, offers once to add one (the user can skip it).")]
    public Task<string> CreateEvent(
        RequestContext<CallToolRequestParams> context,
        [Description("Short meeting title, at most 120 characters.")] string title,
        [Description("Meeting length in minutes, 5 to 480.")] int durationMinutes,
        [Description("The organizer's IANA time zone, e.g. America/Chicago.")] string organizerTimeZone,
        [Description("People to invite, at most 50. Don't include the organizer.")] McpParticipant[] participants,
        [Description("Candidate start times, 1 to 20, each local wall time plus IANA zone.")] McpSlot[] slots,
        [Description("Optional agenda or context, at most 1000 characters. Worth adding when the title alone doesn't say what the meeting is for.")] string? description = null,
        [Description("Optional voting deadline as local wall time in the organizer's zone, e.g. 2026-10-30T17:00. Must be before the last proposed time.")] string? respondBy = null,
        [Description("Optional: the organizer's name as participants should see it.")] string? organizerName = null,
        CancellationToken cancellationToken = default) =>
        Run(async caller =>
        {
            var input = await CompleteAsync(
                caller, context, new NewEvent(title, description, durationMinutes, organizerTimeZone, participants, slots), cancellationToken);
            return await scheduling.CreateEventAsync(caller, new CreateEventRequest(
                input.Title ?? "",
                input.Description,
                input.DurationMinutes,
                input.TimeZone ?? "",
                (input.Participants ?? []).Select(p => new ParticipantInput(p.Email, p.Name, p.Optional ? AttendeeRole.Optional : AttendeeRole.Required)).ToList(),
                (input.Slots ?? []).Select(s => new SlotInput(s.Start, s.TimeZone)).ToList(),
                respondBy,
                organizerName), cancellationToken);
        });

    [McpServerTool(Name = "list_my_events", Title = "List my scheduling events", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists scheduling events visible to the user. By default returns only events waiting for the user's vote. Past events (cancelled, expired, or finalized for a meeting that has ended) are left out unless includePast is true; use that only when the user asks about old events.")]
    public Task<string> ListMyEvents(
        [Description("needs_my_vote (default), organizing, or all.")] string filter = "needs_my_vote",
        [Description("True to also list past events: cancelled, expired, or finalized for a meeting that has ended. Default false.")] bool includePast = false,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.ListMyEventsAsync(caller, ParseFilter(filter), includePast, cancellationToken));

    [McpServerTool(Name = "get_event", Title = "Get scheduling event", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gets one event: its slots with per-slot vote totals and the user's own votes. The organizer also sees each participant's votes and a ranked recommendation (rank 1 is best). Only no rules a slot out. Recommend a slot with an if_necessary vote only when it is the only way to make any time work, and then tell the organizer who would be stretched by it. suggestions are times participants suggested adding (status pending, added or dismissed): the organizer sees everyone's, a participant only their own.")]
    public Task<string> GetEvent(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("Optional IANA zone to add a viewer-local time to every slot.")] string? timeZone = null,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.GetEventAsync(caller, eventId, timeZone, cancellationToken));

    [McpServerTool(Name = "vote", Title = "Vote on proposed times", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Records the user's answer for one or more slots. Each slot is answered on its own: mark every slot the user can make, however many there are, as preferred if it's a great time or yes if it's fine. Save if_necessary for times that would be painful, and no for times they can't make. Answer every slot: one left out stays unanswered, and the organizer keeps waiting on it. Voting again replaces earlier answers, so retrying is safe. Confirm the answers with the user first.")]
    public Task<string> Vote(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("One entry per slot answered.")] McpVote[] votes,
        [Description("Optional short note to the organizer, at most 280 characters.")] string? note = null,
        [Description("True to email the user a copy of their answers. Only when the user asks for one. A retry that changes nothing doesn't send a second copy; the result's notices say where the copy went.")] bool emailReceipt = false,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.VoteAsync(
            caller,
            eventId,
            votes.Select(v => new VoteInput(ToolJson.ParseId(v.SlotId, "slot"), ToolJson.ParseVote(v.Vote))).ToList(),
            note,
            emailReceipt,
            unansweredAsNo: null,
            cancellationToken));

    [McpServerTool(Name = "suggest_slots", Title = "Suggest other times", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("For someone invited, while the event is open: suggests start times that aren't on the poll, e.g. when none of the proposed times work for the user. Emails the organizer, who decides whether to add them to the poll; nothing changes for anyone else until then. Check each time against the user's calendar and confirm with the user first. A time already proposed, or already suggested by the user, isn't suggested again (notices say so), so retrying is safe. get_event shows the user's suggestions and whether each was added.")]
    public Task<string> SuggestSlots(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("Start times to suggest, 1 to 5, each local wall time plus IANA zone (the user's own zone is fine). The meeting length stays the organizer's.")] McpSlot[] slots,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.SuggestSlotsAsync(
            caller, eventId, slots.Select(s => new SlotInput(s.Start, s.TimeZone)).ToList(), cancellationToken));

    [McpServerTool(Name = "propose_more_slots", Title = "Propose more times", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Organizer only: adds another round of candidate times, e.g. when none of the first round works. Also reopens an expired event, or a finalized one that needs rescheduling. Emails participants about the new times. To add times participants suggested (the suggestions in get_event), pass their suggestionIds rather than repeating the times; check each against the organizer's calendar first, because every proposed time should work for the organizer.")]
    public Task<string> ProposeMoreSlots(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("The event's version from the get_event result you are acting on.")] uint version,
        [Description("New candidate start times, each local wall time plus IANA zone. Can be omitted when suggestionIds are given.")] McpSlot[]? slots = null,
        [Description("Optional: suggestionId values from get_event, for suggested times to add to the poll in this round.")] string[]? suggestionIds = null,
        [Description("Optional new voting deadline as local wall time in the organizer's zone. A deadline that has already passed is cleared.")] string? respondBy = null,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.ProposeMoreSlotsAsync(
            caller,
            eventId,
            (slots ?? []).Select(s => new SlotInput(s.Start, s.TimeZone)).ToList(),
            (suggestionIds ?? []).Select(id => ToolJson.ParseId(id, "suggestion")).ToList(),
            respondBy,
            version,
            cancellationToken));

    [McpServerTool(Name = "dismiss_suggestions", Title = "Dismiss suggested times", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Organizer only: decides not to add times participants suggested (the suggestions in get_event). No one is emailed; the person who suggested a time sees that it wasn't added, and can't suggest it again. Confirm with the organizer first.")]
    public Task<string> DismissSuggestions(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("suggestionId values from get_event.")] string[] suggestionIds,
        [Description("The event's version from the get_event result you are acting on.")] uint version,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.DismissSuggestionsAsync(
            caller, eventId, suggestionIds.Select(id => ToolJson.ParseId(id, "suggestion")).ToList(), version, cancellationToken));

    [McpServerTool(Name = "withdraw_slot", Title = "Withdraw a proposed time", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Organizer only: removes a candidate time that no longer works for the organizer. Its votes are discarded.")]
    public Task<string> WithdrawSlot(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("slotId from get_event.")] string slotId,
        [Description("The event's version from the get_event result you are acting on.")] uint version,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.WithdrawSlotAsync(caller, eventId, ToolJson.ParseId(slotId, "slot"), version, cancellationToken));

    [McpServerTool(Name = "finalize_event", Title = "Pick the meeting time", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Organizer only: picks the final slot and emails everyone. Re-check the organizer's calendar first. If the chosen slot has if_necessary votes, make sure the organizer knows who would be stretched by it. Then create the meeting in the organizer's own calendar using the returned localStart, timeZone and attendeeEmails, and call record_calendar_event.")]
    public Task<string> FinalizeEvent(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("slotId of the chosen time.")] string slotId,
        [Description("The event's version from the get_event result you are acting on.")] uint version,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.FinalizeAsync(caller, eventId, ToolJson.ParseId(slotId, "slot"), version, cancellationToken));

    [McpServerTool(Name = "resend_invitations", Title = "Resend invitations", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Organizer only, while the event is open: emails the invitation again, with the same personal voting link and the current times, to the listed participants, or to everyone invited if emails is omitted. Use it when someone says they never got the invitation or lost it. Anyone sent it too recently (within the last hour, by default), unsubscribed, or whose email bounced is skipped and explained in notices. Returns the event and resent, the addresses it was sent to. Confirm with the organizer first.")]
    public Task<string> ResendInvitations(
        [Description("The event's id or shortCode.")] string eventId,
        [Description("Optional: addresses of participants to resend to, as listed by get_event. Omit to resend to everyone invited.")] string[]? emails = null,
        CancellationToken cancellationToken = default) =>
        Run(caller => scheduling.ResendInvitationsAsync(caller, eventId, emails, cancellationToken));

    [McpServerTool(Name = "cancel_event", Title = "Cancel scheduling event", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Organizer only: calls the meeting off and emails everyone invited, with the organizer's reason. While voting is still going, the email says the invitation was withdrawn; after finalize_event, it says the meeting is cancelled. Ask the organizer for the reason and confirm before calling. If the reason is left empty and your client supports MCP input requests (MRTR), the server asks the user for it; otherwise an empty reason is refused.")]
    public Task<string> CancelEvent(
        RequestContext<CallToolRequestParams> context,
        [Description("The event's id or shortCode.")] string eventId,
        [Description("Required: the organizer's short reason for cancelling, quoted in the email to everyone invited.")] string reason,
        CancellationToken cancellationToken = default) =>
        Run(async caller =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                if (McpQuestion.AnswerTo(context) is { } answer)
                {
                    reason = McpQuestion.TextOf(answer, "reason")
                        ?? throw new McpException("No reason was given, so the event was not cancelled.");
                }
                else if (McpQuestion.CanAsk(context))
                {
                    // Ask only when the answer can be used; anything else gets its usual error from CancelAsync.
                    var ev = await scheduling.GetEventAsync(caller, eventId, viewerTimeZone: null, cancellationToken);
                    if (ev.IsOrganizer && SchedulingEvent.CanTransition(ev.State, EventState.Cancelled))
                    {
                        throw new McpQuestion()
                            .Text("reason", "Reason for cancelling",
                                "A short reason, quoted in the email to everyone invited.", FieldLimits.NoteMax, required: true)
                            .Ask($"Why is \"{ev.Title}\" ({ev.ShortCode}) being cancelled? Everyone invited gets an email quoting the reason.");
                    }
                }
            }
            return await scheduling.CancelAsync(caller, eventId, reason, cancellationToken);
        });

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

    /// <summary>A title with fewer words than this, and no description, gets one offer to add a description.</summary>
    internal const int ShortTitleWords = 4;

    private sealed record NewEvent(
        string? Title, string? Description, int DurationMinutes, string? TimeZone, McpParticipant[]? Participants, McpSlot[]? Slots);

    /// <summary>
    /// Fills in what create_event left out by asking through MRTR (<see cref="McpQuestion"/>): the title, time zone,
    /// invite list and candidate times, which are required, and a description when the title is short, which can be
    /// skipped. Returns the input unchanged when nothing is missing or this client can't be asked, so the service
    /// reports anything required that is still missing.
    /// </summary>
    private async Task<NewEvent> CompleteAsync(CallerIdentity caller, RequestContext<CallToolRequestParams> context, NewEvent input, CancellationToken cancellationToken)
    {
        var needTitle = string.IsNullOrWhiteSpace(input.Title);
        var needZone = string.IsNullOrWhiteSpace(input.TimeZone);
        var needParticipants = input.Participants is not { Length: > 0 };
        var needSlots = input.Slots is not { Length: > 0 };
        var offerDescription = string.IsNullOrWhiteSpace(input.Description) && (needTitle || WordCount(input.Title!) < ShortTitleWords);
        var required = new List<string>();
        if (needTitle) required.Add("a title");
        if (needZone) required.Add("a time zone");
        if (needParticipants) required.Add("who to invite");
        if (needSlots) required.Add("candidate times");
        if (required.Count == 0 && !offerDescription)
            return input;

        if (McpQuestion.AnswerTo(context) is not { } answer)
        {
            // Ask only when the answer can be used; anything else gets its usual error from CreateEventAsync.
            if (!McpQuestion.CanAsk(context)
                || input.DurationMinutes is < FieldLimits.MinDurationMinutes or > FieldLimits.MaxDurationMinutes
                || !(await admin.GetAccessAsync(caller, cancellationToken)).CanOrganize)
                return input;

            var zoneText = needZone ? "the organizer's time zone" : input.TimeZone!.Trim();
            var question = new McpQuestion();
            if (needTitle)
                question.Text("title", "Title", $"A short meeting title, at most {FieldLimits.TitleMax} characters.", FieldLimits.TitleMax, required: true);
            if (needZone)
                question.Text("timeZone", "Time zone", "The organizer's IANA time zone, e.g. America/Chicago.", FieldLimits.TimeZoneIdMax, required: true);
            if (needParticipants)
                question.Text("participants", "Who to invite",
                    $"Email addresses, separated by commas, at most {FieldLimits.MaxParticipantsPerEvent}. Don't include the organizer.", 4000, required: true);
            if (needSlots)
                question.Text("slots", "Candidate times",
                    $"Start times that work for the organizer, as local times in {zoneText}, separated by commas, e.g. 2026-11-05T10:00, 2026-11-06T14:30. At most {FieldLimits.MaxSlotsPerEvent}.",
                    1000, required: true);
            if (offerDescription)
                question.Text("description", "Description (optional)",
                    $"What the meeting is for, for the people invited, at most {FieldLimits.DescriptionMax} characters. Leave it blank to skip.",
                    FieldLimits.DescriptionMax, required: false);

            var what = required.Count == 0
                ? "doesn't say much about what the meeting is for. Add a short description for the people invited, or leave it blank to skip."
                : $"still needs {JoinAnd(required)} before the invitations go out.";
            throw question.Ask(needTitle ? $"This meeting {what}" : $"\"{input.Title!.Trim()}\" {what}");
        }

        // Skipping the optional description is fine; leaving out anything required stops here.
        if (!answer.IsAccepted && required.Count > 0)
            throw new McpException($"The organizer didn't give {JoinAnd(required)}, so no event was created.");

        var timeZone = needZone ? McpQuestion.TextOf(answer, "timeZone") : input.TimeZone;
        var completed = input with
        {
            Title = needTitle ? McpQuestion.TextOf(answer, "title") : input.Title,
            TimeZone = timeZone,
            Participants = needParticipants
                ? Split(McpQuestion.TextOf(answer, "participants"), ListSeparators).Select(email => new McpParticipant(email)).ToArray()
                : input.Participants,
            Slots = needSlots && timeZone is not null
                ? Split(McpQuestion.TextOf(answer, "slots"), SlotSeparators).Select(start => new McpSlot(start, timeZone)).ToArray()
                : input.Slots,
            Description = offerDescription ? McpQuestion.TextOf(answer, "description") : input.Description,
        };

        var stillMissing = new List<string>();
        if (string.IsNullOrWhiteSpace(completed.Title)) stillMissing.Add("a title");
        if (string.IsNullOrWhiteSpace(completed.TimeZone)) stillMissing.Add("a time zone");
        if (completed.Participants is not { Length: > 0 }) stillMissing.Add("who to invite");
        if (completed.Slots is not { Length: > 0 }) stillMissing.Add("candidate times");
        if (stillMissing.Count > 0)
            throw new McpException($"The organizer didn't give {JoinAnd(stillMissing)}, so no event was created.");
        return completed;
    }

    private static readonly char[] ListSeparators = [',', ';', ' ', '\t', '\r', '\n'];
    private static readonly char[] SlotSeparators = [',', ';', '\r', '\n'];

    private static string[] Split(string? text, char[] separators) =>
        text?.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string JoinAnd(List<string> items) => items.Count switch
    {
        1 => items[0],
        _ => string.Join(", ", items[..^1]) + " and " + items[^1],
    };

    private Task<string> Run<T>(Func<CallerIdentity, Task<T>> action) => McpCaller.RunAsync(http.HttpContext, action);

    private static EventListFilter ParseFilter(string? filter) => filter?.Trim().ToLowerInvariant() switch
    {
        null or "" or "needs_my_vote" => EventListFilter.NeedsMyVote,
        "organizing" => EventListFilter.Organizing,
        "all" => EventListFilter.All,
        _ => throw new McpException($"'{filter}' isn't a filter. Use needs_my_vote, organizing or all.")
    };
}
