using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

/// <summary>Participants suggesting times that aren't on the poll, and the organizer adding or dismissing them.</summary>
[TestClass]
public sealed class SuggestionTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Fri Nov 6, 2:00 PM in Chicago.</summary>
    private static readonly SlotInput Friday = new("2026-11-06T14:00", "America/Chicago");

    /// <summary>Mon Nov 9, 9:00 AM in New York: 8:00 AM in the organizer's Chicago.</summary>
    private static readonly SlotInput MondayInNewYork = new("2026-11-09T09:00", "America/New_York");

    private Harness _h = null!;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    private Task<EventView> SuggestAsync(CallerIdentity caller, EventView ev, params SlotInput[] slots) =>
        _h.Service.SuggestSlotsAsync(caller, ev.ShortCode, slots, None);

    [TestMethod]
    public async Task A_suggestion_emails_the_organizer_and_changes_nothing_anyone_votes_on()
    {
        var ev = await _h.CreateAsync();
        var before = (await _h.OutboxAsync()).Count;
        _h.Changes.Published.Clear();

        var view = await SuggestAsync(Harness.Bob, ev, Friday, MondayInNewYork);

        Assert.HasCount(3, view.Slots, "a suggestion isn't a proposed time");
        Assert.AreEqual(ev.Version, view.Version, "it doesn't invalidate the organizer's version, like a vote");
        Assert.HasCount(2, view.Suggestions!);
        Assert.IsTrue(view.Suggestions!.All(s => s.Status == SuggestionStatus.Pending && s.SuggestedBy == "bob@example.com"));
        Assert.AreEqual("Mon Nov 9, 8:00 AM CST (UTC-06:00)", view.Suggestions![1].OrganizerLocal);
        Assert.AreEqual("Mon Nov 9, 9:00 AM EST (UTC-05:00)", view.Suggestions![1].SuggestedLocal, "also as the person meant it");
        StringAssert.Contains(view.Notices!.Single(), "sent to the organizer");
        CollectionAssert.Contains(_h.Changes.Published, ev.Id, "the organizer's open page updates");

        var sent = (await _h.OutboxAsync()).Skip(before).ToList();
        var email = sent.Single();
        Assert.AreEqual("rocky@example.com", email.To);
        Assert.IsNull(email.ReplyTo);
        StringAssert.StartsWith(email.Subject, $"[sched:{ev.ShortCode}] Bob suggested 2 other times: Planning");
        StringAssert.Contains(email.TextBody, "Bob (bob@example.com) suggested times for \"Planning\" that aren't on the poll:");
        StringAssert.Contains(email.TextBody, "  - Fri Nov 6, 2:00 PM CST (UTC-06:00)");
        StringAssert.Contains(email.TextBody, "  - Mon Nov 9, 8:00 AM CST (UTC-06:00)");
        StringAssert.Contains(email.TextBody, $"{Harness.BaseUrl}/events/{ev.Id}");

        var audit = (await _h.AuditAsync()).Last();
        Assert.AreEqual("slots.suggested", audit.Action);
        Assert.AreEqual("bob@example.com", audit.Actor);
    }

    [TestMethod]
    public async Task Only_the_organizer_and_the_person_who_suggested_a_time_see_it()
    {
        var ev = await _h.CreateAsync();
        await SuggestAsync(Harness.Bob, ev, Friday);

        Assert.HasCount(1, (await _h.GetAsync(Harness.Rocky, ev)).Suggestions!);
        Assert.HasCount(1, (await _h.GetAsync(Harness.Bob, ev)).Suggestions!);
        Assert.IsNull((await _h.GetAsync(Harness.Carol, ev)).Suggestions, "other participants don't see who suggested what");

        var summary = (await _h.Service.ListMyEventsAsync(Harness.Rocky, EventListFilter.Organizing, None)).Single();
        Assert.AreEqual(1, summary.PendingSuggestions);
        Assert.IsNull((await _h.Service.ListMyEventsAsync(Harness.Bob, EventListFilter.All, None)).Single().PendingSuggestions);
    }

    [TestMethod]
    public async Task Suggesting_the_same_time_again_is_a_safe_retry()
    {
        var ev = await _h.CreateAsync();
        await SuggestAsync(Harness.Bob, ev, Friday);
        var before = (await _h.OutboxAsync()).Count;

        var view = await SuggestAsync(Harness.Bob, ev, Friday, Friday);

        Assert.HasCount(1, view.Suggestions!);
        Assert.AreEqual("You already suggested Fri Nov 6, 2:00 PM CST (UTC-06:00); it's waiting for the organizer.", view.Notices!.Single());
        Assert.HasCount(before, await _h.OutboxAsync(), "no second email to the organizer");
    }

    [TestMethod]
    public async Task A_time_already_on_the_poll_is_not_suggested()
    {
        var ev = await _h.CreateAsync();
        var before = (await _h.OutboxAsync()).Count;

        var view = await SuggestAsync(Harness.Bob, ev, new SlotInput("2026-11-03T10:00", "America/Chicago"));

        Assert.IsNull(view.Suggestions);
        StringAssert.Contains(view.Notices!.Single(), "is already one of the proposed times");
        Assert.HasCount(before, await _h.OutboxAsync());
    }

    [TestMethod]
    public async Task Suggestions_are_for_the_people_invited_while_voting_is_open()
    {
        var ev = await _h.CreateAsync();

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => SuggestAsync(Harness.Rocky, ev, Friday));
        await Assert.ThrowsExactlyAsync<SchedulingAccessException>(() => SuggestAsync(Harness.Mallory, ev, Friday));
        var past = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => SuggestAsync(Harness.Bob, ev, new SlotInput("2026-10-01T10:00", "America/Chicago")));
        StringAssert.Contains(past.Message, "is in the past");
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => SuggestAsync(Harness.Bob, ev, new SlotInput("2026-11-06T14:00", "CST")));

        await _h.Service.FinalizeAsync(Harness.Rocky, ev.ShortCode, ev.Slots[0].SlotId, ev.Version, None);
        var closed = await Assert.ThrowsExactlyAsync<SchedulingConflictException>(() => SuggestAsync(Harness.Bob, ev, Friday));
        StringAssert.Contains(closed.Message, "isn't taking suggestions");
    }

    [TestMethod]
    public async Task A_participant_can_have_only_a_few_suggestions_waiting()
    {
        var ev = await _h.CreateAsync();
        var five = Enumerable.Range(9, FieldLimits.MaxPendingSuggestionsPerParticipant)
            .Select(day => new SlotInput($"2026-11-{day:00}T10:00", "America/Chicago"))
            .ToArray();
        await SuggestAsync(Harness.Bob, ev, five);

        var ex = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => SuggestAsync(Harness.Bob, ev, Friday));
        StringAssert.Contains(ex.Message, "at most 5 suggested times waiting");

        // Someone else has their own allowance, and the organizer deciding frees it up.
        await SuggestAsync(Harness.Carol, ev, Friday);
        var organizer = await _h.GetAsync(Harness.Rocky, ev);
        var bobs = organizer.Suggestions!.First(s => s.SuggestedBy == "bob@example.com");
        await _h.Service.DismissSuggestionsAsync(Harness.Rocky, ev.ShortCode, [bobs.SuggestionId], organizer.Version, None);
        var view = await SuggestAsync(Harness.Bob, ev, Friday);
        Assert.HasCount(6, view.Suggestions!);
    }

    [TestMethod]
    public async Task Adding_a_suggestion_puts_it_on_the_poll_and_emails_everyone()
    {
        var ev = await _h.CreateAsync();
        await _h.VoteAsync(Harness.Bob, ev, (0, VoteValue.No), (1, VoteValue.No), (2, VoteValue.No));
        await SuggestAsync(Harness.Bob, ev, MondayInNewYork);
        var organizer = await _h.GetAsync(Harness.Rocky, ev);
        var suggestion = organizer.Suggestions!.Single();
        var before = (await _h.OutboxAsync()).Count;

        var view = await _h.Service.ProposeMoreSlotsAsync(
            Harness.Rocky, ev.ShortCode, slots: null, [suggestion.SuggestionId], respondBy: null, organizer.Version, None);

        Assert.HasCount(4, view.Slots);
        Assert.AreEqual(suggestion.OrganizerLocal, view.Slots[3].OrganizerLocal, "the time the person meant");
        Assert.AreEqual(SuggestionStatus.Added, view.Suggestions!.Single().Status);
        Assert.IsFalse(view.Participants.Single(p => p.Email == "bob@example.com").Responded, "Bob answers the new time too");

        var sent = (await _h.OutboxAsync()).Skip(before).ToList();
        CollectionAssert.AreEquivalent(new[] { "bob@example.com", "carol@example.com", "dave@example.com" }, sent.Select(o => o.To).ToArray());
        Assert.IsTrue(sent.All(o => o.Subject.Contains("New times for: Planning", StringComparison.Ordinal)));
        StringAssert.Contains(sent[0].TextBody, suggestion.OrganizerLocal);

        await using (var db = await _h.Db.CreateDbContextAsync())
        {
            var stored = await db.Suggestions.SingleAsync();
            Assert.AreEqual(view.Slots[3].SlotId, stored.SlotId);
            Assert.AreEqual("America/New_York", (await db.Slots.SingleAsync(s => s.Id == stored.SlotId)).TimeZoneId);
        }
        var audit = (await _h.AuditAsync()).Last();
        Assert.AreEqual("slots.proposed", audit.Action);
        Assert.AreEqual(1, JsonDocument.Parse(audit.Detail!).RootElement.GetProperty("suggestions").GetInt32());

        var again = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => _h.Service.ProposeMoreSlotsAsync(
            Harness.Rocky, ev.ShortCode, slots: null, [suggestion.SuggestionId], respondBy: null, view.Version, None));
        StringAssert.Contains(again.Message, "was already added");
    }

    [TestMethod]
    public async Task Concurrent_suggestions_from_one_person_are_saved_once()
    {
        var ev = await _h.CreateAsync();
        var before = (await _h.OutboxAsync()).Count;

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SuggestAsync(Harness.Bob, ev, Friday)));

        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.AreEqual(1, await db.Suggestions.CountAsync());
        Assert.HasCount(before + 1, await _h.OutboxAsync(), "one email to the organizer");
    }

    [TestMethod]
    public async Task The_same_time_suggested_by_two_people_or_proposed_too_is_added_once()
    {
        var ev = await _h.CreateAsync();
        await SuggestAsync(Harness.Bob, ev, Friday);
        await SuggestAsync(Harness.Carol, ev, Friday, MondayInNewYork);
        var organizer = await _h.GetAsync(Harness.Rocky, ev);

        var view = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, [Friday],
            [.. organizer.Suggestions!.Select(s => s.SuggestionId)], respondBy: null, organizer.Version, None);

        Assert.HasCount(5, view.Slots, "Friday once, and Monday");
        Assert.IsTrue(view.Suggestions!.All(s => s.Status == SuggestionStatus.Added));
    }

    [TestMethod]
    public async Task An_added_suggestion_keeps_its_seconds()
    {
        var ev = await _h.CreateAsync();
        await SuggestAsync(Harness.Bob, ev, new SlotInput("2026-11-06T14:00:30", "America/Chicago"));
        var organizer = await _h.GetAsync(Harness.Rocky, ev);

        var view = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, slots: null,
            [organizer.Suggestions!.Single().SuggestionId], respondBy: null, organizer.Version, None);

        Assert.AreEqual(SuggestionStatus.Added, view.Suggestions!.Single().Status);
        Assert.AreEqual(organizer.Suggestions!.Single().StartUtc, view.Slots[3].StartUtc);
        StringAssert.EndsWith(view.Slots[3].StartUtc, ":30Z");
    }

    [TestMethod]
    public async Task A_suggestion_the_time_zone_rules_now_skip_is_added_like_the_job_resolves_it()
    {
        var ev = await _h.CreateAsync(Harness.Request(respondBy: null));
        await SuggestAsync(Harness.Bob, ev, Friday);
        // As if new rules had changed this time since it was suggested: 2:30 AM on the day clocks spring forward.
        var skipped = new LocalDateTime(2027, 3, 14, 2, 30);
        await _h.WithDbAsync(async db => (await db.Suggestions.SingleAsync()).LocalStart = skipped);
        var organizer = await _h.GetAsync(Harness.Rocky, ev);

        var view = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, slots: null,
            [organizer.Suggestions!.Single().SuggestionId], respondBy: null, organizer.Version, None);

        Assert.AreEqual(SuggestionStatus.Added, view.Suggestions!.Single().Status);
        Assert.AreEqual("Sun Mar 14, 3:30 AM CDT (UTC-05:00)", view.Slots[3].OrganizerLocal, "moved forward, not refused");
    }

    [TestMethod]
    public async Task Proposing_a_suggested_time_yourself_marks_the_suggestion_added()
    {
        var ev = await _h.CreateAsync();
        await SuggestAsync(Harness.Bob, ev, Friday);
        await SuggestAsync(Harness.Carol, ev, Friday);

        var view = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, [Friday], suggestionIds: null, null, ev.Version, None);

        Assert.HasCount(2, view.Suggestions!);
        Assert.IsTrue(view.Suggestions!.All(s => s.Status == SuggestionStatus.Added));
        Assert.HasCount(4, view.Slots);
    }

    [TestMethod]
    public async Task Only_the_organizer_adds_or_dismisses_suggestions()
    {
        var ev = await _h.CreateAsync();
        var mine = (await SuggestAsync(Harness.Bob, ev, Friday)).Suggestions!.Single();

        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => _h.Service.ProposeMoreSlotsAsync(
            Harness.Bob, ev.ShortCode, slots: null, [mine.SuggestionId], respondBy: null, ev.Version, None));
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(
            () => _h.Service.DismissSuggestionsAsync(Harness.Bob, ev.ShortCode, [mine.SuggestionId], ev.Version, None));
        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => _h.Service.DismissSuggestionsAsync(Harness.Rocky, ev.ShortCode, [Guid.NewGuid()], ev.Version, None));
    }

    [TestMethod]
    public async Task Dismissing_a_suggestion_emails_no_one_and_it_cant_be_suggested_again()
    {
        var ev = await _h.CreateAsync();
        await SuggestAsync(Harness.Bob, ev, Friday);
        var organizer = await _h.GetAsync(Harness.Rocky, ev);
        var suggestion = organizer.Suggestions!.Single();
        var before = (await _h.OutboxAsync()).Count;

        var view = await _h.Service.DismissSuggestionsAsync(Harness.Rocky, ev.ShortCode, [suggestion.SuggestionId], organizer.Version, None);

        Assert.AreEqual(SuggestionStatus.Dismissed, view.Suggestions!.Single().Status);
        Assert.AreNotEqual(organizer.Version, view.Version, "an organizer edit moves the version");
        Assert.HasCount(before, await _h.OutboxAsync());
        Assert.AreEqual("suggestions.dismissed", (await _h.AuditAsync()).Last().Action);

        var bob = await SuggestAsync(Harness.Bob, ev, Friday);
        Assert.AreEqual(SuggestionStatus.Dismissed, bob.Suggestions!.Single().Status, "Bob sees it wasn't added");
        StringAssert.Contains(bob.Notices!.Single(), "decided not to add");

        // Dismissing again changes nothing; a stale version is refused like any organizer edit.
        var again = await _h.Service.DismissSuggestionsAsync(Harness.Rocky, ev.ShortCode, [suggestion.SuggestionId], view.Version, None);
        Assert.AreEqual(view.Version, again.Version);
        await Assert.ThrowsExactlyAsync<SchedulingConflictException>(
            () => _h.Service.DismissSuggestionsAsync(Harness.Rocky, ev.ShortCode, [suggestion.SuggestionId], organizer.Version, None));

        // The organizer can still change their mind and add it.
        var added = await _h.Service.ProposeMoreSlotsAsync(
            Harness.Rocky, ev.ShortCode, slots: null, [suggestion.SuggestionId], respondBy: null, view.Version, None);
        Assert.AreEqual(SuggestionStatus.Added, added.Suggestions!.Single().Status);
        var refused = await Assert.ThrowsExactlyAsync<SchedulingValidationException>(
            () => _h.Service.DismissSuggestionsAsync(Harness.Rocky, ev.ShortCode, [suggestion.SuggestionId], added.Version, None));
        StringAssert.Contains(refused.Message, "Withdraw that time instead");
    }

    [TestMethod]
    public async Task A_waiting_suggestion_can_reopen_an_expired_event()
    {
        var ev = await _h.CreateAsync();
        await SuggestAsync(Harness.Bob, ev, Friday);
        _h.Clock.Advance(Duration.FromDays(15));
        await _h.Jobs.ExpireOverdueEventsAsync(None);
        var expired = await _h.GetAsync(Harness.Rocky, ev);
        Assert.AreEqual(EventState.Expired, expired.State);

        var view = await _h.Service.ProposeMoreSlotsAsync(
            Harness.Rocky, ev.ShortCode, slots: null, [expired.Suggestions!.Single().SuggestionId], respondBy: null, expired.Version, None);

        Assert.AreEqual(EventState.Open, view.State);
        Assert.AreEqual(SuggestionStatus.Added, view.Suggestions!.Single().Status);
    }

    [TestMethod]
    public async Task Suggestions_are_purged_with_their_event()
    {
        var ev = await _h.CreateAsync();
        await SuggestAsync(Harness.Bob, ev, Friday);
        await _h.Service.CancelAsync(Harness.Rocky, ev.ShortCode, "Not needed", None);

        _h.Clock.Advance(Duration.FromDays(31));
        Assert.AreEqual(1, await _h.Jobs.PurgeAsync(None));

        await using var db = await _h.Db.CreateDbContextAsync();
        Assert.IsFalse(await db.Suggestions.AnyAsync());
    }
}
