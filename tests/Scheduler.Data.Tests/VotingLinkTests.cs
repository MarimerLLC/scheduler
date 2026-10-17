using Microsoft.EntityFrameworkCore;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

/// <summary>Voting links (the token in each participant's emails), unsubscribing, and the organizer being told.</summary>
[TestClass]
public sealed class VotingLinkTests
{
    private Harness _h = null!;
    private static readonly CancellationToken None = CancellationToken.None;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync();

    // ── Voting links ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task A_voting_link_is_that_participant_for_that_event_only()
    {
        var ev = await _h.CreateAsync();
        var token = await TokenOfAsync("bob@example.com");

        var caller = await _h.Service.ResolveVotingLinkAsync(ev.ShortCode, token, None);

        Assert.IsNotNull(caller);
        Assert.AreEqual(ev.Id, caller.MagicLinkEventId);
        CollectionAssert.AreEqual(new[] { "bob@example.com" }, caller.VerifiedEmails.ToArray());
        Assert.IsNull(caller.UserId);

        var view = await _h.VoteAsync(caller, ev, (0, VoteValue.Preferred));
        Assert.AreEqual(VoteValue.Preferred, view.Slots[0].MyVote);
        Assert.IsFalse(view.IsOrganizer);
    }

    [TestMethod]
    public async Task A_link_is_refused_for_another_event_or_when_garbled()
    {
        var ev = await _h.CreateAsync();
        var other = await _h.CreateAsync(Harness.Request(title: "Other"));
        var token = await TokenOfAsync("bob@example.com", ev.Id);

        Assert.IsNull(await _h.Service.ResolveVotingLinkAsync(other.ShortCode, token, None));
        Assert.IsNull(await _h.Service.ResolveVotingLinkAsync(ev.ShortCode, token[..^2] + "AA", None));
        Assert.IsNull(await _h.Service.ResolveVotingLinkAsync(ev.ShortCode, "not a token", None));
        Assert.IsNull(await _h.Service.ResolveVotingLinkAsync("NOSUCH00", token, None));
        Assert.IsNotNull(await _h.Service.ResolveVotingLinkAsync($"[sched:{ev.ShortCode.ToLowerInvariant()}]", token, None),
            "the code is normalized as elsewhere");
        Assert.IsNotNull(await _h.Service.ResolveVotingLinkAsync(ev.Id.ToString(), token, None));
    }

    [TestMethod]
    public async Task A_voting_link_never_makes_the_holder_the_organizer()
    {
        var ev = await _h.CreateAsync();
        var caller = await _h.Service.ResolveVotingLinkAsync(ev.ShortCode, await TokenOfAsync("bob@example.com"), None);

        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => _h.Service.CancelAsync(caller!, ev.ShortCode, null, None));
    }

    // ── Unsubscribing ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task Unsubscribing_suppresses_the_address_once_and_can_be_undone()
    {
        var subscriptions = new SubscriptionService(_h.Db, _h.Clock);

        await subscriptions.UnsubscribeAsync(" Bob@Example.com ", None);
        await subscriptions.UnsubscribeAsync("bob@example.com", None);

        Assert.IsTrue(await subscriptions.IsSuppressedAsync("bob@example.com", None));
        await using (var db = await _h.Db.CreateDbContextAsync())
            Assert.AreEqual(SuppressionReason.Unsubscribed, (await db.Suppressions.SingleAsync()).Reason);
        Assert.AreEqual("email.unsubscribed", (await _h.AuditAsync()).Single().Action);

        await subscriptions.ResubscribeAsync("bob@example.com", None);
        Assert.IsFalse(await subscriptions.IsSuppressedAsync("bob@example.com", None));
        Assert.AreEqual("email.resubscribed", (await _h.AuditAsync()).Last().Action);
    }

    [TestMethod]
    public async Task A_bounced_address_cant_be_resubscribed_from_a_link()
    {
        var subscriptions = new SubscriptionService(_h.Db, _h.Clock);
        await _h.WithDbAsync(db =>
        {
            db.Suppressions.Add(new EmailSuppression { Email = "bob@example.com", Reason = SuppressionReason.Bounced, At = _h.Clock.GetCurrentInstant() });
            return Task.CompletedTask;
        });

        await Assert.ThrowsExactlyAsync<SchedulingValidationException>(() => subscriptions.ResubscribeAsync("bob@example.com", None));
        Assert.IsTrue(await subscriptions.IsSuppressedAsync("bob@example.com", None));
    }

    [TestMethod]
    public async Task The_organizer_is_told_which_invitees_wont_get_the_email()
    {
        var subscriptions = new SubscriptionService(_h.Db, _h.Clock);
        await subscriptions.UnsubscribeAsync("carol@example.com", None);

        var ev = await _h.CreateAsync();

        Assert.IsNotNull(ev.Notices);
        StringAssert.Contains(ev.Notices.Single(), "carol@example.com has asked this server not to email them");
        Assert.HasCount(3, ev.Participants, "Carol is still invited and can vote if she signs in");
    }

    private async Task<string> TokenOfAsync(string email, Guid? eventId = null)
    {
        await using var db = await _h.Db.CreateDbContextAsync();
        var participant = await db.Participants.FirstAsync(p => p.Email == email && (eventId == null || p.EventId == eventId));
        return _h.Links.CreateToken(participant.Id, participant.TokenNonce);
    }
}
