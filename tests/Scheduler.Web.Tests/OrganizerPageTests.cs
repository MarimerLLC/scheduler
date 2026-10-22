using System.Net;
using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Scheduler.Core.Email;
using Scheduler.Core.Model;
using Scheduler.Core.Services;
using Scheduler.Web.Components.Pages;

namespace Scheduler.Web.Tests;

/// <summary>
/// The organizer pages, rendered as they are once interactive (bUnit, with the app's services), and the job emails'
/// HTML. The pages load nothing while prerendering. The interactive actions are thin calls into ISchedulingService,
/// tested in Scheduler.Data.Tests.
/// </summary>
[TestClass]
public sealed class OrganizerPageTests
{
    private static readonly CallerIdentity Organizer = new(null, ["rocky@example.com"], Channel.Mcp);

    private SchedulerApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SchedulerApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task The_prerendered_page_loads_nothing()
    {
        var ev = await CreateEventAsync();

        var html = await PrerenderAsync("rocky@example.com", "/events");

        StringAssert.Contains(html, "Loading…");
        Assert.DoesNotContain($"/events/{ev.Id}", html);
    }

    [TestMethod]
    public async Task The_dashboard_shows_what_the_organizer_runs_and_how_many_answered()
    {
        var ev = await CreateEventAsync();
        await VoteAsync("bob@example.com", ev);

        var html = await RenderAsync<Events>("rocky@example.com");

        StringAssert.Contains(html, $"/events/{ev.Id}");
        StringAssert.Contains(html, "1 of 3");
        StringAssert.Contains(html, "New event");
    }

    [TestMethod]
    public async Task The_dashboard_shows_a_participant_what_waits_for_their_answer()
    {
        var ev = await CreateEventAsync();

        var html = await RenderAsync<Events>("carol@example.com");

        StringAssert.Contains(html, "Waiting for your answer");
        StringAssert.Contains(html, $"/e/{ev.ShortCode}");
        Assert.DoesNotContain($"/events/{ev.Id}", html);
    }

    [TestMethod]
    public async Task The_event_page_shows_the_organizer_the_tallies_and_who_answered_what()
    {
        var ev = await CreateEventAsync();
        await VoteAsync("bob@example.com", ev);

        var html = await RenderAsync<EventDetails>("rocky@example.com", p => p.Add(e => e.Id, ev.Id));

        StringAssert.Contains(html, ev.Slots[0].OrganizerLocal);
        StringAssert.Contains(html, "Bob");
        StringAssert.Contains(html, "Works");
        StringAssert.Contains(html, "Propose more times");
        StringAssert.Contains(html, "Cancel this event");
    }

    [TestMethod]
    public async Task A_participant_on_the_event_page_is_sent_to_the_voting_page_and_a_stranger_sees_nothing()
    {
        var ev = await CreateEventAsync();

        var participant = await RenderAsync<EventDetails>("bob@example.com", p => p.Add(e => e.Id, ev.Id));
        var stranger = await RenderAsync<EventDetails>("mallory@example.com", p => p.Add(e => e.Id, ev.Id));

        StringAssert.Contains(participant, $"/e/{ev.ShortCode}");
        Assert.DoesNotContain("Cancel this event", participant);
        StringAssert.Contains(stranger, "no event here for you");
        Assert.DoesNotContain("Planning", stranger);
    }

    [TestMethod]
    public async Task The_compose_page_is_offered_to_organizers()
    {
        var html = await RenderAsync<NewEvent>("rocky@example.com");

        StringAssert.Contains(html, "Times that work for you");
        StringAssert.Contains(html, "Send invitations");
    }

    [TestMethod]
    public async Task The_reminder_and_deadline_emails_have_html_bodies()
    {
        var ev = new SchedulingEvent
        {
            ShortCode = "7KQ2M9XD",
            Title = "Planning",
            OrganizerEmail = "rocky@example.com",
            OrganizerTimeZone = "America/Chicago",
            Duration = Duration.FromMinutes(30),
        };
        ev.Slots.Add(new ProposedSlot { EventId = ev.Id, TimeZoneId = "America/Chicago", LocalStart = new LocalDateTime(2026, 11, 3, 10, 0), Start = Instant.FromUtc(2026, 11, 3, 16, 0) });
        var bob = new Participant { EventId = ev.Id, Email = "bob@example.com", TokenNonce = new byte[16] };
        ev.Participants.Add(bob);
        var composer = _app.Services.GetRequiredService<IEmailComposer>();

        var reminder = await composer.ReminderAsync(ev, bob, "https://sched.test/e/7KQ2M9XD?t=x");
        var closed = await composer.DeadlinePassedAsync(ev, "https://sched.test/events/1");

        StringAssert.Contains(reminder.HtmlBody, "Times you haven&#x27;t answered");
        StringAssert.Contains(reminder.HtmlBody, "Tue Nov 3, 10:00 AM CST");
        Assert.IsNotNull(reminder.ListUnsubscribeUrl);
        StringAssert.Contains(closed.HtmlBody, "voting has closed");
        StringAssert.Contains(closed.Subject, "Voting closed: Planning");
    }

    /// <summary>A page as the server first sends it (prerendered), signed in with the development sign-in.</summary>
    private async Task<string> PrerenderAsync(string email, string path)
    {
        var browser = _app.CreateBrowser();
        await browser.Http.PostAsync("/dev/signin", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email }));
        var response = await browser.Http.GetAsync(path);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, html);
        return html;
    }

    /// <summary>A page as it shows once interactive and loaded, signed in as <paramref name="email"/>.</summary>
    private async Task<string> RenderAsync<TPage>(string email, Action<ComponentParameterCollectionBuilder<TPage>>? parameters = null)
        where TPage : IComponent
    {
        using var scope = _app.Services.CreateScope();
        await using var bunit = new BunitContext();
        bunit.Services.AddFallbackServiceProvider(scope.ServiceProvider);
        var auth = bunit.AddAuthorization();
        auth.SetAuthorized(email);
        auth.SetClaims(new Claim(ClaimTypes.Email, email));
        bunit.JSInterop.Mode = JSRuntimeMode.Loose;
        bunit.SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        var page = bunit.Render(parameters ?? (_ => { }));
        page.WaitForAssertion(() => Assert.DoesNotContain("Loading", page.Markup));
        return WebUtility.HtmlDecode(page.Markup);
    }

    private async Task<EventView> CreateEventAsync()
    {
        using var scope = _app.Services.CreateScope();
        var day = SystemClock.Instance.GetCurrentInstant().InUtc().Date.PlusDays(7);
        return await scope.ServiceProvider.GetRequiredService<ISchedulingService>().CreateEventAsync(Organizer, new CreateEventRequest(
            "Planning", "Q4 roadmap", 30, "America/Chicago",
            [new("bob@example.com", "Bob"), new("carol@example.com", "Carol"), new("dave@example.com", "Dave", AttendeeRole.Optional)],
            [.. Enumerable.Range(0, 3).Select(i => new SlotInput($"{day.PlusDays(i):yyyy-MM-dd}T10:00", "America/Chicago"))],
            null, "Rocky"), CancellationToken.None);
    }

    private async Task VoteAsync(string email, EventView ev)
    {
        using var scope = _app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISchedulingService>().VoteAsync(
            new CallerIdentity(null, [email], Channel.Web), ev.ShortCode,
            [.. ev.Slots.Select(s => new VoteInput(s.SlotId, VoteValue.Preferred))], null, CancellationToken.None);
    }
}
