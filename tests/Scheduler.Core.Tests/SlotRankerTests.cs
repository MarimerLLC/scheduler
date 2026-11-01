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
    public void IfNecessaryLosesToPreferred()
    {
        var ev = TestEvents.WithSlots(2);
        var (first, second) = (ev.Slots[0], ev.Slots[1]);
        ev.AddParticipant("bob@example.com").Votes(first, VoteValue.IfNecessary).Votes(second, VoteValue.Preferred);

        Assert.AreEqual(second.Id, SlotRanker.Rank(ev)[0].SlotId);
    }

    [TestMethod]
    public void YesIsNotAPenalty()
    {
        var ev = TestEvents.WithSlots(2);
        var (first, second) = (ev.Slots[0], ev.Slots[1]);
        ev.AddParticipant("bob@example.com").Votes(first, VoteValue.IfNecessary).Votes(second, VoteValue.Yes);
        ev.AddParticipant("carol@example.com").Votes(first, VoteValue.Preferred).Votes(second, VoteValue.Yes);
        ev.AddParticipant("erin@example.com").Votes(first, VoteValue.Preferred).Votes(second, VoteValue.Yes);

        var ranked = SlotRanker.Rank(ev);

        Assert.AreEqual(second.Id, ranked[0].SlotId);
        Assert.IsTrue(ranked[1].Viable);
    }

    [TestMethod]
    public void RequiredIfNecessaryRanksBelowOptionalNo()
    {
        var ev = TestEvents.WithSlots(2);
        var (first, second) = (ev.Slots[0], ev.Slots[1]);
        ev.AddParticipant("bob@example.com").Votes(first, VoteValue.IfNecessary).Votes(second, VoteValue.Preferred);
        ev.AddParticipant("dave@example.com", AttendeeRole.Optional).Votes(first, VoteValue.Preferred).Votes(second, VoteValue.No);

        var ranked = SlotRanker.Rank(ev);

        Assert.AreEqual(second.Id, ranked[0].SlotId);
        Assert.AreEqual(1, ranked[1].RequiredIfNecessary);
    }

    [TestMethod]
    public void OptionalNoRanksBelowOptionalIfNecessary()
    {
        var ev = TestEvents.WithSlots(2);
        var (first, second) = (ev.Slots[0], ev.Slots[1]);
        ev.AddParticipant("dave@example.com", AttendeeRole.Optional).Votes(first, VoteValue.No).Votes(second, VoteValue.IfNecessary);

        var ranked = SlotRanker.Rank(ev);

        Assert.AreEqual(second.Id, ranked[0].SlotId);
        Assert.AreEqual(1, ranked[0].OptionalIfNecessary);
        Assert.AreEqual(1, ranked[1].OptionalNo);
    }

    [TestMethod]
    public void MorePreferredWinsAmongSlotsWithoutIfNecessary()
    {
        var ev = TestEvents.WithSlots(2);
        var (first, second) = (ev.Slots[0], ev.Slots[1]);
        ev.AddParticipant("bob@example.com").Votes(first, VoteValue.Yes).Votes(second, VoteValue.Preferred);
        ev.AddParticipant("carol@example.com").Votes(first, VoteValue.Yes).Votes(second, VoteValue.Yes);

        Assert.AreEqual(second.Id, SlotRanker.Rank(ev)[0].SlotId);
    }

    [TestMethod]
    public void YesIsCountedAndNotPending()
    {
        var ev = TestEvents.WithSlots(1);
        ev.AddParticipant("bob@example.com").Votes(ev.Slots[0], VoteValue.Yes);

        var score = SlotRanker.Rank(ev).Single();

        Assert.AreEqual(1, score.Yes);
        Assert.AreEqual(0, score.Preferred);
        Assert.AreEqual(0, score.Pending);
        Assert.IsTrue(score.Viable);
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
