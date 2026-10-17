using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Scheduler.Core.Model;
using Scheduler.Core.Services;
using Scheduler.Data;

namespace Scheduler.Web.Tests;

/// <summary>The emails an event sends, the voting page they link to, and unsubscribing from them.</summary>
[TestClass]
public sealed partial class VotingPageTests
{
    private static readonly CallerIdentity Organizer = new(null, ["rocky@example.com"], Channel.Mcp);

    private SchedulerApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SchedulerApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task An_invitation_has_an_html_body_with_the_personal_link_and_unsubscribe_headers()
    {
        await CreateEventAsync(title: "Planning <script>alert(1)</script>");
        var invitation = (await _app.OutboxAsync()).Single(o => o.To == "bob@example.com");

        Assert.IsNotNull(invitation.HtmlBody);
        StringAssert.Contains(invitation.HtmlBody, "Planning &lt;script&gt;alert(1)&lt;/script&gt;", "event text is encoded");
        Assert.DoesNotContain("<script>", invitation.HtmlBody);
        StringAssert.Contains(invitation.HtmlBody, WebUtility.HtmlEncode(VotingLinkIn(invitation)));
        StringAssert.Contains(invitation.ListUnsubscribeUrl, "/unsubscribe/one-click?t=");
        StringAssert.Contains(invitation.TextBody, "/unsubscribe?t=");
    }

    [TestMethod]
    public async Task A_participant_votes_from_the_link_in_their_email()
    {
        var ev = await CreateEventAsync();
        var link = VotingLinkIn((await _app.OutboxAsync()).Single(o => o.To == "bob@example.com"));
        var phone = _app.CreateBrowser();

        // Opening the link moves the token into a cookie for this event and drops it from the URL.
        var response = await phone.Http.GetAsync(link);
        var page = SchedulerApp.Browser.RedirectOf(response);
        Assert.AreEqual($"/e/{ev.ShortCode}", page.PathAndQuery);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("scheduler.vote=", StringComparison.Ordinal));
        StringAssert.Contains(cookie, $"path=/e/{ev.ShortCode}");
        StringAssert.Contains(cookie, "httponly");
        Assert.IsNull(await VotesOfAsync("bob@example.com"), "opening the link doesn't vote");

        var html = await TextOf(await phone.Http.GetAsync(page));
        StringAssert.Contains(html, "Planning");
        StringAssert.Contains(html, ev.Slots[0].OrganizerLocal);

        response = await phone.SubmitFormAsync(page.PathAndQuery,
            (SlotField(ev.Slots[0].SlotId), "preferred"),
            (SlotField(ev.Slots[1].SlotId), "no"),
            ("note", "Mornings are best"));
        html = await TextOf(response);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, html);
        StringAssert.Contains(html, "your answers are saved");

        var votes = await VotesOfAsync("bob@example.com");
        Assert.AreEqual(VoteValue.Preferred, votes![ev.Slots[0].SlotId]);
        Assert.AreEqual(VoteValue.No, votes[ev.Slots[1].SlotId]);
        Assert.HasCount(2, votes, "an unanswered time stays unanswered");

        var bob = (await GetAsOrganizerAsync(ev)).Participants.Single(p => p.Email == "bob@example.com");
        Assert.AreEqual("Mornings are best", bob.Note);
    }

    [TestMethod]
    public async Task A_bad_link_or_no_link_explains_what_to_do()
    {
        var ev = await CreateEventAsync();
        var browser = _app.CreateBrowser();

        var html = await TextOf(await browser.Http.GetAsync($"/e/{ev.ShortCode}?t=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        StringAssert.Contains(html, "This voting link isn't valid any more");

        html = await TextOf(await browser.Http.GetAsync($"/e/{ev.ShortCode}"));
        StringAssert.Contains(html, "Open this page from the link in your invitation email");
        Assert.DoesNotContain("Planning", html.Replace("<title>Vote</title>", ""));
    }

    [TestMethod]
    public async Task A_signed_in_participant_votes_without_the_link()
    {
        var ev = await CreateEventAsync();
        var browser = _app.CreateBrowser();
        await browser.Http.PostAsync("/dev/signin", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "carol@example.com" }));

        await browser.SubmitFormAsync($"/e/{ev.ShortCode}", (SlotField(ev.Slots[2].SlotId), "if_necessary"));

        Assert.AreEqual(VoteValue.IfNecessary, (await VotesOfAsync("carol@example.com"))![ev.Slots[2].SlotId]);
    }

    [TestMethod]
    public async Task One_click_unsubscribe_suppresses_the_address_and_the_page_can_undo_it()
    {
        await CreateEventAsync();
        var invitation = (await _app.OutboxAsync()).Single(o => o.To == "bob@example.com");
        var oneClick = new Uri(invitation.ListUnsubscribeUrl!).PathAndQuery;

        // What a mail client sends (RFC 8058): no cookies, no antiforgery token.
        var response = await _app.CreateClient().PostAsync(oneClick, new FormUrlEncodedContent(new Dictionary<string, string> { ["List-Unsubscribe"] = "One-Click" }));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(await SuppressedAsync("bob@example.com"));

        // Opened in a browser instead, the same URL goes to the page, which offers to undo.
        var browser = _app.CreateBrowser();
        var page = SchedulerApp.Browser.RedirectOf(await browser.Http.GetAsync(oneClick)).PathAndQuery;
        StringAssert.StartsWith(page, "/unsubscribe?t=");
        response = await browser.SubmitFormAsync(page);
        StringAssert.Contains(await TextOf(response), "Stop all email");
        Assert.IsFalse(await SuppressedAsync("bob@example.com"));
    }

    [TestMethod]
    public async Task The_unsubscribe_page_asks_before_unsubscribing()
    {
        await CreateEventAsync();
        var page = UnsubscribePageIn((await _app.OutboxAsync()).Single(o => o.To == "dave@example.com"));
        var browser = _app.CreateBrowser();

        await browser.Http.GetAsync(page);
        Assert.IsFalse(await SuppressedAsync("dave@example.com"), "a mail scanner opening the link changes nothing");

        var response = await browser.SubmitFormAsync(page);
        StringAssert.Contains(await TextOf(response), "won't get any more email");
        Assert.IsTrue(await SuppressedAsync("dave@example.com"));

        var forged = await TextOf(await browser.Http.GetAsync("/unsubscribe?t=forged"));
        StringAssert.Contains(forged, "isn't valid");
    }

    private async Task<EventView> CreateEventAsync(string title = "Planning")
    {
        using var scope = _app.Services.CreateScope();
        var now = SystemClock.Instance.GetCurrentInstant().InUtc().Date;
        var day = now.PlusDays(7);
        var slots = Enumerable.Range(0, 3)
            .Select(i => new SlotInput($"{day.PlusDays(i):yyyy-MM-dd}T10:00", "America/Chicago"))
            .ToList();
        var ev = await scope.ServiceProvider.GetRequiredService<ISchedulingService>().CreateEventAsync(Organizer, new CreateEventRequest(
            title, "Q4 roadmap", 30, "America/Chicago",
            [new("bob@example.com", "Bob"), new("carol@example.com", "Carol"), new("dave@example.com", "Dave", AttendeeRole.Optional)],
            slots, null, "Rocky"), CancellationToken.None);
        return ev;
    }

    private async Task<EventView> GetAsOrganizerAsync(EventView ev)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISchedulingService>().GetEventAsync(Organizer, ev.ShortCode, null, CancellationToken.None);
    }

    private async Task<Dictionary<Guid, VoteValue>?> VotesOfAsync(string email)
    {
        await using var db = await _app.Services.GetRequiredService<IDbContextFactory<SchedulerDbContext>>().CreateDbContextAsync();
        var votes = await db.Votes.Where(v => db.Participants.Any(p => p.Id == v.ParticipantId && p.Email == email)).ToListAsync();
        return votes.Count == 0 ? null : votes.ToDictionary(v => v.SlotId, v => v.Value);
    }

    private async Task<bool> SuppressedAsync(string email)
    {
        await using var db = await _app.Services.GetRequiredService<IDbContextFactory<SchedulerDbContext>>().CreateDbContextAsync();
        return await db.Suppressions.AnyAsync(s => s.Email == email);
    }

    /// <summary>A page's HTML with entities decoded, so assertions can use plain text.</summary>
    private static async Task<string> TextOf(HttpResponseMessage response) => WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    private static string SlotField(Guid slotId) => "slot-" + slotId.ToString("N");

    private static string VotingLinkIn(OutboxEmail email) => PathIn(email, VotingLink());

    private static string UnsubscribePageIn(OutboxEmail email) => PathIn(email, UnsubscribePage());

    private static string PathIn(OutboxEmail email, Regex pattern)
    {
        var match = pattern.Match(email.TextBody);
        Assert.IsTrue(match.Success, email.TextBody);
        return match.Groups[1].Value;
    }

    [GeneratedRegex(@"https?://[^/\s]+(/e/[A-Z0-9]+\?t=[A-Za-z0-9_-]+)")]
    private static partial Regex VotingLink();

    [GeneratedRegex(@"https?://[^/\s]+(/unsubscribe\?t=\S+)")]
    private static partial Regex UnsubscribePage();
}
