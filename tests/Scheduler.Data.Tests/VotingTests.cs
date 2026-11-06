using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
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
        var partway = await _h.Service.ListMyEventsAsync(Harness.Bob, EventListFilter.NeedsMyVote, includePast: false, CancellationToken.None);
        var view = await _h.VoteAsync(Harness.Bob, ev, (1, VoteValue.IfNecessary), (2, VoteValue.No));
        var done = await _h.Service.ListMyEventsAsync(Harness.Bob, EventListFilter.NeedsMyVote, includePast: false, CancellationToken.None);

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
            [new("2026-11-06T14:00", "America/Chicago")], suggestionIds: null, null, ev.Version, CancellationToken.None);
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

    [TestMethod]
    public async Task TimesAFormShowedAndTheVoterLeftBlankAreSavedAsNo()
    {
        var ev = await _h.CreateAsync();

        var view = await FormVoteAsync(ev, [ev.Slots[0].SlotId, ev.Slots[1].SlotId, ev.Slots[2].SlotId], (0, VoteValue.Yes));

        CollectionAssert.AreEqual(new VoteValue?[] { VoteValue.Yes, VoteValue.No, VoteValue.No }, view.Slots.Select(s => s.MyVote).ToList());
        CollectionAssert.AreEqual(new[] { "You left 2 times blank, so they're saved as No." }, view.Notices!.ToList());
        Assert.IsTrue(view.Participants.Single(p => p.Email == "bob@example.com").Responded, "every time is answered");
        var audit = (await _h.AuditAsync()).Last(a => a.Action == "vote.cast");
        Assert.AreEqual(2, JsonDocument.Parse(audit.Detail!).RootElement.GetProperty("blankAsNo").GetInt32());
    }

    [TestMethod]
    public async Task ABlankTimeKeepsAnAnswerSavedSinceTheFormWasShown()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (1, VoteValue.Preferred));   // say, by their agent, after the page loaded

        var view = await FormVoteAsync(ev, [ev.Slots[0].SlotId, ev.Slots[1].SlotId, ev.Slots[2].SlotId], (0, VoteValue.Yes));

        CollectionAssert.AreEqual(new VoteValue?[] { VoteValue.Yes, VoteValue.Preferred, VoteValue.No }, view.Slots.Select(s => s.MyVote).ToList());
        CollectionAssert.AreEqual(new[] { "You left 1 time blank, so it's saved as No." }, view.Notices!.ToList());
    }

    [TestMethod]
    public async Task ATimeAddedOrWithdrawnSinceTheFormWasShownIsntSavedAsNo()
    {
        var ev = await _h.CreateAsync();
        var shown = new[] { ev.Slots[0].SlotId, ev.Slots[1].SlotId, ev.Slots[2].SlotId, Guid.NewGuid() };
        var more = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode,
            [new("2026-11-06T14:00", "America/Chicago")], suggestionIds: null, null, ev.Version, CancellationToken.None);
        await _h.Service.WithdrawSlotAsync(Harness.Rocky, ev.ShortCode, ev.Slots[2].SlotId, more.Version, CancellationToken.None);

        var view = await FormVoteAsync(ev, shown, (0, VoteValue.Yes));

        CollectionAssert.AreEqual(new VoteValue?[] { VoteValue.Yes, VoteValue.No, null }, view.Slots.Select(s => s.MyVote).ToList(),
            "the withdrawn time is gone, and the new one wasn't on the form");
        Assert.IsFalse(view.Participants.Single(p => p.Email == "bob@example.com").Responded);
        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.AreEqual(2, await db.Votes.CountAsync());
    }

    [TestMethod]
    public async Task ANoWithEveryOtherTimeLeftBlankTellsTheOrganizerNoTimeWorks()
    {
        var ev = await _h.CreateAsync();

        await FormVoteAsync(ev, [ev.Slots[0].SlotId, ev.Slots[1].SlotId, ev.Slots[2].SlotId], (0, VoteValue.No));

        Assert.HasCount(1, CantMakeIt(await _h.OutboxAsync()));
    }

    [TestMethod]
    public async Task ADoubleSubmitSavesWithoutAConflict()
    {
        var ev = await _h.CreateAsync();
        Guid[] shown = [ev.Slots[0].SlotId, ev.Slots[1].SlotId, ev.Slots[2].SlotId];

        // A double tap on Save: both requests find nothing saved yet, and would insert the same answers. A few tries,
        // because each one is a race.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.WhenAll(FormVoteAsync(ev, shown, (0, VoteValue.Yes)), FormVoteAsync(ev, shown, (0, VoteValue.Yes)));

            var view = await _h.GetAsync(Harness.Bob, ev);
            CollectionAssert.AreEqual(new VoteValue?[] { VoteValue.Yes, VoteValue.No, VoteValue.No }, view.Slots.Select(s => s.MyVote).ToList());
            await using var db = await _h.Db.CreateDbContextAsync();
            await db.Votes.ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    public async Task WithoutAFormAnOmittedTimeStaysUnanswered()
    {
        var ev = await _h.CreateAsync();

        var view = await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Yes));

        CollectionAssert.AreEqual(new VoteValue?[] { VoteValue.Yes, null, null }, view.Slots.Select(s => s.MyVote).ToList());
        Assert.IsNull(view.Notices);
    }

    /// <summary>A vote from the voting page, which showed <paramref name="shown"/>; any of them not answered was left blank.</summary>
    private Task<EventView> FormVoteAsync(EventView ev, Guid[] shown, params (int Slot, VoteValue Value)[] votes) =>
        _h.Service.VoteAsync(Harness.Bob, ev.ShortCode, [.. votes.Select(v => new VoteInput(ev.Slots[v.Slot].SlotId, v.Value))],
            null, emailReceipt: false, unansweredAsNo: shown, CancellationToken.None);

    private static List<OutboxEmail> CantMakeIt(IEnumerable<OutboxEmail> outbox) =>
        outbox.Where(o => o.Subject.Contains("can't make any time", StringComparison.Ordinal)).ToList();

    [TestMethod]
    public async Task AVoterCanAskForACopyOfTheirAnswers()
    {
        var ev = await _h.CreateAsync();

        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.Yes));
        Assert.IsFalse((await _h.OutboxAsync()).Any(o => o.To == "bob@example.com" && o.Subject.Contains("Your answers", StringComparison.Ordinal)),
            "no copy unless asked");

        var view = await _h.Service.VoteAsync(Harness.Bob, ev.ShortCode,
            [new(ev.Slots[0].SlotId, VoteValue.Preferred), new(ev.Slots[2].SlotId, VoteValue.IfNecessary)],
            "Mornings are hard", emailReceipt: true, unansweredAsNo: null, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "A copy of your answers is on its way to bob@example.com." }, view.Notices!.ToList());

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
        Assert.AreEqual("bob@example.com", JsonDocument.Parse(audit.Detail!).RootElement.GetProperty("receipt")[0].GetString());
    }

    [TestMethod]
    public async Task ARetryThatChangesNothingDoesntSendASecondCopy()
    {
        var ev = await _h.CreateAsync();

        await ReceiptVoteAsync(ev, VoteValue.Yes, "Either works");
        var retry = await ReceiptVoteAsync(ev, VoteValue.Yes, "Either works");

        Assert.HasCount(1, await ReceiptsAsync());
        StringAssert.Contains(retry.Notices!.Single(), "a few minutes ago, so another wasn't sent");
        Assert.AreEqual(VoteValue.Yes, retry.Slots[0].MyVote, "the vote itself still applies");
    }

    [TestMethod]
    public async Task ChangedAnswersOrALaterAskSendAnotherCopy()
    {
        var ev = await _h.CreateAsync();

        await ReceiptVoteAsync(ev, VoteValue.Yes, null);
        await ReceiptVoteAsync(ev, VoteValue.No, null);
        await ReceiptVoteAsync(ev, VoteValue.No, "Sorry");
        _h.Clock.Advance(Duration.FromMinutes(11));
        await ReceiptVoteAsync(ev, VoteValue.No, "Sorry");

        var receipts = await ReceiptsAsync();
        Assert.HasCount(4, receipts);
        StringAssert.Contains(receipts[1].TextBody, ": No");
        StringAssert.Contains(receipts[2].TextBody, "  > Sorry");
    }

    [TestMethod]
    public async Task NoCopyGoesToAnAddressThatTakesNoEmail()
    {
        var ev = await _h.CreateAsync();
        await using (var db = await _h.Db.CreateDbContextAsync())
        {
            db.Suppressions.Add(new EmailSuppression { Email = "bob@example.com", Reason = SuppressionReason.Unsubscribed, At = _h.Clock.GetCurrentInstant() });
            db.Suppressions.Add(new EmailSuppression { Email = "carol@example.com", Reason = SuppressionReason.Bounced, At = _h.Clock.GetCurrentInstant() });
            await db.SaveChangesAsync();
        }

        var bob = await ReceiptVoteAsync(ev, VoteValue.Yes, null);
        var carol = await _h.Service.VoteAsync(Harness.Carol, ev.ShortCode, [new(ev.Slots[0].SlotId, VoteValue.Yes)], null, emailReceipt: true, unansweredAsNo: null, CancellationToken.None);

        Assert.IsEmpty(await ReceiptsAsync());
        StringAssert.Contains(bob.Notices!.Single(), "bob@example.com has asked this server not to email it, so no copy of your answers was sent.");
        StringAssert.Contains(carol.Notices!.Single(), "Email to carol@example.com has bounced before, so no copy of your answers was sent.");
        Assert.AreEqual(VoteValue.Yes, bob.Slots[0].MyVote, "the answers are still saved");
    }

    private Task<EventView> ReceiptVoteAsync(EventView ev, VoteValue value, string? note) =>
        _h.Service.VoteAsync(Harness.Bob, ev.ShortCode, [new(ev.Slots[0].SlotId, value)], note, emailReceipt: true, unansweredAsNo: null, CancellationToken.None);

    private async Task<List<OutboxEmail>> ReceiptsAsync() =>
        (await _h.OutboxAsync()).Where(o => o.Subject.Contains("Your answers", StringComparison.Ordinal)).ToList();

    [TestMethod]
    public async Task UnknownAndRepeatedSlotsAreRejected()
    {
        var ev = await _h.CreateAsync();

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => _h.Service.VoteAsync(
            Harness.Bob, ev.ShortCode, [new(Guid.NewGuid(), VoteValue.Preferred)], null, emailReceipt: false, unansweredAsNo: null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => _h.VoteAsync(
            Harness.Bob, ev, (0, VoteValue.Preferred), (0, VoteValue.No)));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => _h.Service.VoteAsync(
            Harness.Bob, ev.ShortCode, [], null, emailReceipt: false, unansweredAsNo: null, CancellationToken.None));
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

        await _h.Service.VoteAsync(Harness.Bob, ev.ShortCode, [new(slot, VoteValue.IfNecessary)], "Mornings are hard", emailReceipt: false, unansweredAsNo: null, CancellationToken.None);
        var kept = await _h.Service.VoteAsync(Harness.Bob, ev.ShortCode, [new(slot, VoteValue.No)], null, emailReceipt: false, unansweredAsNo: null, CancellationToken.None);
        var cleared = await _h.Service.VoteAsync(Harness.Bob, ev.ShortCode, [new(slot, VoteValue.No)], "", emailReceipt: false, unansweredAsNo: null, CancellationToken.None);

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
