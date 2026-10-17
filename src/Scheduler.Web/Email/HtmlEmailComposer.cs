using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Scheduler.Core.Email;
using Scheduler.Core.Model;
using Scheduler.Web.Email.Templates;

namespace Scheduler.Web.Email;

/// <summary>
/// The web app's composer: subjects and plain-text bodies from <see cref="PlainTextEmailComposer"/>, plus an HTML
/// alternative rendered from the Razor components in <c>Email/Templates</c> with <see cref="HtmlRenderer"/>, so
/// the markup lives next to the UI. See docs/design.md, "Email".
/// </summary>
internal sealed class HtmlEmailComposer(
    PlainTextEmailComposer text,
    IServiceScopeFactory scopes,
    ILoggerFactory loggers,
    IUnsubscribeLinks unsubscribe) : IEmailComposer
{
    private const string PersonalLink = "This link is personal to you: anyone you forward it to can vote as you.";

    public Task<ComposedEmail> InvitationAsync(SchedulingEvent ev, Participant recipient, string votingUrl) =>
        WithEventHtmlAsync(text.Invitation(ev, recipient, votingUrl), recipient.Email, ev, new()
        {
            [nameof(EventEmail.Intro)] = $"{PlainTextEmailComposer.OrganizerName(ev)} is looking for a time to meet and has proposed the times below.",
            [nameof(EventEmail.Slots)] = ev.ActiveSlots.ToList(),
            [nameof(EventEmail.ActionUrl)] = votingUrl,
            [nameof(EventEmail.ActionLabel)] = "Choose the times that work",
            [nameof(EventEmail.ActionNote)] = PersonalLink,
            [nameof(EventEmail.McpUrl)] = text.McpUrl,
        });

    public Task<ComposedEmail> NewSlotsAsync(SchedulingEvent ev, Participant recipient, string votingUrl, IReadOnlyList<ProposedSlot> newSlots, bool rescheduling) =>
        WithEventHtmlAsync(text.NewSlots(ev, recipient, votingUrl, newSlots, rescheduling), recipient.Email, ev, new()
        {
            [nameof(EventEmail.Intro)] = PlainTextEmailComposer.NewSlotsIntro(ev, rescheduling),
            [nameof(EventEmail.Slots)] = newSlots,
            [nameof(EventEmail.SlotsHeading)] = "New times",
            [nameof(EventEmail.ActionUrl)] = votingUrl,
            [nameof(EventEmail.ActionLabel)] = "Answer the new times",
            [nameof(EventEmail.ActionNote)] = PersonalLink,
            [nameof(EventEmail.McpUrl)] = text.McpUrl,
        });

    public Task<ComposedEmail> FinalizedAsync(SchedulingEvent ev, Participant recipient, ProposedSlot slot) =>
        WithEventHtmlAsync(text.Finalized(ev, recipient, slot), recipient.Email, ev, new()
        {
            [nameof(EventEmail.Intro)] = $"{PlainTextEmailComposer.OrganizerName(ev)} has picked a time.",
            [nameof(EventEmail.Chosen)] = slot,
            [nameof(EventEmail.Closing)] = PlainTextEmailComposer.FinalizedClosing,
        });

    public Task<ComposedEmail> CancelledAsync(SchedulingEvent ev, Participant recipient, string? reason) =>
        WithEventHtmlAsync(text.Cancelled(ev, recipient, reason), recipient.Email, ev, new()
        {
            [nameof(EventEmail.Intro)] = $"{PlainTextEmailComposer.OrganizerName(ev)} has cancelled this meeting.",
            [nameof(EventEmail.Reason)] = reason,
        });

    public Task<ComposedEmail> AllRespondedAsync(SchedulingEvent ev, string dashboardUrl) =>
        WithEventHtmlAsync(text.AllResponded(ev, dashboardUrl), ev.OrganizerEmail, ev, new()
        {
            [nameof(EventEmail.Intro)] = "Everyone required has answered every proposed time.",
            [nameof(EventEmail.ActionUrl)] = dashboardUrl,
            [nameof(EventEmail.ActionLabel)] = "See the results and pick a time",
            [nameof(EventEmail.Closing)] = $"Or ask your assistant to finalize scheduling event {ev.ShortCode}.",
        });

    public Task<ComposedEmail> ReminderAsync(SchedulingEvent ev, Participant recipient, string votingUrl) =>
        WithEventHtmlAsync(text.Reminder(ev, recipient, votingUrl), recipient.Email, ev, new()
        {
            [nameof(EventEmail.Intro)] = PlainTextEmailComposer.ReminderIntro(ev),
            [nameof(EventEmail.Slots)] = PlainTextEmailComposer.UnansweredSlots(ev, recipient),
            [nameof(EventEmail.SlotsHeading)] = "Times you haven't answered",
            [nameof(EventEmail.ActionUrl)] = votingUrl,
            [nameof(EventEmail.ActionLabel)] = "Choose the times that work",
            [nameof(EventEmail.ActionNote)] = PersonalLink,
            [nameof(EventEmail.McpUrl)] = text.McpUrl,
        });

    public Task<ComposedEmail> DeadlinePassedAsync(SchedulingEvent ev, string dashboardUrl) =>
        WithEventHtmlAsync(text.DeadlinePassed(ev, dashboardUrl), ev.OrganizerEmail, ev, new()
        {
            [nameof(EventEmail.Intro)] = PlainTextEmailComposer.DeadlinePassedIntro(ev),
            [nameof(EventEmail.ActionUrl)] = dashboardUrl,
            [nameof(EventEmail.ActionLabel)] = "See the answers so far",
            [nameof(EventEmail.Closing)] = PlainTextEmailComposer.DeadlinePassedClosing(ev),
        });

    public async Task<ComposedEmail> AddressVerificationAsync(string email, string link, string requestedBy)
    {
        var plain = text.AddressVerification(email, link, requestedBy);
        return plain with
        {
            HtmlBody = await RenderAsync<LinkEmail>(new()
            {
                [nameof(LinkEmail.Subject)] = plain.Subject,
                [nameof(LinkEmail.Paragraphs)] = new[]
                {
                    $"{requestedBy} asked to add {email} to their account on the scheduling server. Confirming lets that account see and answer meeting invitations sent to this address.",
                    $"If that's you, confirm while signed in as {requestedBy}.",
                },
                [nameof(LinkEmail.ActionUrl)] = link,
                [nameof(LinkEmail.ActionLabel)] = "Confirm this address",
                [nameof(LinkEmail.Closing)] = PlainTextEmailComposer.LinkClosing("you didn't ask for this, ignore this email; nothing changes"),
            }),
        };
    }

    public async Task<ComposedEmail> SignInLinkAsync(string email, string link)
    {
        var plain = text.SignInLink(email, link);
        return plain with
        {
            HtmlBody = await RenderAsync<LinkEmail>(new()
            {
                [nameof(LinkEmail.Subject)] = plain.Subject,
                [nameof(LinkEmail.Paragraphs)] = new[] { $"Someone is signing in to the scheduling server as {email}. If it's you, finish signing in here." },
                [nameof(LinkEmail.ActionUrl)] = link,
                [nameof(LinkEmail.ActionLabel)] = "Sign in",
                [nameof(LinkEmail.Closing)] = PlainTextEmailComposer.LinkClosing("you didn't try to sign in, ignore this email"),
            }),
        };
    }

    private async Task<ComposedEmail> WithEventHtmlAsync(ComposedEmail plain, string to, SchedulingEvent ev, Dictionary<string, object?> parameters)
    {
        parameters[nameof(EventEmail.Subject)] = plain.Subject;
        parameters[nameof(EventEmail.Event)] = ev;
        parameters[nameof(EventEmail.UnsubscribeUrl)] = unsubscribe.PageUrl(to);
        parameters[nameof(EventEmail.UnsubscribeLine)] = PlainTextEmailComposer.UnsubscribeLine(to);
        return plain with { HtmlBody = await RenderAsync<EventEmail>(parameters) };
    }

    /// <summary>Renders outside any request: the renderer needs a scope of its own.</summary>
    private async Task<string> RenderAsync<TComponent>(Dictionary<string, object?> parameters) where TComponent : IComponent
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, loggers);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }
}
