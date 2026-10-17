using System.Text;
using Scheduler.Core.Model;
using Scheduler.Core.Security;
using Scheduler.Core.Time;

namespace Scheduler.Core.Email;

/// <summary>
/// Plain-text notifications. Deliberately simple: roadmap item 2 replaces it with Razor templates that add
/// HTML bodies. Plain text stays the primary body because agents read it.
/// </summary>
public sealed class PlainTextEmailComposer(string mcpUrl) : IEmailComposer
{
    public ComposedEmail Invitation(SchedulingEvent ev, Participant recipient, string votingUrl)
    {
        var body = new StringBuilder();
        body.AppendLine($"{Organizer(ev)} is looking for a time to meet and has proposed the times below.");
        AppendEvent(body, ev);
        AppendSlots(body, ev, ev.ActiveSlots);
        AppendHowToVote(body, ev, votingUrl);
        return new ComposedEmail(Subject(ev, $"{Organizer(ev)} asks: {ev.Title}{Deadline(ev)}"), body.ToString());
    }

    public ComposedEmail NewSlots(SchedulingEvent ev, Participant recipient, string votingUrl, IReadOnlyList<ProposedSlot> newSlots, bool rescheduling)
    {
        var body = new StringBuilder();
        body.AppendLine(rescheduling
            ? $"{Organizer(ev)} needs to reschedule and has proposed new times."
            : $"{Organizer(ev)} has proposed more times.");
        AppendEvent(body, ev);
        AppendSlots(body, ev, newSlots);
        AppendHowToVote(body, ev, votingUrl);
        return new ComposedEmail(Subject(ev, $"New times for: {ev.Title}{Deadline(ev)}"), body.ToString());
    }

    public ComposedEmail Finalized(SchedulingEvent ev, Participant recipient, ProposedSlot slot)
    {
        var body = new StringBuilder();
        body.AppendLine($"{Organizer(ev)} has picked a time:");
        body.AppendLine();
        body.AppendLine($"  {SlotTimeFormatter.Display(slot.Start, ev.OrganizerTimeZone)}, {(int)ev.Duration.TotalMinutes} minutes");
        AppendEvent(body, ev);
        body.AppendLine("A calendar invitation will come from the organizer's calendar. Accept or decline it there.");
        return new ComposedEmail(Subject(ev, $"Set: {ev.Title}"), body.ToString());
    }

    public ComposedEmail Cancelled(SchedulingEvent ev, Participant recipient, string? reason)
    {
        var body = new StringBuilder();
        body.AppendLine($"{Organizer(ev)} has cancelled this meeting.");
        AppendEvent(body, ev);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            body.AppendLine("Reason given by the organizer:");
            body.AppendLine($"  \"{reason}\"");
        }
        return new ComposedEmail(Subject(ev, $"Cancelled: {ev.Title}"), body.ToString());
    }

    public ComposedEmail AllResponded(SchedulingEvent ev, string dashboardUrl)
    {
        var body = new StringBuilder();
        body.AppendLine($"Everyone required has answered every proposed time for \"{ev.Title}\".");
        body.AppendLine();
        body.AppendLine($"See the results and pick a time: {dashboardUrl}");
        body.AppendLine($"Or ask your assistant to finalize scheduling event {ev.ShortCode}.");
        return new ComposedEmail(Subject(ev, $"Everyone has responded: {ev.Title}"), body.ToString());
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
        body.AppendLine($"The link works once, for {LinkMinutes} minutes. If you didn't ask for this, ignore this email; nothing changes.");
        return new ComposedEmail($"Confirm {email} for the scheduling server", body.ToString());
    }

    public ComposedEmail SignInLink(string email, string link)
    {
        var body = new StringBuilder();
        body.AppendLine($"Someone is signing in to the scheduling server as {email}. If it's you, finish signing in here:");
        body.AppendLine($"  {link}");
        body.AppendLine();
        body.AppendLine($"The link works once, for {LinkMinutes} minutes. If you didn't try to sign in, ignore this email.");
        return new ComposedEmail("Your sign-in link for the scheduling server", body.ToString());
    }

    private static int LinkMinutes => (int)VerificationLinks.Lifetime.TotalMinutes;

    private static string Subject(SchedulingEvent ev, string text) => $"[sched:{ev.ShortCode}] {text}";

    private static string Organizer(SchedulingEvent ev) => ev.OrganizerName ?? ev.OrganizerEmail;

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

    private static void AppendSlots(StringBuilder body, SchedulingEvent ev, IEnumerable<ProposedSlot> slots)
    {
        body.AppendLine("Proposed times:");
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
