using NodaTime;
using Scheduler.Core.Model;
using Scheduler.Core.Ranking;

namespace Scheduler.Core.Tests;

[TestClass]
public sealed class SlotRankerTests
{
    [TestMethod]
    public void RequiredNoRanksBelowEverythingElse()
    {
        var ev = TestEvents.WithSlots(2);
        var (first, second) = (ev.Slots[0], ev.Slots[1]);
        ev.AddParticipant("bob@example.com").Votes(first, VoteValue.No).Votes(second, VoteValue.IfNecessary);
        ev.AddParticipant("carol@example.com").Votes(first, VoteValue.Preferred).Votes(second, VoteValue.IfNecessary);

        var ranked = SlotRanker.Rank(ev);

        Assert.AreEqual(second.Id, ranked[0].SlotId);
        Assert.IsTrue(ranked[0].Viable);
        Assert.IsFalse(ranked[1].Viable);
    }

    [TestMethod]
    public void OptionalNoDoesNotMakeASlotUnviable()
    {
        var ev = TestEvents.WithSlots(1);
        ev.AddParticipant("dave@example.com", AttendeeRole.Optional).Votes(ev.Slots[0], VoteValue.No);

        var score = SlotRanker.Rank(ev).Single();

        Assert.IsTrue(score.Viable);
        Assert.AreEqual(1, score.No);
        Assert.AreEqual(0, score.RequiredNo);
    }

    [TestMethod]
    public void MorePreferredWinsWhenNoCountsTie()
    {
        var ev = TestEvents.WithSlots(2);
        var (first, second) = (ev.Slots[0], ev.Slots[1]);
        ev.AddParticipant("bob@example.com").Votes(first, VoteValue.IfNecessary).Votes(second, VoteValue.Preferred);

        Assert.AreEqual(second.Id, SlotRanker.Rank(ev)[0].SlotId);
    }

    [TestMethod]
    public void EarlierSlotWinsAFullTie()
    {
        var ev = TestEvents.WithSlots(3);

        CollectionAssert.AreEqual(ev.Slots.Select(s => s.Id).ToList(), SlotRanker.Rank(ev).Select(s => s.SlotId).ToList());
    }

    [TestMethod]
    public void PendingCountsParticipantsWhoHaventVoted()
    {
        var ev = TestEvents.WithSlots(1);
        ev.AddParticipant("bob@example.com").Votes(ev.Slots[0], VoteValue.Preferred);
        ev.AddParticipant("carol@example.com");

        var score = SlotRanker.Rank(ev).Single();

        Assert.AreEqual(1, score.Preferred);
        Assert.AreEqual(1, score.Pending);
    }

    [TestMethod]
    public void WithdrawnSlotsAreLeftOut()
    {
        var ev = TestEvents.WithSlots(2);
        ev.Slots[0].WithdrawnAt = Instant.FromUtc(2026, 10, 20, 0, 0);

        Assert.AreEqual(ev.Slots[1].Id, SlotRanker.Rank(ev).Single().SlotId);
    }
}
