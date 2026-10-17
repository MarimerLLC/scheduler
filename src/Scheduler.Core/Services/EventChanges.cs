namespace Scheduler.Core.Services;

/// <summary>
/// Tells open pages that an event changed (a vote, an organizer edit, a job), so the organizer's dashboard and
/// event page update live. In-process only: with more than one replica this would need a backplane.
/// See docs/design.md, "Web UI".
/// </summary>
public interface IEventChanges
{
    /// <summary>Raised by the services after a change to the event is saved.</summary>
    void Publish(Guid eventId);

    /// <summary>Calls <paramref name="handler"/> for every change until the result is disposed.</summary>
    IDisposable Subscribe(Action<Guid> handler);
}

/// <summary>The in-process <see cref="IEventChanges"/>. A failing handler doesn't stop the others or the publisher.</summary>
public sealed class EventChanges : IEventChanges
{
    private readonly Lock _lock = new();
    private Action<Guid>[] _handlers = [];

    public void Publish(Guid eventId)
    {
        foreach (var handler in Volatile.Read(ref _handlers))
        {
            try
            {
                handler(eventId);
            }
            catch
            {
                // A page that went away mid-update; the change itself is already saved.
            }
        }
    }

    public IDisposable Subscribe(Action<Guid> handler)
    {
        lock (_lock)
            _handlers = [.. _handlers, handler];
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<Guid> handler)
    {
        lock (_lock)
            _handlers = _handlers.Where(h => h != handler).ToArray();
    }

    private sealed class Subscription(EventChanges owner, Action<Guid> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unsubscribe(handler);
        }
    }
}
