using Scheduler.Core.Model;

namespace Scheduler.Core.Email;

/// <summary>Subject and bodies of one notification, before it becomes an outbox row.</summary>
public sealed record ComposedEmail(string Subject, string TextBody, string? HtmlBody = null);

/// <summary>
/// Writes the notification emails. <paramref name="votingUrl"/> parameters are the recipient's personal
/// magic link. Every subject starts with <c>[sched:SHORTCODE]</c>. Text from the event (title, description,
/// reason) is someone else's writing and is quoted, never presented as instructions.
/// </summary>
public interface IEmailComposer
{
    ComposedEmail Invitation(SchedulingEvent ev, Participant recipient, string votingUrl);

    ComposedEmail NewSlots(SchedulingEvent ev, Participant recipient, string votingUrl, IReadOnlyList<ProposedSlot> newSlots, bool rescheduling);

    ComposedEmail Finalized(SchedulingEvent ev, Participant recipient, ProposedSlot slot);

    ComposedEmail Cancelled(SchedulingEvent ev, Participant recipient, string? reason);

    /// <summary>To the organizer: every required participant has answered every slot.</summary>
    ComposedEmail AllResponded(SchedulingEvent ev, string dashboardUrl);
}
