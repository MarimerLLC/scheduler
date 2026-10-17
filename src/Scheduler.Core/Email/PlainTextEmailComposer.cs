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

    public ComposedEmail Cancelled(SchedulingEvent ev, Participant recipient, string? reason)
    {
        var body = new StringBuilder();
        body.AppendLine($"{OrganizerName(ev)} has cancelled this meeting.");
        AppendEvent(body, ev);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            body.AppendLine("Reason given by the organizer:");
            body.AppendLine($"  \"{reason}\"");
        }
        return EventEmail(recipient.Email, Subject(ev, $"Cancelled: {ev.Title}"), body);
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

    Task<ComposedEmail> IEmailComposer.InvitationAsync(SchedulingEvent ev, Participant recipient, string votingUrl) =>
        Task.FromResult(Invitation(ev, recipient, votingUrl));

    Task<ComposedEmail> IEmailComposer.NewSlotsAsync(SchedulingEvent ev, Participant recipient, string votingUrl, IReadOnlyList<ProposedSlot> newSlots, bool rescheduling) =>
        Task.FromResult(NewSlots(ev, recipient, votingUrl, newSlots, rescheduling));

    Task<ComposedEmail> IEmailComposer.FinalizedAsync(SchedulingEvent ev, Participant recipient, ProposedSlot slot) =>
        Task.FromResult(Finalized(ev, recipient, slot));

    Task<ComposedEmail> IEmailComposer.CancelledAsync(SchedulingEvent ev, Participant recipient, string? reason) =>
        Task.FromResult(Cancelled(ev, recipient, reason));

    Task<ComposedEmail> IEmailComposer.AllRespondedAsync(SchedulingEvent ev, string dashboardUrl) =>
        Task.FromResult(AllResponded(ev, dashboardUrl));

    Task<ComposedEmail> IEmailComposer.ReminderAsync(SchedulingEvent ev, Participant recipient, string votingUrl) =>
        Task.FromResult(Reminder(ev, recipient, votingUrl));

    Task<ComposedEmail> IEmailComposer.DeadlinePassedAsync(SchedulingEvent ev, string dashboardUrl) =>
        Task.FromResult(DeadlinePassed(ev, dashboardUrl));

    Task<ComposedEmail> IEmailComposer.AddressVerificationAsync(string email, string link, string requestedBy) =>
        Task.FromResult(AddressVerification(email, link, requestedBy));

    Task<ComposedEmail> IEmailComposer.SignInLinkAsync(string email, string link) =>
        Task.FromResult(SignInLink(email, link));

    // ── Wording shared with the HTML composer ───────────────────────────────

    public const string FinalizedClosing = "A calendar invitation will come from the organizer's calendar. Accept or decline it there.";

    public const string SignInLinkSubject = "Your sign-in link for the scheduling server";

    public static string AddressVerificationSubject(string email) => $"Confirm {email} for the scheduling server";

    public static string OrganizerName(SchedulingEvent ev) => ev.OrganizerName ?? ev.OrganizerEmail;

    public static string NewSlotsIntro(SchedulingEvent ev, bool rescheduling) => rescheduling
        ? $"{OrganizerName(ev)} needs to reschedule and has proposed new times."
        : $"{OrganizerName(ev)} has proposed more times.";

    public static string ReminderIntro(SchedulingEvent ev) =>
        $"{OrganizerName(ev)} is still waiting for your answers about a meeting time, and voting closes soon.";

    public static string DeadlinePassedIntro(SchedulingEvent ev) =>
        $"The voting deadline for \"{ev.Title}\" passed before you picked a time, so voting has closed.";

    public static string DeadlinePassedClosing(SchedulingEvent ev) =>
        $"To keep going, propose more times (that reopens voting), or ask your assistant to propose more times for scheduling event {ev.ShortCode}.";

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
