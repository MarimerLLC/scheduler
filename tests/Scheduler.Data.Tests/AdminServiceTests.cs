using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

[TestClass]
public sealed class AdminServiceTests
{
    private Harness _h = null!;
    private static readonly CallerIdentity Admin = Harness.Admin;
    private static readonly CancellationToken None = CancellationToken.None;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    private AdminService Service => _h.AdminService;

    [TestMethod]
    public async Task TheListShowsConfiguredEntriesThenGrants()
    {
        await Service.AddOrganizerAsync(Admin, "Zed@Partner.example", "Partner PM", None);

        var list = await Service.ListOrganizersAsync(Admin, None);

        CollectionAssert.AreEqual(
            new[] { "*@marimer.llc", "rocky@example.com", "zed@partner.example" },
            list.Select(e => e.Pattern).ToArray());
        Assert.AreEqual(OrganizerSource.Configuration, list[0].Source);
        Assert.IsTrue(list[0].IsDomain);
        var granted = list[2];
        Assert.AreEqual(OrganizerSource.Granted, granted.Source);
        Assert.AreEqual("Partner PM", granted.Note);
        Assert.AreEqual("admin@example.com", granted.GrantedBy);
        Assert.AreEqual("Fri Oct 16, 3:00 PM UTC (UTC+00:00)", granted.GrantedAt);
    }

    [TestMethod]
    public async Task OnlyAdminsCanManageOrganizers()
    {
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.ListOrganizersAsync(Harness.Rocky, None));
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.AddOrganizerAsync(Harness.Rocky, "bob@example.com", null, None));
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.RemoveOrganizerAsync(Harness.Rocky, "bob@example.com", None));

        var byLink = new CallerIdentity(null, ["admin@example.com"], Channel.Web, MagicLinkEventId: Guid.NewGuid());
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => Service.ListOrganizersAsync(byLink, None));
    }

    [TestMethod]
    public async Task AGrantedAddressCanCreateEvents()
    {
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(
            () => _h.Service.CreateEventAsync(Harness.Bob, Harness.Request(participants: [new("carol@example.com")]), None));

        await Service.AddOrganizerAsync(Admin, "bob@example.com", null, None);
        var ev = await _h.Service.CreateEventAsync(Harness.Bob, Harness.Request(participants: [new("carol@example.com")]), None);

        Assert.IsTrue(ev.IsOrganizer);
    }

    [TestMethod]
    public async Task AGrantedDomainCoversEveryoneThere()
    {
        await Service.AddOrganizerAsync(Admin, "*@partner.example", null, None);

        var ev = await _h.Service.CreateEventAsync(Harness.Caller("anyone@partner.example"), Harness.Request(), None);

        Assert.IsTrue(ev.IsOrganizer);
        Assert.IsTrue((await Service.GetAccessAsync(Harness.Caller("someone@partner.example"), None)).CanOrganize);
        Assert.IsFalse((await Service.GetAccessAsync(Harness.Caller("someone@notpartner.example"), None)).CanOrganize);
    }

    [TestMethod]
    public async Task RemovingAnOrganizerStopsNewEventsButKeepsTheirExistingOnes()
    {
        await Service.AddOrganizerAsync(Admin, "bob@example.com", null, None);
        var ev = await _h.Service.CreateEventAsync(Harness.Bob, Harness.Request(participants: [new("carol@example.com")]), None);

        await Service.RemoveOrganizerAsync(Admin, "BOB@example.com", None);

        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(
            () => _h.Service.CreateEventAsync(Harness.Bob, Harness.Request(participants: [new("carol@example.com")]), None));
        var result = await _h.Service.FinalizeAsync(Harness.Bob, ev.ShortCode, ev.Slots[0].SlotId, ev.Version, None);
        Assert.AreEqual(EventState.Finalized, result.Event.State);
    }

    [TestMethod]
    public async Task ConfiguredEntriesCantBeRemovedHere()
    {
        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => Service.RemoveOrganizerAsync(Admin, "*@marimer.llc", None));

        StringAssert.Contains(ex.Message, "Scheduler:Organizers");
    }

    [TestMethod]
    public async Task RemovingSomethingNotOnTheListSaysSo()
    {
        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => Service.RemoveOrganizerAsync(Admin, "nobody@example.com", None));

        StringAssert.Contains(ex.Message, "isn't on the organizer list");
    }

    [TestMethod]
    public async Task AddingTwiceKeepsTheOriginalGrant()
    {
        var first = await Service.AddOrganizerAsync(Admin, "bob@example.com", "first", None);
        _h.Clock.AdvanceMinutes(5);
        var second = await Service.AddOrganizerAsync(Admin, "Bob@Example.com", "second", None);

        Assert.AreEqual(first, second);
        Assert.HasCount(1, (await _h.AuditAsync()).Where(a => a.Action == "organizer.added"));
    }

    [TestMethod]
    public async Task AWildcardCantBeGranted()
    {
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => Service.AddOrganizerAsync(Admin, "*", null, None));
    }

    [TestMethod]
    public async Task AdminChangesAreAudited()
    {
        await Service.AddOrganizerAsync(Admin, "*@partner.example", "Partner team", None);
        await Service.RemoveOrganizerAsync(Admin, "*@partner.example", None);

        var audit = await _h.AuditAsync();
        CollectionAssert.AreEqual(new[] { "organizer.added", "organizer.removed" }, audit.Select(a => a.Action).ToArray());
        Assert.IsTrue(audit.All(a => a.EventId is null && a.Actor == "admin@example.com"));
        StringAssert.Contains(audit[0].Detail, "*@partner.example");
    }

    [TestMethod]
    public async Task AccessReflectsEveryRole()
    {
        var rocky = await Service.GetAccessAsync(Harness.Rocky, None);
        var admin = await Service.GetAccessAsync(Admin, None);
        var bob = await Service.GetAccessAsync(Harness.Bob, None);

        Assert.AreEqual((true, false), (rocky.CanOrganize, rocky.IsAdmin));
        Assert.AreEqual((false, true), (admin.CanOrganize, admin.IsAdmin), "being an admin doesn't make you an organizer");
        Assert.AreEqual((false, false), (bob.CanOrganize, bob.IsAdmin));
        CollectionAssert.AreEqual(new[] { "bob@example.com" }, bob.Emails.ToArray());
    }
}
