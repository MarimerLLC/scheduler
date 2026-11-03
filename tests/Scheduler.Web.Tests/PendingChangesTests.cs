using Scheduler.Web.Mcp;

namespace Scheduler.Web.Tests;

/// <summary>The set of changed events a listen stream hasn't handled yet.</summary>
[TestClass]
public sealed class PendingChangesTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [TestMethod]
    public async Task Repeated_changes_to_one_event_are_one_entry_and_others_are_kept()
    {
        var pending = new PendingChanges();
        pending.WatchOnly([A, B]);

        pending.Add(A);
        for (var i = 0; i < 1000; i++)
            pending.Add(B);
        var taken = await pending.TakeAsync(CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { A, B }, taken.ToArray());
    }

    [TestMethod]
    public async Task Changes_to_unwatched_events_are_dropped()
    {
        var pending = new PendingChanges();
        pending.Add(Guid.NewGuid());
        pending.WatchOnly([A]);
        pending.Add(Guid.NewGuid());
        pending.Add(A);

        CollectionAssert.AreEqual(new[] { A }, (await pending.TakeAsync(CancellationToken.None)).ToArray());
    }

    [TestMethod]
    public async Task An_overflow_before_the_watched_events_are_known_counts_as_a_change_to_each()
    {
        var pending = new PendingChanges();
        for (var i = 0; i <= PendingChanges.Limit; i++)
            pending.Add(Guid.NewGuid());
        pending.WatchOnly([A, B]);

        CollectionAssert.AreEquivalent(new[] { A, B }, (await pending.TakeAsync(CancellationToken.None)).ToArray());
    }

    [TestMethod]
    public async Task Taking_waits_for_a_change()
    {
        var pending = new PendingChanges();
        pending.WatchOnly([A]);
        var take = pending.TakeAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.IsFalse(take.IsCompleted);

        pending.Add(A);

        CollectionAssert.AreEqual(new[] { A }, (await take.WaitAsync(TimeSpan.FromSeconds(5))).ToArray());
    }
}
