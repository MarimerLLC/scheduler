using Scheduler.Core.Model;

namespace Scheduler.Core.Email;

/// <summary>
/// Subject and bodies of one notification, before it becomes an outbox row. <see cref="ListUnsubscribeUrl"/>
/// is the recipient's one-click unsubscribe URL (RFC 8058), for the List-Unsubscribe header.
/// </summary>
public sealed record ComposedEmail(string Subject, string TextBody, string? HtmlBody = null, string? ListUnsubscribeUrl = null);

/// <summary>
/// Writes the notification emails: a plain-text body (agents read it) and, from the web app's composer, an HTML
/// alternative. <paramref name="votingUrl"/> parameters are the recipient's personal magic link. Every subject
/// about an event starts with <c>[sched:SHORTCODE]</c>, and every email about an event carries the recipient's
/// unsubscribe link. Text from the event (title, description, reason) is someone else's writing and is quoted,
/// never presented as instructions. See docs/design.md, "Email".
/// </summary>
public interface IEmailComposer
{
    Task<ComposedEmail> InvitationAsync(SchedulingEvent ev, Participant recipient, string votingUrl);

    Task<ComposedEmail> NewSlotsAsync(SchedulingEvent ev, Participant recipient, string votingUrl, IReadOnlyList<ProposedSlot> newSlots, bool rescheduling);

    Task<ComposedEmail> FinalizedAsync(SchedulingEvent ev, Participant recipient, ProposedSlot slot);

    Task<ComposedEmail> CancelledAsync(SchedulingEvent ev, Participant recipient, string? reason);

    /// <summary>To the organizer: every required participant has answered every slot.</summary>
    Task<ComposedEmail> AllRespondedAsync(SchedulingEvent ev, string dashboardUrl);

    /// <summary>To a participant who hasn't answered every time, about a day before the deadline.</summary>
    Task<ComposedEmail> ReminderAsync(SchedulingEvent ev, Participant recipient, string votingUrl);

    /// <summary>To the organizer: the deadline passed without a decision, so voting closed.</summary>
    Task<ComposedEmail> DeadlinePassedAsync(SchedulingEvent ev, string dashboardUrl);

    /// <summary>To an address being added to <paramref name="requestedBy"/>'s account: a link to confirm it.</summary>
    Task<ComposedEmail> AddressVerificationAsync(string email, string link, string requestedBy);

    /// <summary>To an address a sign-in provider didn't verify: a link that finishes signing in.</summary>
    Task<ComposedEmail> SignInLinkAsync(string email, string link);
}

/// <summary>
/// Per-address unsubscribe links. The token is a protected payload of the address, so it needs no table and
/// works in any old email. See docs/design.md, "Email".
/// </summary>
public interface IUnsubscribeLinks
{
    /// <summary>The page a person opens from the email footer, which asks before unsubscribing.</summary>
    string PageUrl(string email);

    /// <summary>The RFC 8058 one-click URL for the List-Unsubscribe header: a POST to it unsubscribes.</summary>
    string OneClickUrl(string email);
}
