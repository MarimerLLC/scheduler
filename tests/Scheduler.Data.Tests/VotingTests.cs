using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

[TestClass]
public sealed class VotingTests
{
    private Harness _h = null!;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    [TestMethod]
    public async Task VotingAgainReplacesTheEarlierAnswer()
    {
        var ev = await _h.CreateAsync();

        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Preferred));
        var view = await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.No));

        Assert.AreEqual(VoteValue.No, view.Slots[0].MyVote);
        Assert.AreEqual(new SlotTally(0, 0, 0, 1, 2), view.Slots[0].Tally);
        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.AreEqual(1, await db.Votes.CountAsync());
    }

    [TestMethod]
    public async Task AYesVoteIsStoredAndTallied()
    {
        var ev = await _h.CreateAsync();

        var view = await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Yes));

        Assert.AreEqual(VoteValue.Yes, view.Slots[0].MyVote);
        Assert.AreEqual(new SlotTally(0, 1, 0, 0, 2), view.Slots[0].Tally);
        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.AreEqual(VoteValue.Yes, (await db.Votes.SingleAsync()).Value);
    }

    [TestMethod]
    public async Task AnEventLeavesNeedsMyVoteOnceEverySlotIsAnswered()
    {
        var ev = await _h.CreateAsync();

        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Preferred));
        var partway = await _h.Service.ListMyEventsAsync(Harness.Bob, EventListFilter.NeedsMyVote, CancellationToken.None);
        var view = await _h.VoteAsync(Harness.Bob, ev, (1, VoteValue.IfNecessary), (2, VoteValue.No));
        var done = await _h.Service.ListMyEventsAsync(Harness.Bob, EventListFilter.NeedsMyVote, CancellationToken.None);

        Assert.IsTrue(partway.Single().NeedsMyVote);
        Assert.IsEmpty(done);
        Assert.IsTrue(view.Participants.Single(p => p.Email == "bob@example.com").Responded);
    }

    [TestMethod]
    public async Task TheOrganizerHearsOnceWhenEveryRequiredPersonHasAnswered()
    {
        var ev = await _h.CreateAsync();
        var all = new[] { (0, VoteValue.Preferred), (1, VoteValue.Preferred), (2, VoteValue.No) };

        await _h.VoteAsync(Harness.Bob, ev, all);
        Assert.IsFalse((await _h.OutboxAsync()).Any(o => o.To == "rocky@example.com"));

        await _h.VoteAsync(Harness.Carol, ev, all);
        await _h.VoteAsync(Harness.Dave, ev, all);       // optional; doesn't trigger a second email
        await _h.VoteAsync(Harness.Carol, ev, (0, VoteValue.No));

        var toRocky = (await _h.OutboxAsync()).Where(o => o.To == "rocky@example.com").ToList();
        Assert.HasCount(1, toRocky);
        StringAssert.Contains(toRocky[0].Subject, "Everyone has responded");
        Assert.IsNull(toRocky[0].ReplyTo);
        StringAssert.Contains(toRocky[0].TextBody, $"{Harness.BaseUrl}/events/{ev.Id}");
    }

    [TestMethod]
    public async Task TheOrganizerHearsRightAwayWhenARequiredPersonCantMakeAnyTime()
    {
        var ev = await _h.CreateAsync();

        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.No), (1, VoteValue.No));
        Assert.IsEmpty(CantMakeIt(await _h.OutboxAsync()), "a time is still unanswered");

        await _h.VoteAsync(Harness.Bob, ev, (2, VoteValue.No));

        var toRocky = CantMakeIt(await _h.OutboxAsync());
        Assert.HasCount(1, toRocky);
        Assert.AreEqual("rocky@example.com", toRocky[0].To);
        Assert.AreEqual($"[sched:{ev.ShortCode}] Bob can't make any time: Planning", toRocky[0].Subject);
        Assert.IsNull(toRocky[0].ReplyTo);
        StringAssert.Contains(toRocky[0].TextBody, $"{Harness.BaseUrl}/events/{ev.Id}");
    }

    [TestMethod]
    public async Task SayingNoToEveryTimeAgainDoesntEmailTheOrganizerAgain()
    {
        var ev = await _h.CreateAsync();
        var allNo = new[] { (0, VoteValue.No), (1, VoteValue.No), (2, VoteValue.No) };

        await _h.VoteAsync(Harness.Bob, ev, allNo);
        await _h.VoteAsync(Harness.Bob, ev, allNo);

        Assert.HasCount(1, CantMakeIt(await _h.OutboxAsync()));
    }

    [TestMethod]
    public async Task SayingNoToNewTimesTooEmailsTheOrganizerAgain()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.No), (1, VoteValue.No), (2, VoteValue.No));

        var more = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode,
            [new("2026-11-06T14:00", "America/Chicago")], null, ev.Version, CancellationToken.None);
        await _h.VoteAsync(Harness.Bob, more, (3, VoteValue.No));

        Assert.HasCount(2, CantMakeIt(await _h.OutboxAsync()));
    }

    [TestMethod]
    public async Task OptionalPeopleAndAnyWorkableTimeDontTriggerTheWarning()
    {
        var ev = await _h.CreateAsync();

        await _h.VoteAsync(Harness.Dave, ev, (0, VoteValue.No), (1, VoteValue.No), (2, VoteValue.No));
        await _h.VoteAsync(Harness.Carol, ev, (0, VoteValue.No), (1, VoteValue.IfNecessary), (2, VoteValue.No));

        Assert.IsEmpty(CantMakeIt(await _h.OutboxAsync()));
    }

    private static List<OutboxEmail> CantMakeIt(IEnumerable<OutboxEmail> outbox) =>
        outbox.Where(o => o.Subject.Contains("can't make any time", StringComparison.Ordinal)).ToList();

    [TestMethod]
    public async Task AVoterCanAskForACopyOfTheirAnswers()
    {
        var ev = await _h.CreateAsync();

        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Yes));
        Assert.IsFalse((await _h.OutboxAsync()).Any(o => o.To == "bob@example.com" && o.Subject.Contains("Your answers", StringComparison.Ordinal)),
            "no copy unless asked");

        await _h.Service.VoteAsync(Harness.Bob, ev.ShortCode,
            [new(ev.Slots[0].SlotId, VoteValue.Preferred), new(ev.Slots[2].SlotId, VoteValue.IfNecessary)],
            "Mornings are hard", emailReceipt: true, CancellationToken.None);

        var receipt = (await _h.OutboxAsync()).Single(o => o.Subject.Contains("Your answers", StringComparison.Ordinal));
        Assert.AreEqual("bob@example.com", receipt.To);
        Assert.AreEqual($"[sched:{ev.ShortCode}] Your answers: Planning", receipt.Subject);
        Assert.AreEqual("rocky@example.com", receipt.ReplyTo);
        StringAssert.Contains(receipt.TextBody, $"{ev.Slots[0].OrganizerLocal}: Preferred");
        StringAssert.Contains(receipt.TextBody, $"{ev.Slots[1].OrganizerLocal}: Not answered");
        StringAssert.Contains(receipt.TextBody, $"{ev.Slots[2].OrganizerLocal}: If necessary");
        StringAssert.Contains(receipt.TextBody, "  > Mornings are hard");
        StringAssert.Contains(receipt.TextBody, $"{Harness.BaseUrl}/e/{ev.ShortCode}?t=");
        var audit = (await _h.AuditAsync()).Last(a => a.Action == "vote.cast");
        Assert.IsTrue(JsonDocument.Parse(audit.Detail!).RootElement.GetProperty("receipt").GetBoolean());
    }

    [TestMethod]
    public async Task UnknownAndRepeatedSlotsAreRejected()
    {
        var ev = await _h.CreateAsync();

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => _h.Service.VoteAsync(
            Harness.Bob, ev.ShortCode, [new(Guid.NewGuid(), VoteValue.Preferred)], null, emailReceipt: false, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => _h.VoteAsync(
            Harness.Bob, ev, (0, VoteValue.Preferred), (0, VoteValue.No)));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => _h.Service.VoteAsync(
            Harness.Bob, ev.ShortCode, [], null, emailReceipt: false, CancellationToken.None));
    }

    [TestMethod]
    public async Task TheOrganizerDoesntVote()
    {
        var ev = await _h.CreateAsync();

        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => _h.VoteAsync(Harness.Rocky, ev, (0, VoteValue.Preferred)));

        StringAssert.Contains(ex.Message, "organizers don't vote");
    }

    [TestMethod]
    public async Task StrangersCantVote()
    {
        var ev = await _h.CreateAsync();

        await Assert.ThrowsExactlyAsync<SchedulingAccessException>(() => _h.VoteAsync(Harness.Mallory, ev, (0, VoteValue.Preferred)));
    }

    [TestMethod]
    public async Task ANoteCanBeSetKeptAndCleared()
    {
        var ev = await _h.CreateAsync();
        var slot = ev.Slots[0].SlotId;

        await _h.Service.VoteAsync(Harness.Bob, ev.ShortCode, [new(slot, VoteValue.IfNecessary)], "Mornings are hard", emailReceipt: false, CancellationToken.None);
        var kept = await _h.Service.VoteAsync(Harness.Bob, ev.ShortCode, [new(slot, VoteValue.No)], null, emailReceipt: false, CancellationToken.None);
        var cleared = await _h.Service.VoteAsync(Harness.Bob, ev.ShortCode, [new(slot, VoteValue.No)], "", emailReceipt: false, CancellationToken.None);

        Assert.AreEqual("Mornings are hard", kept.Participants.Single(p => p.Email == "bob@example.com").Note);
        Assert.IsNull(cleared.Participants.Single(p => p.Email == "bob@example.com").Note);
    }

    [TestMethod]
    public async Task VotesRecordTheChannel()
    {
        var ev = await _h.CreateAsync();

        await _h.VoteAsync(new CallerIdentity(null, ["bob@example.com"], Channel.Web, ev.Id), ev, (0, VoteValue.Preferred));

        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.AreEqual(Channel.Web, (await db.Votes.SingleAsync()).Channel);
        Assert.AreEqual(Channel.Web, (await db.Audit.SingleAsync(a => a.Action == "vote.cast")).Channel);
    }

    [TestMethod]
    public async Task AVoteDoesntChangeTheVersionTheOrganizerActsOn()
    {
        var ev = await _h.CreateAsync();

        await _h.VoteAsync(Harness.Bob, ev, (1, VoteValue.Preferred));

        // Finalizing with the version from before the vote still works.
        var result = await _h.Service.FinalizeAsync(Harness.Rocky, ev.ShortCode, ev.Slots[1].SlotId, ev.Version, CancellationToken.None);
        Assert.AreEqual(EventState.Finalized, result.Event.State);
    }
}
