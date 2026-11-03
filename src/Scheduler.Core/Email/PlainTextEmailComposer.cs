using System.Text;
using Scheduler.Core.Model;
using Scheduler.Core.Security;
using Scheduler.Core.Time;

namespace Scheduler.Core.Email;

/// <summary>
/// The subjects and plain-text bodies of the notifications. Plain text is the primary body because agents read
/// it; the web app's <c>HtmlEmailComposer</c> adds an HTML alternative with the same content. Without
/// <paramref name="unsubscribe"/> (tests) emails carry no unsubscribe link.
/// </summary>
public sealed class PlainTextEmailComposer(string mcpUrl, IUnsubscribeLinks? unsubscribe = null) : IEmailComposer
{
    public string McpUrl => mcpUrl;

    public ComposedEmail Invitation(SchedulingEvent ev, Participant recipient, string votingUrl)
    {
        var body = new StringBuilder();
        body.AppendLine($"{OrganizerName(ev)} is looking for a time to meet and has proposed the times below.");
        AppendEvent(body, ev);
        AppendSlots(body, ev, ev.ActiveSlots);
        AppendHowToVote(body, ev, votingUrl);
        return EventEmail(recipient.Email, Subject(ev, $"{OrganizerName(ev)} asks: {ev.Title}{Deadline(ev)}"), body);
    }

    public ComposedEmail NewSlots(SchedulingEvent ev, Participant recipient, string votingUrl, IReadOnlyList<ProposedSlot> newSlots, bool rescheduling)
    {
        var body = new StringBuilder();
        body.AppendLine(NewSlotsIntro(ev, rescheduling));
        AppendEvent(body, ev);
        AppendSlots(body, ev, newSlots);
        AppendHowToVote(body, ev, votingUrl);
        return EventEmail(recipient.Email, Subject(ev, $"New times for: {ev.Title}{Deadline(ev)}"), body);
    }

    public ComposedEmail Finalized(SchedulingEvent ev, Participant recipient, ProposedSlot slot)
    {
        var body = new StringBuilder();
        body.AppendLine($"{OrganizerName(ev)} has picked a time:");
        body.AppendLine();
        body.AppendLine($"  {SlotTimeFormatter.Display(slot.Start, ev.OrganizerTimeZone)}, {(int)ev.Duration.TotalMinutes} minutes");
        AppendEvent(body, ev);
        body.AppendLine(FinalizedClosing);
        return EventEmail(recipient.Email, Subject(ev, $"Set: {ev.Title}"), body);
    }

    public ComposedEmail Cancelled(SchedulingEvent ev, Participant recipient, string reason, bool meetingWasSet)
    {
        var body = new StringBuilder();
        body.AppendLine(CancelledIntro(ev, meetingWasSet));
        AppendEvent(body, ev);
        body.AppendLine("Reason given by the organizer:");
        body.AppendLine($"  \"{reason}\"");
        return EventEmail(recipient.Email, Subject(ev, CancelledSubject(ev, meetingWasSet)), body);
    }

    public ComposedEmail CantMakeAnyTime(SchedulingEvent ev, Participant who, string dashboardUrl)
    {
        var body = new StringBuilder();
        body.AppendLine(CantMakeAnyTimeIntro(ev, who));
        body.AppendLine();
        body.AppendLine($"See everyone's answers: {dashboardUrl}");
        body.AppendLine(CantMakeAnyTimeClosing(ev, who));
        return EventEmail(ev.OrganizerEmail, Subject(ev, $"{ParticipantName(who)} can't make any time: {ev.Title}"), body);
    }

    public ComposedEmail AllResponded(SchedulingEvent ev, string dashboardUrl)
    {
        var body = new StringBuilder();
        body.AppendLine($"Everyone required has answered every proposed time for \"{ev.Title}\".");
        body.AppendLine();
        body.AppendLine($"See the results and pick a time: {dashboardUrl}");
        body.AppendLine($"Or ask your assistant to finalize scheduling event {ev.ShortCode}.");
        return EventEmail(ev.OrganizerEmail, Subject(ev, $"Everyone has responded: {ev.Title}"), body);
    }

    public ComposedEmail Reminder(SchedulingEvent ev, Participant recipient, string votingUrl)
    {
        var body = new StringBuilder();
        body.AppendLine(ReminderIntro(ev));
        AppendEvent(body, ev);
        var unanswered = UnansweredSlots(ev, recipient);
        AppendSlots(body, ev, unanswered, "Times you haven't answered:");
        AppendHowToVote(body, ev, votingUrl);
        return EventEmail(recipient.Email, Subject(ev, $"Reminder: {ev.Title}{Deadline(ev)}"), body);
    }

    public ComposedEmail DeadlinePassed(SchedulingEvent ev, string dashboardUrl)
    {
        var body = new StringBuilder();
        body.AppendLine(DeadlinePassedIntro(ev));
        body.AppendLine();
        body.AppendLine($"See the answers so far: {dashboardUrl}");
        body.AppendLine(DeadlinePassedClosing(ev));
        return EventEmail(ev.OrganizerEmail, Subject(ev, $"Voting closed: {ev.Title}"), body);
    }

    public ComposedEmail DeliveryProblem(SchedulingEvent ev, string address, string dashboardUrl)
    {
        var body = new StringBuilder();
        body.AppendLine(DeliveryProblemIntro(ev, address));
        body.AppendLine();
        body.AppendLine(DeliveryProblemClosing);
        body.AppendLine($"The event: {dashboardUrl}");
        return EventEmail(ev.OrganizerEmail, Subject(ev, $"Couldn't reach {address}: {ev.Title}"), body);
    }

    public ComposedEmail AddressVerification(string email, string link, string requestedBy)
    {
        var body = new StringBuilder();
        body.AppendLine($"{requestedBy} asked to add {email} to their account on the scheduling server.");
        body.AppendLine("Confirming lets that account see and answer meeting invitations sent to this address.");
        body.AppendLine();
        body.AppendLine($"If that's you, confirm here while signed in as {requestedBy}:");
        body.AppendLine($"  {link}");
        body.AppendLine();
        body.AppendLine(LinkClosing("you didn't ask for this, ignore this email; nothing changes"));
        return new ComposedEmail(AddressVerificationSubject(email), body.ToString());
    }

    public ComposedEmail SignInLink(string email, string link)
    {
        var body = new StringBuilder();
        body.AppendLine($"Someone is signing in to the scheduling server as {email}. If it's you, finish signing in here:");
        body.AppendLine($"  {link}");
        body.AppendLine();
        body.AppendLine(LinkClosing("you didn't try to sign in, ignore this email"));
        return new ComposedEmail(SignInLinkSubject, body.ToString());
    }

    public ComposedEmail OrganizerRequestWaiting(string requester, string reviewUrl)
    {
        var body = new StringBuilder();
        body.AppendLine(OrganizerRequestWaitingIntro(requester));
        body.AppendLine();
        body.AppendLine("Approve or decline it on the Organizers page:");
        body.AppendLine($"  {reviewUrl}");
        body.AppendLine();
        body.AppendLine(OrganizerRequestWaitingClosing);
        return new ComposedEmail(OrganizerRequestWaitingSubject, body.ToString());
    }

    public ComposedEmail OrganizerRequestApproved(string email, string newEventUrl)
    {
        var body = new StringBuilder();
        body.AppendLine(OrganizerRequestApprovedIntro(email));
        body.AppendLine();
        body.AppendLine("Schedule a meeting here:");
        body.AppendLine($"  {newEventUrl}");
        body.AppendLine();
        body.AppendLine($"Or ask your assistant, with this server's MCP endpoint as a connector: {mcpUrl}");
        return new ComposedEmail(OrganizerRequestApprovedSubject, body.ToString());
    }

    public ComposedEmail OrganizerRequestDeclined(string email, string canAskAgainAt) =>
        new(OrganizerRequestDeclinedSubject, OrganizerRequestDeclinedText(email, canAskAgainAt) + Environment.NewLine);

    Task<ComposedEmail> IEmailComposer.InvitationAsync(SchedulingEvent ev, Participant recipient, string votingUrl) =>
        Task.FromResult(Invitation(ev, recipient, votingUrl));

    Task<ComposedEmail> IEmailComposer.NewSlotsAsync(SchedulingEvent ev, Participant recipient, string votingUrl, IReadOnlyList<ProposedSlot> newSlots, bool rescheduling) =>
        Task.FromResult(NewSlots(ev, recipient, votingUrl, newSlots, rescheduling));

    Task<ComposedEmail> IEmailComposer.FinalizedAsync(SchedulingEvent ev, Participant recipient, ProposedSlot slot) =>
        Task.FromResult(Finalized(ev, recipient, slot));

    Task<ComposedEmail> IEmailComposer.CancelledAsync(SchedulingEvent ev, Participant recipient, string reason, bool meetingWasSet) =>
        Task.FromResult(Cancelled(ev, recipient, reason, meetingWasSet));

    Task<ComposedEmail> IEmailComposer.CantMakeAnyTimeAsync(SchedulingEvent ev, Participant who, string dashboardUrl) =>
        Task.FromResult(CantMakeAnyTime(ev, who, dashboardUrl));

    Task<ComposedEmail> IEmailComposer.AllRespondedAsync(SchedulingEvent ev, string dashboardUrl) =>
        Task.FromResult(AllResponded(ev, dashboardUrl));

    Task<ComposedEmail> IEmailComposer.ReminderAsync(SchedulingEvent ev, Participant recipient, string votingUrl) =>
        Task.FromResult(Reminder(ev, recipient, votingUrl));

    Task<ComposedEmail> IEmailComposer.DeadlinePassedAsync(SchedulingEvent ev, string dashboardUrl) =>
        Task.FromResult(DeadlinePassed(ev, dashboardUrl));

    Task<ComposedEmail> IEmailComposer.DeliveryProblemAsync(SchedulingEvent ev, string address, string dashboardUrl) =>
        Task.FromResult(DeliveryProblem(ev, address, dashboardUrl));

    Task<ComposedEmail> IEmailComposer.AddressVerificationAsync(string email, string link, string requestedBy) =>
        Task.FromResult(AddressVerification(email, link, requestedBy));

    Task<ComposedEmail> IEmailComposer.SignInLinkAsync(string email, string link) =>
        Task.FromResult(SignInLink(email, link));

    Task<ComposedEmail> IEmailComposer.OrganizerRequestWaitingAsync(string requester, string reviewUrl) =>
        Task.FromResult(OrganizerRequestWaiting(requester, reviewUrl));

    Task<ComposedEmail> IEmailComposer.OrganizerRequestApprovedAsync(string email, string newEventUrl) =>
        Task.FromResult(OrganizerRequestApproved(email, newEventUrl));

    Task<ComposedEmail> IEmailComposer.OrganizerRequestDeclinedAsync(string email, string canAskAgainAt) =>
        Task.FromResult(OrganizerRequestDeclined(email, canAskAgainAt));

    // ── Wording shared with the HTML composer ───────────────────────────────

    public const string OrganizerRequestWaitingSubject = "A request to organize is waiting on the scheduling server";

    public static string OrganizerRequestWaitingIntro(string requester) =>
        $"{requester} has asked to create scheduling events on this server.";

    public const string OrganizerRequestWaitingClosing =
        "You're getting this because you're an admin. Any note they wrote is shown only on the page. " +
        "This server emails the admins when a request arrives and none was already waiting, so check the page for others.";

    public const string OrganizerRequestApprovedSubject = "You can now schedule meetings on the scheduling server";

    public static string OrganizerRequestApprovedIntro(string email) =>
        $"An admin approved your request: {email} can now create scheduling events.";

    public const string OrganizerRequestDeclinedSubject = "Your request to organize on the scheduling server";

    public static string OrganizerRequestDeclinedText(string email, string canAskAgainAt) =>
        $"An admin declined the request for {email} to create scheduling events. You can still vote on any meeting " +
        $"you're invited to. If you think this was a mistake, you can ask again from {canAskAgainAt}.";

    public const string FinalizedClosing = "A calendar invitation will come from the organizer's calendar. Accept or decline it there.";

    public const string SignInLinkSubject = "Your sign-in link for the scheduling server";

    public static string AddressVerificationSubject(string email) => $"Confirm {email} for the scheduling server";

    public static string OrganizerName(SchedulingEvent ev) => ev.OrganizerName ?? ev.OrganizerEmail;

    public static string ParticipantName(Participant participant) => participant.DisplayName ?? participant.Email;

    public static string CancelledSubject(SchedulingEvent ev, bool meetingWasSet) =>
        meetingWasSet ? $"Cancelled: {ev.Title}" : $"Invitation withdrawn: {ev.Title}";

    public static string CancelledIntro(SchedulingEvent ev, bool meetingWasSet) => meetingWasSet
        ? $"{OrganizerName(ev)} has cancelled this meeting. It won't take place."
        : $"{OrganizerName(ev)} has withdrawn this invitation. No time will be picked, so there's nothing left to vote on.";

    public static string CantMakeAnyTimeIntro(SchedulingEvent ev, Participant who) =>
        $"{ParticipantName(who)} ({who.Email}) is required for \"{ev.Title}\" and answered no to every proposed time, " +
        "so none of the current times can work.";

    public static string CantMakeAnyTimeClosing(SchedulingEvent ev, Participant who) =>
        (who.Note is null ? "" : "They left you a note; read it on the event page. ") +
        $"To keep going, propose more times, or ask your assistant to propose more times for scheduling event {ev.ShortCode}.";

    public static string NewSlotsIntro(SchedulingEvent ev, bool rescheduling) => rescheduling
        ? $"{OrganizerName(ev)} needs to reschedule and has proposed new times."
        : $"{OrganizerName(ev)} has proposed more times.";

    public static string ReminderIntro(SchedulingEvent ev) =>
        $"{OrganizerName(ev)} is still waiting for your answers about a meeting time, and voting closes soon.";

    public static string DeadlinePassedIntro(SchedulingEvent ev) =>
        $"The voting deadline for \"{ev.Title}\" passed before you picked a time, so voting has closed.";

    public static string DeadlinePassedClosing(SchedulingEvent ev) =>
        $"To keep going, propose more times (that reopens voting), or ask your assistant to propose more times for scheduling event {ev.ShortCode}.";

    public static string DeliveryProblemIntro(SchedulingEvent ev, string address) =>
        $"Email about \"{ev.Title}\" to {address} couldn't be delivered: the receiving mail server refused the address.";

    public const string DeliveryProblemClosing =
        "This server won't email that address again. They are still a participant; check the address with them, or tell them about the meeting yourself.";

    /// <summary>Active slots the participant hasn't answered, earliest first.</summary>
    public static IReadOnlyList<ProposedSlot> UnansweredSlots(SchedulingEvent ev, Participant participant) =>
        ev.ActiveSlots.Where(s => participant.Votes.All(v => v.SlotId != s.Id)).OrderBy(s => s.Start).ToList();

    public static string LinkClosing(string ifNotYou) =>
        $"The link works once, for {(int)VerificationLinks.Lifetime.TotalMinutes} minutes. If {ifNotYou}.";

    /// <summary>The footer line on event emails: why the recipient got it, and how to stop.</summary>
    public static string UnsubscribeLine(string email) =>
        $"Sent to {email} about a meeting being scheduled. To get no more email from this server:";

    // ── Helpers ─────────────────────────────────────────────────────────────

    private ComposedEmail EventEmail(string to, string subject, StringBuilder body)
    {
        if (unsubscribe is null)
            return new ComposedEmail(subject, body.ToString());

        body.AppendLine();
        body.AppendLine("--");
        body.AppendLine(UnsubscribeLine(to));
        body.AppendLine(unsubscribe.PageUrl(to));
        return new ComposedEmail(subject, body.ToString(), ListUnsubscribeUrl: unsubscribe.OneClickUrl(to));
    }

    private static string Subject(SchedulingEvent ev, string text) => $"[sched:{ev.ShortCode}] {text}";

    private static string Deadline(SchedulingEvent ev) =>
        ev.RespondBy is { } by ? $" (vote by {SlotTimeFormatter.Display(by, ev.OrganizerTimeZone)})" : "";

    private static void AppendEvent(StringBuilder body, SchedulingEvent ev)
    {
        body.AppendLine();
        body.AppendLine($"Meeting: {ev.Title}");
        body.AppendLine($"Length: {(int)ev.Duration.TotalMinutes} minutes");
        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            body.AppendLine("Details from the organizer:");
            foreach (var line in ev.Description.Split('\n'))
                body.AppendLine($"  > {line.TrimEnd()}");
        }
        body.AppendLine();
    }

    private static void AppendSlots(StringBuilder body, SchedulingEvent ev, IEnumerable<ProposedSlot> slots, string heading = "Proposed times:")
    {
        body.AppendLine(heading);
        foreach (var slot in slots.OrderBy(s => s.Start))
            body.AppendLine($"  - {SlotTimeFormatter.Display(slot.Start, ev.OrganizerTimeZone)}");
        body.AppendLine();
    }

    private void AppendHowToVote(StringBuilder body, SchedulingEvent ev, string votingUrl)
    {
        body.AppendLine($"Vote here (this link is personal to you): {votingUrl}");
        body.AppendLine();
        body.AppendLine("Or, if you use an AI assistant with the scheduling server connected, ask it to");
        body.AppendLine($"\"check my scheduling requests\". Server: {mcpUrl}  Event: {ev.ShortCode}");
    }
}
