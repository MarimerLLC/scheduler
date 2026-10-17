using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

/// <summary>Per-organizer limits on events and invitations over a rolling 24 hours.</summary>
[TestClass]
public sealed class OrganizerLimitTests
{
    private Harness _h = null!;

    [TestInitialize]
    public async Task InitializeAsync() => _h = await Harness.CreateAsync(options =>
    {
        options.MaxEventsPerDay = 2;
        options.MaxInvitationsPerDay = 5;
    });

    [TestMethod]
    public async Task An_organizer_creates_at_most_the_daily_number_of_events()
    {
        await _h.CreateAsync(OneGuest());
        _h.Clock.Advance(Duration.FromHours(1));
        await _h.CreateAsync(OneGuest());

        var error = await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => _h.CreateAsync(OneGuest()));
        StringAssert.Contains(error.Message, "created 2 events in the last 24 hours");
        Assert.HasCount(2, await _h.OutboxAsync(), "a refused event sends nothing");

        // The window rolls: a day after the first event, one more fits.
        _h.Clock.Advance(Duration.FromHours(23) + Duration.FromMinutes(1));
        await _h.CreateAsync(OneGuest());
        await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => _h.CreateAsync(OneGuest()));
    }

    [TestMethod]
    public async Task An_organizer_invites_at_most_the_daily_number_of_people()
    {
        await _h.CreateAsync(); // Bob, Carol and Dave

        var error = await Assert.ThrowsExactlyAsync<SchedulingForbiddenException>(() => _h.CreateAsync());
        StringAssert.Contains(error.Message, "would invite 3 people, and you've invited 3");

        await _h.CreateAsync(Harness.Request(participants: [new("erin@example.com"), new("frank@example.com")]));
        Assert.HasCount(5, await _h.OutboxAsync());
    }

    [TestMethod]
    public async Task Emails_to_people_already_invited_dont_count()
    {
        var ev = await _h.CreateAsync(Harness.Request(participants: [new("a@example.com"), new("b@example.com"), new("c@example.com"), new("d@example.com"), new("e@example.com")]));

        var more = await _h.Service.ProposeMoreSlotsAsync(Harness.Rocky, ev.ShortCode, [new("2026-11-06T10:00", "America/Chicago")], null, ev.Version, CancellationToken.None);

        Assert.HasCount(4, more.Slots);
        Assert.HasCount(10, await _h.OutboxAsync());
    }

    [TestMethod]
    public async Task Each_organizer_has_their_own_limits()
    {
        await _h.CreateAsync(OneGuest());
        await _h.CreateAsync(OneGuest());

        var ann = await _h.Service.CreateEventAsync(Harness.Caller("ann@marimer.llc"), OneGuest(), CancellationToken.None);

        Assert.AreEqual("ann@marimer.llc", ann.OrganizerEmail);
    }

    private static CreateEventRequest OneGuest() => Harness.Request(participants: [new("bob@example.com", "Bob")]);
}
