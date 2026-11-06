using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

[TestClass]
public sealed class VisibilityTests
{
    private Harness _h = null!;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    [TestMethod]
    public async Task ParticipantsSeeTotalsButNotEachOthersVotes()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Preferred), (1, VoteValue.No));

        var carolsView = await _h.GetAsync(Harness.Carol, ev);

        Assert.IsFalse(carolsView.IsOrganizer);
        Assert.AreEqual(new SlotTally(1, 0, 0, 0, 2), carolsView.Slots[0].Tally);
        Assert.AreEqual(new SlotTally(0, 0, 0, 1, 2), carolsView.Slots[1].Tally);
        Assert.IsNull(carolsView.Slots[0].MyVote);
        Assert.IsNull(carolsView.Slots[0].Rank);
        Assert.IsNull(carolsView.Slots[0].Viable);

        var bob = carolsView.Participants.Single(p => p.Email == "bob@example.com");
        Assert.AreEqual("Bob", bob.DisplayName);
        Assert.IsNull(bob.Votes);
        Assert.IsNull(bob.Responded);

        var carol = carolsView.Participants.Single(p => p.Email == "carol@example.com");
        Assert.IsNotNull(carol.Votes);
        Assert.IsFalse(carol.Responded);
    }

    [TestMethod]
    public async Task TheOrganizerSeesEveryVoteAndTheRanking()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.No), (1, VoteValue.Preferred));

        var view = await _h.GetAsync(Harness.Rocky, ev);

        var bob = view.Participants.Single(p => p.Email == "bob@example.com");
        Assert.AreEqual(VoteValue.No, bob.Votes![ev.Slots[0].SlotId]);
        Assert.AreEqual(VoteValue.Preferred, bob.Votes[ev.Slots[1].SlotId]);
        Assert.AreEqual(1, view.Slots[1].Rank);
        Assert.IsFalse(view.Slots[0].Viable);
        Assert.AreEqual(3, view.Slots[0].Rank);
    }

    [TestMethod]
    public async Task StrangersAndMadeUpIdsGetTheSameAnswer()
    {
        var ev = await _h.CreateAsync();

        var stranger = await Assert.ThrowsExactlyAsync<SchedulingAccessException>(() => _h.GetAsync(Harness.Mallory, ev));
        var madeUp = await Assert.ThrowsExactlyAsync<SchedulingAccessException>(
            () => _h.Service.GetEventAsync(Harness.Bob, Guid.NewGuid().ToString(), null, CancellationToken.None));

        Assert.AreEqual(stranger.Message, madeUp.Message);
    }

    [TestMethod]
    public async Task AViewerZoneAddsLocalTimes()
    {
        var ev = await _h.CreateAsync();

        var view = await _h.Service.GetEventAsync(Harness.Bob, ev.ShortCode, "Europe/London", CancellationToken.None);

        Assert.AreEqual("Tue Nov 3, 4:00 PM GMT (UTC+00:00)", view.Slots[0].ViewerLocal);
    }

    [TestMethod]
    public async Task AnInvalidViewerZoneIsRejected()
    {
        var ev = await _h.CreateAsync();

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => _h.Service.GetEventAsync(Harness.Bob, ev.ShortCode, "CST", CancellationToken.None));
    }

    [TestMethod]
    public async Task VotingLinkCallersSeeOnlyThatEvent()
    {
        var first = await _h.CreateAsync();
        var second = await _h.CreateAsync(Harness.Request(title: "Another"));
        var bobByLink = new CallerIdentity(null, ["bob@example.com"], Channel.Web, MagicLinkEventId: first.Id);

        Assert.AreEqual(first.Id, (await _h.GetAsync(bobByLink, first)).Id);
        await Assert.ThrowsExactlyAsync<SchedulingAccessException>(() => _h.GetAsync(bobByLink, second));

        var listed = await _h.Service.ListMyEventsAsync(bobByLink, EventListFilter.All, includePast: false, CancellationToken.None);
        Assert.AreEqual(first.Id, listed.Single().Id);
    }

    [TestMethod]
    public async Task AVotingLinkForTheOrganizersAddressGrantsNoOrganizerRights()
    {
        var ev = await _h.CreateAsync();
        var linkAsRocky = new CallerIdentity(null, ["rocky@example.com"], Channel.Web, MagicLinkEventId: ev.Id);

        // Rocky isn't a participant, and a link caller is never resolved to an account.
        await Assert.ThrowsExactlyAsync<SchedulingAccessException>(
            () => _h.Service.CancelAsync(linkAsRocky, ev.ShortCode, null, CancellationToken.None));
    }

    [TestMethod]
    public async Task ListingFiltersByRole()
    {
        var ev = await _h.CreateAsync();

        var organizing = await _h.Service.ListMyEventsAsync(Harness.Rocky, EventListFilter.Organizing, includePast: false, CancellationToken.None);
        var rockyNeeds = await _h.Service.ListMyEventsAsync(Harness.Rocky, EventListFilter.NeedsMyVote, includePast: false, CancellationToken.None);
        var bobOrganizing = await _h.Service.ListMyEventsAsync(Harness.Bob, EventListFilter.Organizing, includePast: false, CancellationToken.None);

        Assert.IsTrue(organizing.Single().IsOrganizer);
        Assert.IsEmpty(rockyNeeds);
        Assert.IsEmpty(bobOrganizing);
        Assert.AreEqual(ev.Id, organizing.Single().Id);
    }

    [TestMethod]
    public async Task PastEventsAreListedOnlyWhenAskedFor()
    {
        var open = await _h.CreateAsync(Harness.Request(respondBy: null, title: "Open"));
        var cancelled = await _h.CreateAsync(Harness.Request(title: "Cancelled"));
        var expired = await _h.CreateAsync(Harness.Request(title: "Expired"));
        // Slot 0 is Tue Nov 3, 10:00-10:30 Chicago (16:00-16:30 UTC).
        var finalized = await _h.CreateAsync(Harness.Request(title: "Finalized"));
        await _h.Service.CancelAsync(Harness.Rocky, cancelled.ShortCode, "No longer needed", CancellationToken.None);
        await _h.Service.FinalizeAsync(Harness.Rocky, finalized.ShortCode, finalized.Slots[0].SlotId, finalized.Version, CancellationToken.None);
        _h.Clock.Reset(Instant.FromUtc(2026, 10, 31, 12, 0));
        Assert.AreEqual(1, await _h.Jobs.ExpireOverdueEventsAsync(CancellationToken.None));

        async Task<string[]> TitlesAsync(CallerIdentity caller, EventListFilter filter, bool includePast) =>
            (await _h.Service.ListMyEventsAsync(caller, filter, includePast, CancellationToken.None))
                .Select(e => e.Title).Order().ToArray();

        CollectionAssert.AreEqual(new[] { "Finalized", "Open" }, await TitlesAsync(Harness.Rocky, EventListFilter.Organizing, false));
        CollectionAssert.AreEqual(new[] { "Finalized", "Open" }, await TitlesAsync(Harness.Bob, EventListFilter.All, false));
        CollectionAssert.AreEqual(
            new[] { "Cancelled", "Expired", "Finalized", "Open" }, await TitlesAsync(Harness.Rocky, EventListFilter.Organizing, true));
        CollectionAssert.AreEqual(
            new[] { "Cancelled", "Expired", "Finalized", "Open" }, await TitlesAsync(Harness.Bob, EventListFilter.All, true));

        _h.Clock.Reset(Instant.FromUtc(2026, 11, 3, 16, 29));
        CollectionAssert.AreEqual(new[] { "Finalized", "Open" }, await TitlesAsync(Harness.Bob, EventListFilter.All, false), "during the meeting");

        _h.Clock.Reset(Instant.FromUtc(2026, 11, 3, 16, 30));
        CollectionAssert.AreEqual(new[] { "Open" }, await TitlesAsync(Harness.Bob, EventListFilter.All, false), "once it has ended");
        CollectionAssert.AreEqual(new[] { "Open" }, await TitlesAsync(Harness.Bob, EventListFilter.NeedsMyVote, false));
        CollectionAssert.AreEqual(new[] { "Open" }, await TitlesAsync(Harness.Bob, EventListFilter.NeedsMyVote, true), "past events never need a vote");
    }
}
