using Microsoft.EntityFrameworkCore;
using NodaTime;

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
}
