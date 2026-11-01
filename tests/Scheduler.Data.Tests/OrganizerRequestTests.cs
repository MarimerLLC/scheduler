using Microsoft.EntityFrameworkCore;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

[TestClass]
public sealed class OrganizerRequestTests
{
    private Harness _h = null!;
    private static readonly CallerIdentity Admin = Harness.Admin;
    private static readonly CancellationToken None = CancellationToken.None;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync(o => o.Admins = ["admin@example.com", "second@example.com"]);

    private AdminService Service => _h.AdminService;

    private Task<OrganizerRequestView> AskAsync(CallerIdentity caller, string? note = "Contoso PM") =>
        Service.RequestToOrganizeAsync(caller, null, note, None);

    [TestMethod]
    public async Task ARequestWaitsForAnAdminAndShowsInTheCallersAccess()
    {
        var request = await AskAsync(Harness.Bob);

        Assert.AreEqual(OrganizerRequestStatus.Pending, request.Status);
        Assert.AreEqual("bob@example.com", request.Email);
        Assert.AreEqual("Contoso PM", request.Note);
        Assert.AreEqual("Fri Oct 16, 3:00 PM UTC (UTC+00:00)", request.RequestedAt);

        var access = await Service.GetAccessAsync(Harness.Bob, None);
        Assert.IsFalse(access.CanOrganize);
        Assert.AreEqual(request, access.OrganizerRequest);

        var listed = await Service.ListOrganizerRequestsAsync(Admin, None);
        Assert.AreEqual(request, listed.Single());
    }

    [TestMethod]
    public async Task ApprovingGrantsTheAddressAndEmailsTheRequester()
    {
        var request = await AskAsync(Harness.Bob);

        var approved = await Service.ApproveOrganizerRequestAsync(Admin, request.Id, None);

        Assert.AreEqual(OrganizerRequestStatus.Approved, approved.Status);
        Assert.AreEqual("admin@example.com", approved.DecidedBy);
        Assert.IsTrue((await Service.GetAccessAsync(Harness.Bob, None)).CanOrganize);
        var ev = await _h.Service.CreateEventAsync(Harness.Bob, Harness.Request(participants: [new("carol@example.com")]), None);
        Assert.IsTrue(ev.IsOrganizer);

        var grant = (await Service.ListOrganizersAsync(Admin, None)).Single(e => e.Pattern == "bob@example.com");
        Assert.AreEqual("By request: Contoso PM", grant.Note);
        Assert.IsEmpty(await Service.ListOrganizerRequestsAsync(Admin, None));

        var toBob = (await _h.OutboxAsync()).Single(o => o.To == "bob@example.com" && o.EventId is null);
        StringAssert.Contains(toBob.Subject, "You can now schedule meetings");
        StringAssert.Contains(toBob.TextBody, $"{Harness.BaseUrl}/events/new");
    }

    [TestMethod]
    public async Task DecliningEmailsTheRequesterAndStopsThemAskingUntilTheCooldownEnds()
    {
        var request = await AskAsync(Harness.Bob);

        var declined = await Service.DeclineOrganizerRequestAsync(Admin, request.Id, None);

        Assert.AreEqual(OrganizerRequestStatus.Declined, declined.Status);
        Assert.AreEqual("Sun Nov 15, 3:00 PM UTC (UTC+00:00)", declined.CanAskAgainAt);
        Assert.IsFalse((await Service.GetAccessAsync(Harness.Bob, None)).CanOrganize);
        Assert.AreEqual(OrganizerRequestStatus.Declined, (await Service.GetAccessAsync(Harness.Bob, None)).OrganizerRequest?.Status);

        var toBob = (await _h.OutboxAsync()).Single(o => o.To == "bob@example.com");
        StringAssert.Contains(toBob.TextBody, "declined");
        StringAssert.Contains(toBob.TextBody, "Sun Nov 15");

        var ex = await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => AskAsync(Harness.Bob));
        StringAssert.Contains(ex.Message, "ask again from Sun Nov 15");

        _h.Clock.Advance(NodaTime.Duration.FromDays(30));
        var again = await AskAsync(Harness.Bob);
        Assert.AreEqual(OrganizerRequestStatus.Pending, again.Status);
        Assert.IsNull((await Service.GetAccessAsync(Harness.Carol, None)).OrganizerRequest);
    }

    [TestMethod]
    public async Task AskingAgainWhileARequestWaitsReturnsItUnchanged()
    {
        var first = await AskAsync(Harness.Bob, "first");
        _h.Clock.AdvanceMinutes(5);
        var second = await AskAsync(Harness.Bob, "second");

        Assert.AreEqual(first, second);
        Assert.HasCount(1, await Service.ListOrganizerRequestsAsync(Admin, None));
    }

    [TestMethod]
    public async Task OnlyTheFirstWaitingRequestEmailsTheAdminsAndNeverWithTheNote()
    {
        await AskAsync(Harness.Bob, "Contoso PM");
        await AskAsync(Harness.Carol, "Another note");

        var toAdmins = (await _h.OutboxAsync()).Where(o => o.To is "admin@example.com" or "second@example.com").ToList();
        CollectionAssert.AreEquivalent(new[] { "admin@example.com", "second@example.com" }, toAdmins.Select(o => o.To).ToArray());
        Assert.IsTrue(toAdmins.All(o => o.TextBody.Contains("bob@example.com") && o.TextBody.Contains($"{Harness.BaseUrl}/admin/organizers")));
        Assert.IsTrue(toAdmins.All(o => !o.TextBody.Contains("Contoso PM")), "the note stays out of the admins' inboxes");

        // Once the queue is empty again, the next request emails them again.
        foreach (var request in await Service.ListOrganizerRequestsAsync(Admin, None))
            await Service.DeclineOrganizerRequestAsync(Admin, request.Id, None);
        await AskAsync(Harness.Dave);
        Assert.HasCount(4, (await _h.OutboxAsync()).Where(o => o.To is "admin@example.com" or "second@example.com"));
    }

    [TestMethod]
    public async Task ABadNoteIsRefusedAndNothingIsSaved()
    {
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => AskAsync(Harness.Bob, "Deals at https://spam.example"));

        Assert.IsEmpty(await Service.ListOrganizerRequestsAsync(Admin, None));
        Assert.IsEmpty(await _h.OutboxAsync());
    }

    [TestMethod]
    public async Task ConfiguredNoteTermsAreRefused()
    {
        var h = await Harness.CreateAsync(o => o.BlockedNoteTerms = ["crypto giveaway"]);

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => h.AdminService.RequestToOrganizeAsync(Harness.Bob, null, "Crypto Giveaway!", None));
    }

    [TestMethod]
    public async Task TheNoteIsStoredCleaned()
    {
        var request = await AskAsync(Harness.Bob, "  Contoso\r\n  PM  ");

        Assert.AreEqual("Contoso PM", request.Note);
    }

    [TestMethod]
    public async Task SomeoneWhoCanAlreadyOrganizeCantAsk()
    {
        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => AskAsync(Harness.Rocky));
        StringAssert.Contains(ex.Message, "already");
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => AskAsync(Harness.Caller("anyone@marimer.llc")));
    }

    [TestMethod]
    public async Task AVotingLinkCantAsk()
    {
        var byLink = new CallerIdentity(null, ["bob@example.com"], Channel.Web, MagicLinkEventId: Guid.NewGuid());

        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => AskAsync(byLink));
    }

    [TestMethod]
    public async Task TheAddressMustBeOneOfTheCallersOwn()
    {
        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => Service.RequestToOrganizeAsync(Harness.Bob, "mallory@example.com", null, None));
        StringAssert.Contains(ex.Message, "isn't one of your verified addresses");

        var bothAddresses = new CallerIdentity(null, ["bob@example.com", "bob@work.example"], Channel.Web);
        var request = await Service.RequestToOrganizeAsync(bothAddresses, "Bob@Work.example", null, None);
        Assert.AreEqual("bob@work.example", request.Email);
    }

    [TestMethod]
    public async Task NewRequestsAreRefusedWhileTooManyWait()
    {
        var h = await Harness.CreateAsync(o => o.MaxPendingOrganizerRequests = 2);
        await h.AdminService.RequestToOrganizeAsync(Harness.Bob, null, null, None);
        await h.AdminService.RequestToOrganizeAsync(Harness.Carol, null, null, None);

        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => h.AdminService.RequestToOrganizeAsync(Harness.Dave, null, null, None));
        StringAssert.Contains(ex.Message, "Too many requests");
    }

    [TestMethod]
    public async Task OnlyAdminsCanSeeOrDecideRequests()
    {
        var request = await AskAsync(Harness.Bob);

        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.ListOrganizerRequestsAsync(Harness.Rocky, None));
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.ApproveOrganizerRequestAsync(Harness.Bob, request.Id, None));
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.DeclineOrganizerRequestAsync(Harness.Rocky, request.Id, None));
        Assert.HasCount(1, await Service.ListOrganizerRequestsAsync(Admin, None));
    }

    [TestMethod]
    public async Task ARequestIsDecidedOnce()
    {
        var request = await AskAsync(Harness.Bob);
        await Service.ApproveOrganizerRequestAsync(Admin, request.Id, None);

        var ex = await Assert.ThrowsExactlyAsync<SchedulingConflictException>(
            () => Service.DeclineOrganizerRequestAsync(Harness.Caller("second@example.com"), request.Id, None));
        StringAssert.Contains(ex.Message, "already approved by admin@example.com");

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => Service.ApproveOrganizerRequestAsync(Admin, Guid.NewGuid(), None));
    }

    [TestMethod]
    public async Task ApprovingWhenTheAddressIsAlreadyGrantedKeepsTheGrant()
    {
        var request = await AskAsync(Harness.Bob);
        await Service.AddOrganizerAsync(Admin, "bob@example.com", "Added by hand", None);

        await Service.ApproveOrganizerRequestAsync(Admin, request.Id, None);

        var grant = (await Service.ListOrganizersAsync(Admin, None)).Single(e => e.Pattern == "bob@example.com");
        Assert.AreEqual("Added by hand", grant.Note);
    }

    [TestMethod]
    public async Task RequestsAndDecisionsAreAuditedWithoutTheNote()
    {
        var request = await AskAsync(Harness.Bob, "Contoso PM");
        await Service.ApproveOrganizerRequestAsync(Admin, request.Id, None);

        var audit = await _h.AuditAsync();
        CollectionAssert.AreEqual(
            new[] { "organizer.requested", "organizer.added", "organizer.request.approved" },
            audit.Select(a => a.Action).ToArray());
        Assert.AreEqual("bob@example.com", audit[0].Actor);
        Assert.IsTrue(audit.Skip(1).All(a => a.Actor == "admin@example.com"));
        Assert.IsTrue(audit.All(a => a.EventId is null && !a.Detail.Contains("Contoso PM")));
    }

    [TestMethod]
    public async Task ThePurgeDeletesDecidedRequestsOnceTheCooldownEnds()
    {
        var waiting = await AskAsync(Harness.Bob);
        var declined = await AskAsync(Harness.Carol);
        await Service.DeclineOrganizerRequestAsync(Admin, declined.Id, None);

        _h.Clock.Advance(NodaTime.Duration.FromDays(30) + NodaTime.Duration.FromMinutes(1));
        await _h.Jobs.PurgeAsync(None);

        await using var db = await _h.Db.CreateDbContextAsync();
        CollectionAssert.AreEqual(new[] { waiting.Id }, await db.OrganizerRequests.Select(r => r.Id).ToArrayAsync());
    }
}
