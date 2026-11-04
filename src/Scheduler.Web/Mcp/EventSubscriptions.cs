using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;
using Scheduler.Web.Identity;
using Signal = System.Threading.Channels.Channel;
using BoundedChannelFullMode = System.Threading.Channels.BoundedChannelFullMode;
using BoundedChannelOptions = System.Threading.Channels.BoundedChannelOptions;

namespace Scheduler.Web.Mcp;

/// <summary>
/// The <c>subscriptions/listen</c> handler (SEP-2575, protocol 2026-07-28): pushes
/// <c>notifications/resources/updated</c> for the <see cref="EventResources"/> a caller may read, over the listen
/// request's own response stream, which is the only channel the stateless transport has. The subscription is
/// the open stream; nothing is stored. See docs/design.md, "Push updates".
/// </summary>
internal static class EventSubscriptions
{
    public static async ValueTask<EmptyResult> ListenAsync(RequestContext<SubscriptionsListenRequestParams> request, CancellationToken cancellationToken)
    {
        var services = request.Services ?? throw new InvalidOperationException("The listen request has no services.");
        var options = services.GetRequiredService<IOptions<SubscriptionOptions>>().Value;
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EventSubscriptions));
        var caller = CallerResolver.FromPrincipal(request.User, Channel.Mcp) ?? throw new McpException(McpCaller.NotSignedIn);

        var requested = (request.Params?.Notifications?.ResourceSubscriptions ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (requested.Count > options.MaxUrisPerStream)
            throw new McpException($"A listen request can watch at most {options.MaxUrisPerStream} resources; this one asked for {requested.Count}.");
        var resources = requested
            .Select(uri => (Uri: uri, Resource: EventResourceUri.TryParse(uri)))
            .Where(r => r.Resource is not null)
            .ToList();

        // Every stream holds a slot, reserved before any reads: one of the caller's watching slots if it names an
        // event, else one of the larger pool of idle slots (a client that asked only for list_changed, as Claude
        // Code does, still holds its stream open).
        var streams = services.GetRequiredService<ListenStreams>();
        var slot = Reserve(streams, caller, resources.Count > 0 ? StreamKind.Watching : StreamKind.Idle, options);
        try
        {
            // Subscribe before the first reads, so a change saved while they run isn't missed.
            var pending = new PendingChanges();
            using var subscription = services.GetRequiredService<IEventChanges>().Subscribe(pending.Add);

            var scopes = services.GetRequiredService<IServiceScopeFactory>();
            var watches = new List<Watch>();
            foreach (var (uri, resource) in resources)
            {
                // Granted only if the caller may read it now; an unreadable event isn't acknowledged at all.
                if (await ReadStatusAsync(scopes, caller, resource!.EventRef, cancellationToken) is { } status)
                    watches.Add(new Watch(uri, status.Id, resource.IsStatus) { Marker = status.Marker });
            }
            pending.WatchOnly(watches.Select(w => w.EventId).ToHashSet());
            if (watches.Count == 0 && slot.Kind == StreamKind.Watching)
            {
                // Nothing it named was readable, so it watches nothing after all.
                slot.Dispose();
                slot = Reserve(streams, caller, StreamKind.Idle, options);
            }

            var subscriptionId = JsonSerializer.SerializeToNode(request.JsonRpcRequest.Id, McpJsonUtilities.DefaultOptions);
            // Only resource subscriptions are honored; list_changed requests are left out of the acknowledgement.
            // The SDK's acknowledgement type has no _meta, so the tag is added to its JSON.
            var ack = JsonSerializer.SerializeToNode(
                new SubscriptionsAcknowledgedNotificationParams
                {
                    Notifications = new SubscriptionsListenNotifications { ResourceSubscriptions = watches.Select(w => w.Uri).ToList() },
                },
                McpJsonUtilities.DefaultOptions)!.AsObject();
            ack["_meta"] = Tag(subscriptionId);
            await request.Server.SendNotificationAsync(
                NotificationMethods.SubscriptionsAcknowledgedNotification, ack, cancellationToken: cancellationToken);
            log.LogInformation("Listen stream opened with {Granted} of {Requested} resources", watches.Count, requested.Count);

            // Ending the stream after a while, rather than sending keepalives, keeps proxies from cutting it while
            // idle and makes the client reconnect with a fresh token. A stream with nothing (left) to watch stays
            // open until then too: clients reconnect as soon as a stream ends, so ending it early only makes them retry.
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(TimeSpan.FromMinutes(options.StreamMinutes));
            var reason = "failed";
            try
            {
                while (true)
                {
                    var changed = await pending.TakeAsync(lifetime.Token);
                    foreach (var eventId in changed)
                    {
                        var affected = watches.Where(w => w.Active && w.EventId == eventId).ToList();
                        if (affected.Count == 0)
                            continue;
                        var status = await ReadStatusAsync(scopes, caller, eventId.ToString(), lifetime.Token);
                        foreach (var watch in affected)
                        {
                            if (status is null)
                            {
                                // Access lost or the event is gone: nothing more to say about it on this stream.
                                watch.Active = false;
                                continue;
                            }
                            if (watch.IsStatus)
                            {
                                if (status.Marker == watch.Marker)
                                    continue;
                                watch.Marker = status.Marker;
                            }
                            await request.Server.SendNotificationAsync(
                                NotificationMethods.ResourceUpdatedNotification,
                                new ResourceUpdatedNotificationParams { Uri = watch.Uri, Meta = Tag(subscriptionId) },
                                cancellationToken: lifetime.Token);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The stream's lifetime is up; the client reconnects.
                reason = "lifetime ended";
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                    reason = "client went away";
                log.LogInformation("Listen stream closed ({Reason})", reason);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new EmptyResult();
        }
        finally
        {
            slot.Dispose();
        }
    }

    private static ListenStreams.Slot Reserve(ListenStreams streams, CallerIdentity caller, StreamKind kind, SubscriptionOptions options) =>
        kind == StreamKind.Watching
            ? streams.TryOpen(caller, kind, options.MaxStreamsPerCaller)
                ?? throw new McpException($"You already have {options.MaxStreamsPerCaller} listen streams open. Close one, or watch more resources on one stream.")
            : streams.TryOpen(caller, kind, options.MaxIdleStreamsPerCaller)
                ?? throw new McpException($"You already have {options.MaxIdleStreamsPerCaller} listen streams open that watch nothing. Close one first.");

    /// <summary>The event's status as the caller may see it, or null if they may not (or it no longer exists).</summary>
    private static async Task<EventStatusView?> ReadStatusAsync(IServiceScopeFactory scopes, CallerIdentity caller, string eventRef, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            return await scope.ServiceProvider.GetRequiredService<ISchedulingService>().GetEventStatusAsync(caller, eventRef, cancellationToken);
        }
        catch (SchedulingException)
        {
            return null;
        }
    }

    private static JsonObject Tag(JsonNode? subscriptionId) => new() { [MetaKeys.SubscriptionId] = subscriptionId?.DeepClone() };

    private sealed record Watch(string Uri, Guid EventId, bool IsStatus)
    {
        public string? Marker { get; set; }
        public bool Active { get; set; } = true;
    }
}

/// <summary>
/// The events that changed since the stream last looked, one entry per event however often it changed, so a
/// busy event can't push out another's change. Before the watched events are known (while the first reads run)
/// every change is kept, up to <see cref="Limit"/>; past that, everything watched is treated as changed.
/// </summary>
internal sealed class PendingChanges
{
    public const int Limit = 256;

    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _changed = [];
    private HashSet<Guid>? _watched;
    private bool _overflowed;

    // Holds at most one wake-up: TakeAsync takes everything pending at once.
    private readonly System.Threading.Channels.Channel<bool> _wake = Signal.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Add(Guid eventId)
    {
        lock (_lock)
        {
            if (_watched is not null && !_watched.Contains(eventId))
                return;
            if (_changed.Count < Limit)
                _changed.Add(eventId);
            else
                _overflowed = true;
        }
        _wake.Writer.TryWrite(true);
    }

    /// <summary>From now on only changes to these events count; earlier changes to others are dropped.</summary>
    public void WatchOnly(HashSet<Guid> watched)
    {
        lock (_lock)
        {
            _watched = watched;
            if (_overflowed)
                _changed.UnionWith(watched);
            _changed.IntersectWith(watched);
            _overflowed = false;
        }
    }

    /// <summary>Waits for at least one change, then returns every pending one and clears them.</summary>
    public async Task<IReadOnlyCollection<Guid>> TakeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _wake.Reader.ReadAsync(cancellationToken);
            lock (_lock)
            {
                if (_overflowed && _watched is not null)
                    _changed.UnionWith(_watched);
                _overflowed = false;
                if (_changed.Count == 0)
                    continue;
                var taken = _changed.ToList();
                _changed.Clear();
                return taken;
            }
        }
    }
}

internal enum StreamKind
{
    /// <summary>Watches at least one event.</summary>
    Watching,

    /// <summary>Watches nothing; the client only holds it open.</summary>
    Idle,
}

/// <summary>
/// Counts each caller's open listen streams by <see cref="StreamKind"/>, so one caller can't hold an unbounded
/// number open.
/// </summary>
internal sealed class ListenStreams
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _open = [];

    /// <summary>Takes a slot for the caller; disposing it gives it back. Null when they already have <paramref name="max"/>.</summary>
    public Slot? TryOpen(CallerIdentity caller, StreamKind kind, int max)
    {
        var key = Key(caller, kind);
        lock (_lock)
        {
            var count = _open.GetValueOrDefault(key);
            if (count >= max)
                return null;
            _open[key] = count + 1;
        }
        return new Slot(this, key, kind);
    }

    /// <summary>The account, or the first verified address when the sign-in didn't resolve one.</summary>
    private static string Key(CallerIdentity caller, StreamKind kind) =>
        kind + ":" + (caller.UserId is { } id ? "account:" + id : "email:" + caller.VerifiedEmails[0]);

    private void Close(string key)
    {
        lock (_lock)
        {
            if (_open.GetValueOrDefault(key) <= 1)
                _open.Remove(key);
            else
                _open[key]--;
        }
    }

    public sealed class Slot(ListenStreams owner, string key, StreamKind kind) : IDisposable
    {
        private int _disposed;

        public StreamKind Kind { get; } = kind;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Close(key);
        }
    }
}
