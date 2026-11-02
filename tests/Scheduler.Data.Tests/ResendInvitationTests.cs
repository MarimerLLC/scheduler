using Microsoft.EntityFrameworkCore;
using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

[TestClass]
public sealed class ResendInvitationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private Harness _h = null!;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    private Task<ResendResult> ResendAsync(EventView ev, params string[] emails) =>
        _h.Service.ResendInvitationsAsync(Harness.Rocky, ev.ShortCode, emails.Length == 0 ? null : emails, None);

    private async Task<List<OutboxEmail>> SentSinceCreateAsync() => (await _h.OutboxAsync()).Skip(3).ToList();

    [TestMethod]
    public async Task ResendsToEveryoneWithTheSameVotingLinks()
    {
        var ev = await _h.CreateAsync();
        var first = await _h.OutboxAsync();
        _h.Clock.Advance(Duration.FromHours(2));

        var result = await ResendAsync(ev);

        CollectionAssert.AreEqual(new[] { "bob@example.com", "carol@example.com", "dave@example.com" }, result.Resent.ToArray());
        Assert.IsNull(result.Event.Notices);
        var resent = await SentSinceCreateAsync();
        Assert.HasCount(3, resent);
        foreach (var email in resent)
        {
            var original = first.Single(o => o.To == email.To);
            Assert.AreEqual(original.Subject, email.Subject);
            Assert.AreEqual(original.TextBody, email.TextBody);
            Assert.AreEqual("rocky@example.com", email.ReplyTo);
        }

        var audit = (await _h.AuditAsync()).Last();
        Assert.AreEqual("invitations.resent", audit.Action);
        StringAssert.Contains(audit.Detail, "carol@example.com");
        Assert.AreEqual(ev.Id, _h.Changes.Published.Last());
    }

    [TestMethod]
    public async Task ResendsOnlyToTheNamedParticipants()
    {
        var ev = await _h.CreateAsync();
        _h.Clock.Advance(Duration.FromHours(2));

        var result = await ResendAsync(ev, " Carol@Example.com ", "dave@example.com", "carol@example.com");

        CollectionAssert.AreEqual(new[] { "carol@example.com", "dave@example.com" }, result.Resent.ToArray());
        CollectionAssert.AreEquivalent(new[] { "carol@example.com", "dave@example.com" }, (await SentSinceCreateAsync()).Select(o => o.To).ToArray());
    }

    [TestMethod]
    public async Task ShowsTheCurrentTimes()
    {
        var ev = await _h.CreateAsync();
        ev = await _h.Service.WithdrawSlotAsync(Harness.Rocky, ev.ShortCode, ev.Slots[0].SlotId, ev.Version, None);
        _h.Clock.Advance(Duration.FromHours(2));

        await ResendAsync(ev, "bob@example.com");

        var body = (await SentSinceCreateAsync()).Single().TextBody;
        Assert.DoesNotContain("Tue Nov 3", body);
        StringAssert.Contains(body, "Wed Nov 4");
    }

    [TestMethod]
    public async Task WaitsTheCooldownAfterTheInvitationOrTheLastResend()
    {
        var ev = await _h.CreateAsync();
        _h.Clock.Advance(Duration.FromMinutes(30));

        var tooSoon = await ResendAsync(ev, "bob@example.com");
        Assert.IsEmpty(tooSoon.Resent);
        Assert.AreEqual("bob@example.com was sent the invitation less than 1 hour ago, so it wasn't sent again. Try again later.", tooSoon.Event.Notices!.Single());
        Assert.IsEmpty(await SentSinceCreateAsync());
        Assert.AreEqual("event.created", (await _h.AuditAsync()).Last().Action);

        _h.Clock.Advance(Duration.FromMinutes(31));
        Assert.HasCount(1, (await ResendAsync(ev, "bob@example.com")).Resent);

        _h.Clock.Advance(Duration.FromMinutes(59));
        var everyone = await ResendAsync(ev);
        CollectionAssert.AreEqual(new[] { "carol@example.com", "dave@example.com" }, everyone.Resent.ToArray());
        StringAssert.StartsWith(everyone.Event.Notices!.Single(), "bob@example.com was sent the invitation less than 1 hour ago");
    }

    [TestMethod]
    public async Task SkipsAddressesThatUnsubscribedOrBounced()
    {
        var ev = await _h.CreateAsync();
        await _h.WithDbAsync(db =>
        {
            db.Suppressions.Add(new EmailSuppression { Email = "bob@example.com", Reason = SuppressionReason.Unsubscribed, At = _h.Clock.GetCurrentInstant() });
            db.Suppressions.Add(new EmailSuppression { Email = "carol@example.com", Reason = SuppressionReason.Bounced, At = _h.Clock.GetCurrentInstant() });
            return Task.CompletedTask;
        });
        _h.Clock.Advance(Duration.FromHours(2));

        var result = await ResendAsync(ev);

        CollectionAssert.AreEqual(new[] { "dave@example.com" }, result.Resent.ToArray());
        Assert.HasCount(2, result.Event.Notices!);
        StringAssert.StartsWith(result.Event.Notices![0], "bob@example.com has asked this server not to email them");
        StringAssert.StartsWith(result.Event.Notices![1], "Email to carol@example.com has bounced before");
    }

    [TestMethod]
    public async Task RefusesAddressesThatArentInvited()
    {
        var ev = await _h.CreateAsync();
        _h.Clock.Advance(Duration.FromHours(2));

        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => ResendAsync(ev, "bob@example.com", "mallory@example.com"));

        Assert.AreEqual("mallory@example.com isn't invited to this event. Only people already invited can be sent the invitation again.", ex.Message);
        Assert.IsEmpty(await SentSinceCreateAsync());
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => ResendAsync(ev, "not an address"));
    }

    [TestMethod]
    public async Task OnlyTheOrganizerCanResend()
    {
        var ev = await _h.CreateAsync();
        _h.Clock.Advance(Duration.FromHours(2));

        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() =>
            _h.Service.ResendInvitationsAsync(Harness.Bob, ev.ShortCode, null, None));
        await Assert.ThrowsExactlyAsync<SchedulingAccessException>(() =>
            _h.Service.ResendInvitationsAsync(Harness.Mallory, ev.ShortCode, null, None));
        Assert.IsEmpty(await SentSinceCreateAsync());
    }

    [TestMethod]
    public async Task OnlyWhileTheEventIsOpen()
    {
        var ev = await _h.CreateAsync();
        _h.Clock.Advance(Duration.FromHours(2));
        await _h.Service.FinalizeAsync(Harness.Rocky, ev.ShortCode, ev.Slots[0].SlotId, ev.Version, None);
        var sent = (await _h.OutboxAsync()).Count;

        var ex = await Assert.ThrowsExactlyAsync<SchedulingConflictException>(() => ResendAsync(ev));

        StringAssert.StartsWith(ex.Message, "This event is finalized and isn't taking votes");
        Assert.HasCount(sent, await _h.OutboxAsync());
    }

    [TestMethod]
    public async Task DoesntCountTowardTheDailyInvitationLimit()
    {
        _h = await Harness.CreateAsync(options => options.MaxInvitationsPerDay = 3);
        var ev = await _h.CreateAsync();
        _h.Clock.Advance(Duration.FromHours(2));

        Assert.HasCount(3, (await ResendAsync(ev)).Resent);

        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.IsTrue(await db.Participants.AllAsync(p => p.InvitationResentAt == _h.Clock.GetCurrentInstant()));
    }
}
