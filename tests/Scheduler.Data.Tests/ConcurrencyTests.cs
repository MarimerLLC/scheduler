using Microsoft.EntityFrameworkCore;
using NodaTime;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

[TestClass]
public sealed class ConcurrencyTests
{
    [TestMethod]
    public async Task TheDatabaseRejectsAWriteThatRacedAnother()
    {
        var h = await Harness.CreateAsync();
        await h.CreateAsync();

        // Two organizer sessions load the same event...
        await using var first = await h.Db.CreateDbContextAsync();
        await using var second = await h.Db.CreateDbContextAsync();
        var a = await first.Events.SingleAsync();
        var b = await second.Events.SingleAsync();

        // ...the first saves...
        a.UpdatedAt += Duration.FromSeconds(1);
        await first.SaveChangesAsync();

        // ...so the second, still holding the old xmin, is refused.
        b.UpdatedAt += Duration.FromSeconds(2);
        await Assert.ThrowsExactlyAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [TestMethod]
    public async Task ANewOrganizersConcurrentFirstEventsAllLandOnOneAccount()
    {
        var h = await Harness.CreateAsync();
        var newcomer = Harness.Caller("newcomer@marimer.llc");

        // Several tabs (or agents) create the organizer's first events at once...
        var created = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => Task.Run(() =>
            h.Service.CreateEventAsync(newcomer, Harness.Request(title: $"Planning {i}"), CancellationToken.None))));

        // ...and every one of them is theirs to see and manage, on the one account that owns the address.
        var organizing = await h.Service.ListMyEventsAsync(newcomer, EventListFilter.Organizing, includePast: false, CancellationToken.None);
        CollectionAssert.AreEquivalent(created.Select(e => e.Id).ToList(), organizing.Select(e => e.Id).ToList());
        foreach (var ev in created)
            Assert.IsTrue((await h.GetAsync(newcomer, ev)).IsOrganizer);

        await using var db = await h.Db.CreateDbContextAsync();
        Assert.AreEqual(1, await db.Users.CountAsync());
    }

    [TestMethod]
    public async Task APersonsFirstTokenAndFirstEventsAtOnceShareOneAccount()
    {
        var h = await Harness.CreateAsync();
        var newcomer = new CallerIdentity(null, ["newcomer@marimer.llc"], Channel.Web);

        // One tab makes a personal access token while others create events...
        var token = Task.Run(() => h.AccessTokens.CreateAsync(newcomer, "RockBot", 30, CancellationToken.None));
        var events = Task.WhenAll(Enumerable.Range(1, 4).Select(i => Task.Run(() =>
            h.Service.CreateEventAsync(newcomer, Harness.Request(title: $"Planning {i}"), CancellationToken.None))));
        await Task.WhenAll(token, events);

        // ...and the token and the events all belong to the one account.
        await using var db = await h.Db.CreateDbContextAsync();
        var account = await db.Users.SingleAsync();
        Assert.AreEqual(account.Id, (await db.PersonalAccessTokens.SingleAsync()).UserId);
        Assert.IsTrue(await db.Events.AllAsync(e => e.OrganizerId == account.Id));
        Assert.AreEqual(4, await db.Events.CountAsync());
    }
}
