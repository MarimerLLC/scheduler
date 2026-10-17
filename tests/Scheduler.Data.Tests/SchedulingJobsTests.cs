using Microsoft.EntityFrameworkCore;
using NodaTime;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

/// <summary>
/// The background jobs, one pass at a time. The harness's standard event has a deadline of Fri Oct 30 17:00 CDT
/// (22:00 UTC); the clock starts at Fri Oct 16 15:00 UTC.
/// </summary>
[TestClass]
public sealed class SchedulingJobsTests
{
    private static readonly Instant Deadline = Instant.FromUtc(2026, 10, 30, 22, 0);
    private static readonly CancellationToken None = CancellationToken.None;

    private Harness _h = null!;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    // ── Deadline ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task An_open_event_past_its_deadline_expires_and_the_organizer_is_told()
    {
        var ev = await _h.CreateAsync();
        Assert.AreEqual(0, await _h.Jobs.ExpireOverdueEventsAsync(None), "not before the deadline");

        _h.Clock.Reset(Deadline + Duration.FromMinutes(1));
        _h.Changes.Published.Clear();
        Assert.AreEqual(1, await _h.Jobs.ExpireOverdueEventsAsync(None));
        Assert.AreEqual(0, await _h.Jobs.ExpireOverdueEventsAsync(None), "once");

        Assert.AreEqual(EventState.Expired, (await _h.GetAsync(Harness.Rocky, ev)).State);
        var email = (await _h.OutboxAsync()).Last();
        Assert.AreEqual("rocky@example.com", email.To);
        StringAssert.StartsWith(email.Subject, $"[sched:{ev.ShortCode}] Voting closed: Planning");
        var audit = (await _h.AuditAsync()).Last();
        Assert.AreEqual("event.expired", audit.Action);
        Assert.AreEqual(Channel.System, audit.Channel);
        Assert.IsNull(audit.Actor);
        CollectionAssert.Contains(_h.Changes.Published, ev.Id, "open pages are told");
    }

    [TestMethod]
    public async Task Proposing_more_times_reopens_an_expired_event()
    {
        var ev = await _h.CreateAsync();
        _h.Clock.Reset(Deadline + Duration.FromMinutes(1));
        await _h.Jobs.ExpireOverdueEventsAsync(None);
        var expired = await _h.GetAsync(Harness.Rocky, ev);

        var reopened = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode,
            [new("2026-11-09T10:00", "America/Chicago")], "2026-11-06T17:00", expired.Version, None);

        Assert.AreEqual(EventState.Open, reopened.State);
    }

    // ── Reminders ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Participants_who_havent_answered_everything_get_one_reminder_a_day_before()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Preferred), (1, VoteValue.No), (2, VoteValue.Preferred));
        await _h.VoteAsync(Harness.Carol, ev, (0, VoteValue.Preferred));

        _h.Clock.Reset(Deadline - Duration.FromHours(25));
        Assert.AreEqual(0, await _h.Jobs.SendRemindersAsync(None), "not yet");

        _h.Clock.Reset(Deadline - Duration.FromHours(23));
        var before = (await _h.OutboxAsync()).Count;
        Assert.AreEqual(2, await _h.Jobs.SendRemindersAsync(None), "Carol and Dave; Bob has answered every time");
        Assert.AreEqual(0, await _h.Jobs.SendRemindersAsync(None), "once");

        var reminders = (await _h.OutboxAsync()).Skip(before).ToList();
        CollectionAssert.AreEquivalent(new[] { "carol@example.com", "dave@example.com" }, reminders.Select(r => r.To).ToArray());
        var carol = reminders.Single(r => r.To == "carol@example.com");
        StringAssert.StartsWith(carol.Subject, $"[sched:{ev.ShortCode}] Reminder: Planning");
        StringAssert.Contains(carol.TextBody, "/e/" + ev.ShortCode + "?t=");
        Assert.DoesNotContain(ev.Slots[0].OrganizerLocal, carol.TextBody, "only the times Carol hasn't answered");
        StringAssert.Contains(carol.TextBody, ev.Slots[1].OrganizerLocal);
        Assert.AreEqual("reminders.sent", (await _h.AuditAsync()).Last().Action);
    }

    [TestMethod]
    public async Task Someone_invited_less_than_a_day_before_the_deadline_isnt_reminded()
    {
        await _h.CreateAsync(Harness.Request(respondBy: "2026-10-17T05:00")); // 15 hours after "now"

        Assert.AreEqual(0, await _h.Jobs.SendRemindersAsync(None));
    }

    [TestMethod]
    public async Task A_new_deadline_gets_a_new_reminder()
    {
        var ev = await _h.CreateAsync();
        _h.Clock.Reset(Deadline - Duration.FromHours(23));
        Assert.AreEqual(3, await _h.Jobs.SendRemindersAsync(None));

        var current = await _h.GetAsync(Harness.Rocky, ev);
        await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode,
            [new("2026-11-09T10:00", "America/Chicago")], "2026-11-06T17:00", current.Version, None);
        Assert.AreEqual(0, await _h.Jobs.SendRemindersAsync(None), "the new deadline is a week away");

        _h.Clock.Reset(Instant.FromUtc(2026, 11, 6, 23, 0) - Duration.FromHours(23)); // Nov 6 17:00 CST
        Assert.AreEqual(3, await _h.Jobs.SendRemindersAsync(None));
    }

    // ── Retention ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task A_closed_event_is_deleted_with_everything_about_it_after_the_retention_period()
    {
        var finalized = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, finalized, (0, VoteValue.Preferred));
        await _h.Service.FinalizeAsync(Harness.Rocky, finalized.ShortCode, finalized.Slots[0].SlotId,
            (await _h.GetAsync(Harness.Rocky, finalized)).Version, None);
        var open = await _h.CreateAsync(Harness.Request(title: "Still open", respondBy: null));

        _h.Clock.Advance(Duration.FromDays(29));
        Assert.AreEqual(0, await _h.Jobs.PurgeAsync(None), "kept for 30 days");

        _h.Clock.Advance(Duration.FromDays(2));
        Assert.AreEqual(1, await _h.Jobs.PurgeAsync(None));

        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.IsFalse(await db.Events.AnyAsync(e => e.Id == finalized.Id));
        Assert.IsFalse(await db.Participants.AnyAsync(p => p.EventId == finalized.Id));
        Assert.IsFalse(await db.Slots.AnyAsync(s => s.EventId == finalized.Id));
        Assert.IsFalse(await db.Votes.AnyAsync());
        Assert.IsFalse(await db.Audit.AnyAsync(a => a.EventId == finalized.Id));
        Assert.IsFalse(await db.Outbox.AnyAsync(o => o.EventId == finalized.Id));
        Assert.IsTrue(await db.Events.AnyAsync(e => e.Id == open.Id), "open events are never purged");
        Assert.IsTrue(await db.Outbox.AnyAsync(o => o.EventId == open.Id));
    }

    // ── Time zone rules ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task New_time_zone_rules_recompute_future_slots_from_their_local_time()
    {
        var ev = await _h.CreateAsync();
        Assert.AreEqual(0, await _h.Jobs.RecomputeSlotTimesAsync(None), "the first run only records the version");
        Assert.AreEqual(0, await _h.Jobs.RecomputeSlotTimesAsync(None), "same version: nothing to do");

        // As if the rules had changed: an older version recorded, and a slot's instant no longer matching them.
        await _h.WithDbAsync(async db =>
        {
            (await db.ServerSettings.SingleAsync()).Value = "2020a";
            var slot = await db.Slots.FirstAsync(s => s.Id == ev.Slots[0].SlotId);
            slot.Start += Duration.FromHours(1);
        });
        _h.Changes.Published.Clear();

        Assert.AreEqual(1, await _h.Jobs.RecomputeSlotTimesAsync(None));

        Assert.AreEqual(ev.Slots[0].StartUtc, (await _h.GetAsync(Harness.Rocky, ev)).Slots[0].StartUtc);
        CollectionAssert.Contains(_h.Changes.Published, ev.Id);
        await using var check = await _h.Db.CreateDbContextAsync();
        Assert.AreEqual(DateTimeZoneProviders.Tzdb.VersionId, (await check.ServerSettings.SingleAsync()).Value);
    }

    // ── Live updates and the dashboard ──────────────────────────────────────

    [TestMethod]
    public async Task Every_change_is_published_for_open_pages()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Preferred));
        await _h.Service.CancelAsync(Harness.Rocky, ev.ShortCode, null, None);

        CollectionAssert.AreEqual(new[] { ev.Id, ev.Id, ev.Id }, _h.Changes.Published.ToArray());
    }

    [TestMethod]
    public async Task The_organizer_sees_how_many_have_answered_and_participants_dont()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Preferred), (1, VoteValue.No), (2, VoteValue.Preferred));

        var mine = (await _h.Service.ListMyEventsAsync(Harness.Rocky, EventListFilter.Organizing, None)).Single();
        var bobs = (await _h.Service.ListMyEventsAsync(Harness.Bob, EventListFilter.All, None)).Single();

        Assert.AreEqual((1, 3), (mine.Responded, mine.Invited));
        Assert.IsNull(bobs.Responded);
        Assert.IsNull(bobs.Invited);
    }
}
