using Microsoft.EntityFrameworkCore;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

[TestClass]
public sealed class CreateEventTests
{
    private Harness _h = null!;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    [TestMethod]
    public async Task CreatesAnOpenEventWithResolvedSlots()
    {
        var ev = await _h.CreateAsync();

        Assert.AreEqual(EventState.Open, ev.State);
        Assert.IsTrue(ev.IsOrganizer);
        Assert.AreEqual("rocky@example.com", ev.OrganizerEmail);
        Assert.AreEqual("Rocky Lhotka", ev.OrganizerName);
        Assert.HasCount(3, ev.Slots);
        Assert.AreEqual("2026-11-03T16:00:00Z", ev.Slots[0].StartUtc);
        Assert.AreEqual("Tue Nov 3, 10:00 AM CST (UTC-06:00)", ev.Slots[0].OrganizerLocal);
        Assert.AreEqual("Fri Oct 30, 5:00 PM CDT (UTC-05:00)", ev.RespondBy);
        CollectionAssert.AreEqual(
            new[] { "bob@example.com", "carol@example.com", "dave@example.com" },
            ev.Participants.Select(p => p.Email).ToArray());
    }

    [TestMethod]
    public async Task QueuesOnePersonalInvitationPerParticipant()
    {
        var ev = await _h.CreateAsync();

        var outbox = await _h.OutboxAsync();
        CollectionAssert.AreEquivalent(
            new[] { "bob@example.com", "carol@example.com", "dave@example.com" },
            outbox.Select(o => o.To).ToArray());
        foreach (var email in outbox)
        {
            Assert.StartsWith($"[sched:{ev.ShortCode}] Rocky Lhotka asks: Planning", email.Subject);
            Assert.AreEqual("rocky@example.com", email.ReplyTo);
            Assert.AreEqual(OutboxStatus.Pending, email.Status);
            StringAssert.Contains(email.TextBody, $"{Harness.BaseUrl}/e/{ev.ShortCode}?t=");
        }

        // Each link is personal: three different tokens.
        Assert.HasCount(3, outbox.Select(o => o.TextBody[o.TextBody.IndexOf("?t=", StringComparison.Ordinal)..].Split('\n')[0]).Distinct().ToList());
    }

    [TestMethod]
    public async Task VotingLinkInTheInvitationIsTheParticipantsToken()
    {
        var ev = await _h.CreateAsync();

        await using var db = await _h.Db.CreateDbContextAsync();
        var bob = await db.Participants.SingleAsync(p => p.Email == "bob@example.com");
        var invitation = (await _h.OutboxAsync()).Single(o => o.To == "bob@example.com");

        StringAssert.Contains(invitation.TextBody, $"?t={_h.Links.CreateToken(bob.Id, bob.TokenNonce)}");
    }

    [TestMethod]
    public async Task WritesAnAuditEntryAndProvisionsTheOrganizersAccount()
    {
        var ev = await _h.CreateAsync();

        var audit = (await _h.AuditAsync()).Single();
        Assert.AreEqual("event.created", audit.Action);
        Assert.AreEqual("rocky@example.com", audit.Actor);
        Assert.AreEqual(ev.Id, audit.EventId);

        await using var db = await _h.Db.CreateDbContextAsync();
        var owner = await db.UserEmails.SingleAsync();
        Assert.AreEqual("rocky@example.com", owner.Email);
    }

    [TestMethod]
    public async Task DomainAllowlistGrantsOrganizing()
    {
        var ev = await _h.Service.CreateEventAsync(Harness.Caller("anne@marimer.llc"), Harness.Request(), CancellationToken.None);

        Assert.IsTrue(ev.IsOrganizer);
    }

    [TestMethod]
    public async Task CallersNotOnTheAllowlistCannotCreate()
    {
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(
            () => _h.Service.CreateEventAsync(Harness.Mallory, Harness.Request(), CancellationToken.None));

        Assert.IsEmpty(await _h.OutboxAsync());
    }

    [TestMethod]
    public async Task VotingLinkCallersCannotCreate()
    {
        var linkCaller = new CallerIdentity(null, ["rocky@example.com"], Channel.Web, MagicLinkEventId: Guid.NewGuid());

        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(
            () => _h.Service.CreateEventAsync(linkCaller, Harness.Request(), CancellationToken.None));
    }

    [TestMethod]
    public async Task SlotsInThePastAreRejected()
    {
        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => _h.CreateAsync(Harness.Request(slots: [new("2026-10-15T10:00", "America/Chicago")], respondBy: null)));

        StringAssert.Contains(ex.Message, "in the past");
    }

    [TestMethod]
    public async Task TheSameInstantInTwoZonesIsADuplicate()
    {
        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => _h.CreateAsync(Harness.Request(
                slots: [new("2026-11-03T10:00", "America/Chicago"), new("2026-11-03T11:00", "America/New_York")],
                respondBy: null)));

        StringAssert.Contains(ex.Message, "more than once");
    }

    [TestMethod]
    public async Task AWrongOffsetIsRejected()
    {
        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => _h.CreateAsync(Harness.Request(slots: [new("2026-11-03T10:00-05:00", "America/Chicago")], respondBy: null)));

        StringAssert.Contains(ex.Message, "UTC-06:00, not UTC-05:00");
    }

    [TestMethod]
    public async Task TheOrganizerAndDuplicateAddressesAreDroppedWithNotices()
    {
        var ev = await _h.CreateAsync(Harness.Request(participants:
        [
            new("Rocky@Example.com"),
            new("bob@example.com", Role: AttendeeRole.Optional),
            new("BOB@example.com", "Bob"),
        ]));

        var bob = ev.Participants.Single();
        Assert.AreEqual("bob@example.com", bob.Email);
        Assert.AreEqual(AttendeeRole.Required, bob.Role);
        Assert.AreEqual("Bob", bob.DisplayName);
        Assert.HasCount(2, ev.Notices!);
    }

    [TestMethod]
    public async Task InvitingOnlyYourselfIsRejected()
    {
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => _h.CreateAsync(Harness.Request(participants: [new("rocky@example.com")])));
    }

    [TestMethod]
    public async Task ADeadlineAfterTheLastSlotIsRejected()
    {
        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => _h.CreateAsync(Harness.Request(respondBy: "2026-11-06T09:00")));

        StringAssert.Contains(ex.Message, "before the last proposed time");
    }

    [TestMethod]
    public async Task OverlongTitlesAreRejected()
    {
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => _h.CreateAsync(Harness.Request(title: new string('x', FieldLimits.TitleMax + 1))));
    }

    [TestMethod]
    [DataRow("{0}")]
    [DataRow("[sched:{0}]")]
    [DataRow("sched:{0}")]
    public async Task EventsCanBeFoundByShortCode(string format)
    {
        var ev = await _h.CreateAsync();

        var found = await _h.Service.GetEventAsync(Harness.Bob, string.Format(format, ev.ShortCode.ToLowerInvariant()), null, CancellationToken.None);

        Assert.AreEqual(ev.Id, found.Id);
    }
}
