using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Model;

namespace Scheduler.Core.Tests;

[TestClass]
public sealed class EventStateTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 20, 15, 0);

    [TestMethod]
    public void NewEventIsDraftAndCannotTakeVotes()
    {
        var ev = TestEvents.WithSlots(1);

        Assert.AreEqual(EventState.Draft, ev.State);
        Assert.ThrowsExactly<SchedulingConflictException>(ev.EnsureAcceptingVotes);
    }

    [TestMethod]
    public void FinalizeRecordsSlotAndCloses()
    {
        var ev = TestEvents.WithSlots(2);
        ev.Open();

        ev.Finalize(ev.Slots[1].Id, Now);

        Assert.AreEqual(EventState.Finalized, ev.State);
        Assert.AreEqual(ev.Slots[1].Id, ev.FinalizedSlotId);
        Assert.AreEqual(Now, ev.ClosedAt);
    }

    [TestMethod]
    public void CannotFinalizeAWithdrawnSlot()
    {
        var ev = TestEvents.WithSlots(1);
        ev.Open();
        ev.Slots[0].WithdrawnAt = Now;

        Assert.ThrowsExactly<SchedulingValidationException>(() => ev.Finalize(ev.Slots[0].Id, Now));
        Assert.AreEqual(EventState.Open, ev.State);
    }

    [TestMethod]
    public void CannotFinalizeADraft()
    {
        var ev = TestEvents.WithSlots(1);

        Assert.ThrowsExactly<SchedulingConflictException>(() => ev.Finalize(ev.Slots[0].Id, Now));
    }

    [TestMethod]
    public void ReopenClearsTheDecisionForRescheduling()
    {
        var ev = TestEvents.WithSlots(1);
        ev.Open();
        ev.Finalize(ev.Slots[0].Id, Now);

        ev.Reopen();

        Assert.AreEqual(EventState.Open, ev.State);
        Assert.IsNull(ev.FinalizedSlotId);
        Assert.IsNull(ev.ClosedAt);
    }

    [TestMethod]
    public void CancelledIsTerminal()
    {
        var ev = TestEvents.WithSlots(1);
        ev.Open();
        ev.Cancel(Now);

        foreach (var state in Enum.GetValues<EventState>())
            Assert.IsFalse(SchedulingEvent.CanTransition(EventState.Cancelled, state), $"cancelled -> {state}");
    }

    [TestMethod]
    public void ExpiredCanReopenWithMoreSlots()
    {
        var ev = TestEvents.WithSlots(1);
        ev.Open();
        ev.Expire(Now);

        ev.Reopen();

        Assert.AreEqual(EventState.Open, ev.State);
    }
}
