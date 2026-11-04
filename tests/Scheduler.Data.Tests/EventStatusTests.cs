using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

/// <summary>
/// The milestone view behind <c>scheduler://events/{code}/status</c>: its marker moves at milestones and only
/// then, and participants see only what they may (docs/design.md, "Push updates").
/// </summary>
[TestClass]
public sealed class EventStatusTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private Harness _h = null!;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    private Task<EventStatusView> StatusAsync(CallerIdentity caller, EventView ev) =>
        _h.Service.GetEventStatusAsync(caller, ev.ShortCode, None);

    [TestMethod]
    public async Task A_vote_that_reaches_no_milestone_leaves_the_marker_alone()
    {
        var ev = await _h.CreateAsync();
        var before = await StatusAsync(Harness.Rocky, ev);

        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Yes), (1, VoteValue.No), (2, VoteValue.Preferred));
        await _h.VoteAsync(Harness.Dave, ev, (0, VoteValue.No), (1, VoteValue.No), (2, VoteValue.No));
        var after = await StatusAsync(Harness.Rocky, ev);

        Assert.AreEqual(before.Marker, after.Marker);
        Assert.IsFalse(after.EveryoneResponded);
        Assert.IsEmpty(after.RulesOutEveryTime!, "Dave is optional");
    }

    [TestMethod]
    public async Task The_marker_moves_when_every_required_person_has_responded()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Yes), (1, VoteValue.Yes), (2, VoteValue.Yes));
        var before = await StatusAsync(Harness.Rocky, ev);

        await _h.VoteAsync(Harness.Carol, ev, (0, VoteValue.Yes), (1, VoteValue.IfNecessary), (2, VoteValue.Yes));
        var after = await StatusAsync(Harness.Rocky, ev);

        Assert.IsFalse(before.EveryoneResponded);
        Assert.IsTrue(after.EveryoneResponded);
        Assert.AreNotEqual(before.Marker, after.Marker);
    }

    [TestMethod]
    public async Task The_marker_moves_when_a_required_person_rules_out_every_time()
    {
        var ev = await _h.CreateAsync();
        var before = await StatusAsync(Harness.Rocky, ev);

        await _h.VoteAsync(Harness.Carol, ev, (0, VoteValue.No), (1, VoteValue.No), (2, VoteValue.No));
        var after = await StatusAsync(Harness.Rocky, ev);

        Assert.AreNotEqual(before.Marker, after.Marker);
        CollectionAssert.AreEqual(new[] { "carol@example.com" }, after.RulesOutEveryTime!.ToArray());
    }

    [TestMethod]
    public async Task The_marker_moves_on_finalize_cancel_and_expiry()
    {
        var finalized = await _h.CreateAsync();
        var cancelled = await _h.CreateAsync();
        var expired = await _h.CreateAsync();
        var before = new[]
        {
            await StatusAsync(Harness.Rocky, finalized),
            await StatusAsync(Harness.Rocky, cancelled),
            await StatusAsync(Harness.Bob, expired),
        };

        await _h.Service.FinalizeAsync(Harness.Rocky, finalized.ShortCode, finalized.Slots[1].SlotId, finalized.Version, None);
        await _h.Service.CancelAsync(Harness.Rocky, cancelled.ShortCode, "No longer needed", None);
        _h.Clock.Reset(Instant.FromUtc(2026, 10, 30, 22, 1));
        await _h.Jobs.ExpireOverdueEventsAsync(None);
        var after = new[]
        {
            await StatusAsync(Harness.Rocky, finalized),
            await StatusAsync(Harness.Rocky, cancelled),
            await StatusAsync(Harness.Bob, expired),
        };

        for (var i = 0; i < before.Length; i++)
            Assert.AreNotEqual(before[i].Marker, after[i].Marker, $"event {i}");
        Assert.AreEqual(EventState.Finalized, after[0].State);
        Assert.AreEqual(finalized.Slots[1].SlotId, after[0].FinalizedSlotId);
        Assert.AreEqual(EventState.Cancelled, after[1].State);
        Assert.AreEqual(EventState.Expired, after[2].State);
    }

    [TestMethod]
    public async Task A_participant_sees_no_organizer_fields_and_their_marker_ignores_votes()
    {
        var ev = await _h.CreateAsync();
        var before = await StatusAsync(Harness.Bob, ev);

        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Yes), (1, VoteValue.Yes), (2, VoteValue.Yes));
        await _h.VoteAsync(Harness.Carol, ev, (0, VoteValue.No), (1, VoteValue.No), (2, VoteValue.No));
        var after = await StatusAsync(Harness.Bob, ev);

        Assert.IsFalse(after.IsOrganizer);
        Assert.IsNull(after.EveryoneResponded);
        Assert.IsNull(after.RulesOutEveryTime);
        Assert.AreEqual(before.Marker, after.Marker, "everyone required responded and Carol rules out every time, but Bob may not know");
        Assert.IsNotNull(after.RespondBy);
    }

    [TestMethod]
    public async Task The_marker_moves_when_the_deadline_moves_by_seconds()
    {
        var ev = await _h.CreateAsync();
        var before = await StatusAsync(Harness.Bob, ev);

        await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, [new("2026-11-06T10:00", "America/Chicago")],
            suggestionIds: null, "2026-10-30T17:00:30", ev.Version, None);
        var after = await StatusAsync(Harness.Bob, ev);

        Assert.AreEqual(before.RespondBy, after.RespondBy, "the display text has no seconds");
        Assert.AreNotEqual(before.Marker, after.Marker);
    }

    [TestMethod]
    public async Task The_marker_is_the_same_on_every_read()
    {
        var ev = await _h.CreateAsync();

        Assert.AreEqual((await StatusAsync(Harness.Rocky, ev)).Marker, (await StatusAsync(Harness.Rocky, ev)).Marker);
        Assert.AreEqual(16, (await StatusAsync(Harness.Rocky, ev)).Marker.Length);
    }

    [TestMethod]
    public async Task Someone_not_invited_is_refused()
    {
        var ev = await _h.CreateAsync();

        await Assert.ThrowsExactlyAsync<SchedulingAccessException>(() => StatusAsync(Harness.Mallory, ev));
    }
}
