using Microsoft.EntityFrameworkCore;
using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

[TestClass]
public sealed class OrganizerTests
{
    private Harness _h = null!;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    private static readonly SlotInput[] Friday = [new("2026-11-06T14:00", "America/Chicago")];

    [TestMethod]
    public async Task ProposingMoreSlotsAddsARoundAndEmailsEveryone()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Preferred), (1, VoteValue.Preferred), (2, VoteValue.Preferred));
        var before = (await _h.OutboxAsync()).Count;

        var view = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, Friday, null, ev.Version, CancellationToken.None);

        Assert.HasCount(4, view.Slots);
        Assert.AreNotEqual(ev.Version, view.Version);
        Assert.IsFalse(view.Participants.Single(p => p.Email == "bob@example.com").Responded, "Bob hasn't answered the new slot");

        var newTimes = (await _h.OutboxAsync()).Skip(before).ToList();
        Assert.HasCount(3, newTimes);
        Assert.IsTrue(newTimes.All(o => o.Subject.Contains("New times for: Planning", StringComparison.Ordinal)));
        Assert.IsTrue(newTimes.All(o => o.TextBody.Contains("Fri Nov 6, 2:00 PM CST", StringComparison.Ordinal)));

        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.AreEqual(2, await db.Slots.Where(s => s.EventId == ev.Id).MaxAsync(s => s.Round));
    }

    [TestMethod]
    public async Task AStaleVersionIsRejected()
    {
        var ev = await _h.CreateAsync();
        await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, Friday, null, ev.Version, CancellationToken.None);

        var ex = await Assert.ThrowsExactlyAsync<SchedulingConflictException>(
            () => _h.Service.WithdrawSlotAsync(Harness.Rocky, ev.ShortCode, ev.Slots[0].SlotId, ev.Version, CancellationToken.None));

        StringAssert.Contains(ex.Message, "changed since you last read it");
    }

    [TestMethod]
    public async Task ParticipantsCantDoOrganizerThings()
    {
        var ev = await _h.CreateAsync();

        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(
            () => _h.Service.FinalizeAsync(Harness.Bob, ev.ShortCode, ev.Slots[0].SlotId, ev.Version, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(
            () => _h.Service.CancelAsync(Harness.Bob, ev.ShortCode, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<SchedulingAccessException>(
            () => _h.Service.CancelAsync(Harness.Mallory, ev.ShortCode, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task WithdrawingASlotDeletesItsVotesAndCanCompleteAResponse()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Preferred), (1, VoteValue.Preferred), (2, VoteValue.No));
        await _h.VoteAsync(Harness.Carol, ev, (1, VoteValue.Preferred), (2, VoteValue.Preferred));

        var view = await _h.Service.WithdrawSlotAsync(Harness.Rocky, ev.ShortCode, ev.Slots[0].SlotId, ev.Version, CancellationToken.None);

        Assert.HasCount(2, view.Slots);
        Assert.IsTrue(view.Participants.Single(p => p.Email == "bob@example.com").Responded);
        Assert.IsTrue(view.Participants.Single(p => p.Email == "carol@example.com").Responded, "Carol had answered every remaining slot");
        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.IsFalse(await db.Votes.AnyAsync(v => v.SlotId == ev.Slots[0].SlotId));
    }

    [TestMethod]
    public async Task TheLastSlotCantBeWithdrawn()
    {
        var ev = await _h.CreateAsync(Harness.Request(slots: [new("2026-11-03T10:00", "America/Chicago")], respondBy: null));

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => _h.Service.WithdrawSlotAsync(Harness.Rocky, ev.ShortCode, ev.Slots[0].SlotId, ev.Version, CancellationToken.None));
    }

    [TestMethod]
    public async Task FinalizingReturnsWhatTheCalendarNeedsAndEmailsEveryone()
    {
        var ev = await _h.CreateAsync();
        var before = (await _h.OutboxAsync()).Count;

        var result = await _h.Service.FinalizeAsync(Harness.Rocky, ev.ShortCode, ev.Slots[1].SlotId, ev.Version, CancellationToken.None);

        Assert.AreEqual("2026-11-04T10:00", result.LocalStart);
        Assert.AreEqual("America/Chicago", result.TimeZone);
        Assert.AreEqual(30, result.DurationMinutes);
        CollectionAssert.AreEqual(new[] { "bob@example.com", "carol@example.com", "dave@example.com" }, result.AttendeeEmails.ToArray());
        Assert.AreEqual(ev.Slots[1].SlotId, result.Event.FinalizedSlotId);

        var set = (await _h.OutboxAsync()).Skip(before).ToList();
        Assert.HasCount(3, set);
        Assert.IsTrue(set.All(o => o.Subject.EndsWith("Set: Planning", StringComparison.Ordinal)));
        Assert.IsTrue(set.All(o => o.TextBody.Contains("Wed Nov 4, 10:00 AM CST (UTC-06:00), 30 minutes", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AFinalizedEventTakesNoVotesButRecordsTheCalendarEvent()
    {
        var ev = await _h.CreateAsync();
        await Assert.ThrowsExactlyAsync<SchedulingConflictException>(
            () => _h.Service.RecordCalendarEventAsync(Harness.Rocky, ev.ShortCode, "uid-1", CancellationToken.None));

        await _h.Service.FinalizeAsync(Harness.Rocky, ev.ShortCode, ev.Slots[0].SlotId, ev.Version, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<SchedulingConflictException>(() => _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Preferred)));
        await _h.Service.RecordCalendarEventAsync(Harness.Rocky, ev.ShortCode, "uid-1@calendar.example", CancellationToken.None);

        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.AreEqual("uid-1@calendar.example", (await db.Events.SingleAsync()).CalendarEventUid);
    }

    [TestMethod]
    public async Task ProposingMoreSlotsAfterFinalizingReschedules()
    {
        var ev = await _h.CreateAsync();
        var finalized = await _h.Service.FinalizeAsync(Harness.Rocky, ev.ShortCode, ev.Slots[0].SlotId, ev.Version, CancellationToken.None);

        var view = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, Friday, null, finalized.Event.Version, CancellationToken.None);

        Assert.AreEqual(EventState.Open, view.State);
        Assert.IsNull(view.FinalizedSlotId);
        var last = (await _h.OutboxAsync()).Last();
        StringAssert.Contains(last.TextBody, "needs to reschedule");
        Assert.AreEqual("event.rescheduling", (await _h.AuditAsync()).Last().Action);
    }

    [TestMethod]
    public async Task ReopeningAnExpiredEventClearsAPassedDeadline()
    {
        var ev = await _h.CreateAsync();
        _h.Clock.AdvanceDays(15); // past the Oct 30 deadline: now Oct 31
        await _h.WithDbAsync(async db =>
        {
            var stored = await db.Events.SingleAsync();
            stored.Expire(_h.Clock.GetCurrentInstant());
        });
        var expired = await _h.GetAsync(Harness.Rocky, ev);

        var view = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, Friday, null, expired.Version, CancellationToken.None);

        Assert.AreEqual(EventState.Open, view.State);
        Assert.IsNull(view.RespondBy);
        StringAssert.Contains(view.Notices!.Single(), "deadline had passed");
    }

    [TestMethod]
    public async Task ANewDeadlineCanBeSetWhenProposingMore()
    {
        var ev = await _h.CreateAsync();

        var view = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, Friday, "2026-11-02T12:00", ev.Version, CancellationToken.None);

        Assert.AreEqual("Mon Nov 2, 12:00 PM CST (UTC-06:00)", view.RespondBy);
    }

    [TestMethod]
    public async Task CancellingEmailsEveryoneWithTheReasonAndIsFinal()
    {
        var ev = await _h.CreateAsync();
        var before = (await _h.OutboxAsync()).Count;

        var view = await _h.Service.CancelAsync(Harness.Rocky, ev.ShortCode, "Project shelved", CancellationToken.None);

        Assert.AreEqual(EventState.Cancelled, view.State);
        var cancelled = (await _h.OutboxAsync()).Skip(before).ToList();
        Assert.HasCount(3, cancelled);
        Assert.IsTrue(cancelled.All(o => o.TextBody.Contains("\"Project shelved\"", StringComparison.Ordinal)));

        await Assert.ThrowsExactlyAsync<SchedulingConflictException>(
            () => _h.Service.FinalizeAsync(Harness.Rocky, ev.ShortCode, ev.Slots[0].SlotId, view.Version, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<SchedulingConflictException>(
            () => _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, Friday, null, view.Version, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<SchedulingConflictException>(
            () => _h.Service.CancelAsync(Harness.Rocky, ev.ShortCode, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task TheAuditLogTellsTheStory()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Preferred));
        var withdrawn = await _h.Service.WithdrawSlotAsync(Harness.Rocky, ev.ShortCode, ev.Slots[2].SlotId, ev.Version, CancellationToken.None);
        await _h.Service.FinalizeAsync(Harness.Rocky, ev.ShortCode, ev.Slots[0].SlotId, withdrawn.Version, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "event.created", "vote.cast", "slot.withdrawn", "event.finalized" },
            (await _h.AuditAsync()).Select(a => a.Action).ToArray());
    }

    [TestMethod]
    public async Task SlotTimesRoundTripThroughTheDatabase()
    {
        var ev = await _h.CreateAsync();

        await using var db = await _h.Db.CreateDbContextAsync();
        var slot = await db.Slots.OrderBy(s => s.Start).FirstAsync(s => s.EventId == ev.Id);
        var stored = await db.Events.SingleAsync();

        Assert.AreEqual(new LocalDateTime(2026, 11, 3, 10, 0), slot.LocalStart);
        Assert.AreEqual("America/Chicago", slot.TimeZoneId);
        Assert.AreEqual(Instant.FromUtc(2026, 11, 3, 16, 0), slot.Start);
        Assert.AreEqual(Duration.FromMinutes(30), stored.Duration);
    }
}
