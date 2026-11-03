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
using ChangeQueue = System.Threading.Channels.Channel;
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
    /// <summary>Changes waiting to be handled per stream; older ones are dropped, since the next read sees them anyway.</summary>
    private const int Backlog = 64;

    public static async ValueTask<EmptyResult> ListenAsync(RequestContext<SubscriptionsListenRequestParams> request, CancellationToken cancellationToken)
    {
        var services = request.Services ?? throw new InvalidOperationException("The listen request has no services.");
        var options = services.GetRequiredService<IOptions<SubscriptionOptions>>().Value;
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EventSubscriptions));
        var caller = CallerResolver.FromPrincipal(request.User, Channel.Mcp) ?? throw new McpException(McpCaller.NotSignedIn);

        var requested = (request.Params?.Notifications?.ResourceSubscriptions ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (requested.Count > options.MaxUrisPerStream)
            throw new McpException($"A listen request can watch at most {options.MaxUrisPerStream} resources; this one asked for {requested.Count}.");

        using var slot = services.GetRequiredService<ListenStreams>().TryOpen(caller, options.MaxStreamsPerCaller)
            ?? throw new McpException($"You already have {options.MaxStreamsPerCaller} listen streams open. Close one, or watch more resources on one stream.");

        // Subscribe before the first reads, so a change saved while they run isn't missed. Until the watched
        // events are known every change is kept; after that only theirs.
        var changes = ChangeQueue.CreateBounded<Guid>(new BoundedChannelOptions(Backlog)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        HashSet<Guid>? watchedEvents = null;
        using var subscription = services.GetRequiredService<IEventChanges>().Subscribe(eventId =>
        {
            if (Volatile.Read(ref watchedEvents) is not { } watched || watched.Contains(eventId))
                changes.Writer.TryWrite(eventId);
        });

        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var watches = new List<Watch>();
        foreach (var uri in requested)
        {
            if (EventResourceUri.TryParse(uri) is not { } resource)
                continue;
            // Granted only if the caller may read it now; an unreadable event isn't acknowledged at all.
            if (await ReadStatusAsync(scopes, caller, resource.EventRef, cancellationToken) is { } status)
                watches.Add(new Watch(uri, status.Id, resource.IsStatus) { Marker = status.Marker });
        }
        Volatile.Write(ref watchedEvents, watches.Select(w => w.EventId).ToHashSet());

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
        // idle and makes the client reconnect with a fresh token.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(options.StreamMinutes));
        try
        {
            while (watches.Any(w => w.Active) && await changes.Reader.WaitToReadAsync(lifetime.Token))
            {
                var changed = new HashSet<Guid>();
                while (changes.Reader.TryRead(out var eventId))
                    changed.Add(eventId);

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
        }
        finally
        {
            log.LogInformation("Listen stream closed ({Reason})",
                cancellationToken.IsCancellationRequested ? "client went away" : "lifetime ended");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new EmptyResult();
    }

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

/// <summary>Counts each caller's open listen streams, so one caller can't hold an unbounded number open.</summary>
internal sealed class ListenStreams
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _open = [];

    /// <summary>Takes a slot for the caller; disposing it gives it back. Null when they already have <paramref name="max"/>.</summary>
    public IDisposable? TryOpen(CallerIdentity caller, int max)
    {
        var key = Key(caller);
        lock (_lock)
        {
            var count = _open.GetValueOrDefault(key);
            if (count >= max)
                return null;
            _open[key] = count + 1;
        }
        return new Slot(this, key);
    }

    /// <summary>The account, or the first verified address when the sign-in didn't resolve one.</summary>
    private static string Key(CallerIdentity caller) =>
        caller.UserId is { } id ? "account:" + id : "email:" + caller.VerifiedEmails[0];

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

    private sealed class Slot(ListenStreams owner, string key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Close(key);
        }
    }
}
