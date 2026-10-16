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
        Assert.AreEqual(new SlotTally(0, 0, 1, 2), view.Slots[0].Tally);
        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.AreEqual(1, await db.Votes.CountAsync());
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
    public async Task UnknownAndRepeatedSlotsAreRejected()
    {
        var ev = await _h.CreateAsync();

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => _h.Service.VoteAsync(
            Harness.Bob, ev.ShortCode, [new(Guid.NewGuid(), VoteValue.Preferred)], null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => _h.VoteAsync(
            Harness.Bob, ev, (0, VoteValue.Preferred), (0, VoteValue.No)));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => _h.Service.VoteAsync(
            Harness.Bob, ev.ShortCode, [], null, CancellationToken.None));
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

        await _h.Service.VoteAsync(Harness.Bob, ev.ShortCode, [new(slot, VoteValue.IfNecessary)], "Mornings are hard", CancellationToken.None);
        var kept = await _h.Service.VoteAsync(Harness.Bob, ev.ShortCode, [new(slot, VoteValue.No)], null, CancellationToken.None);
        var cleared = await _h.Service.VoteAsync(Harness.Bob, ev.ShortCode, [new(slot, VoteValue.No)], "", CancellationToken.None);

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
