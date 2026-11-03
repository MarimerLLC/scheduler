using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Scheduler.Web.Identity;

namespace Scheduler.Web.Tests;

/// <summary>
/// Events as resources and push updates over <c>subscriptions/listen</c>, through the real app and the SDK
/// client (docs/design.md, "Push updates").
/// </summary>
[TestClass]
public sealed class McpSubscriptionTests
{
    private const string Organizer = "rocky@example.com";
    private const string Bob = "bob@example.com";
    private const string Carol = "carol@example.com";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private SchedulerApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SchedulerApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task A_vote_is_pushed_to_whoever_watches_the_event()
    {
        await using var organizer = await ConnectAsync(Organizer);
        await using var bob = await ConnectAsync(Bob);
        var ev = await CreateAsync(organizer);
        var uri = EventUri(ev.ShortCode);

        await using var listener = await Listener.OpenAsync(organizer, uri);
        await VoteAsync(bob, ev, "yes");
        var update = await listener.NextAsync();

        CollectionAssert.AreEqual(new[] { uri }, listener.Granted);
        Assert.AreEqual(NotificationMethods.ResourceUpdatedNotification, update.Method);
        Assert.AreEqual(uri, update.Params!["uri"]!.GetValue<string>());
        Assert.AreEqual(listener.Id, update.Params["_meta"]![MetaKeys.SubscriptionId]!.GetValue<string>());
    }

    [TestMethod]
    public async Task The_status_resource_is_pushed_only_at_milestones()
    {
        await using var organizer = await ConnectAsync(Organizer);
        await using var bob = await ConnectAsync(Bob);
        await using var carol = await ConnectAsync(Carol);
        var ev = await CreateAsync(organizer, Bob, Carol);
        var (status, eventUri) = (StatusUri(ev.ShortCode), EventUri(ev.ShortCode));

        // The status URI comes first, so for one change its notification (if any) comes before the event's.
        await using var listener = await Listener.OpenAsync(organizer, status, eventUri);
        await VoteAsync(bob, ev, "yes");
        var first = await listener.NextAsync();
        await VoteAsync(carol, ev, "preferred");
        var second = await listener.NextAsync();
        var third = await listener.NextAsync();

        Assert.AreEqual(eventUri, UriOf(first), "Bob's vote leaves Carol to answer: no milestone");
        Assert.AreEqual(status, UriOf(second), "Carol's vote completes the required answers");
        Assert.AreEqual(eventUri, UriOf(third));
    }

    [TestMethod]
    public async Task An_event_the_caller_cannot_read_is_not_acknowledged_or_pushed()
    {
        await using var organizer = await ConnectAsync(Organizer);
        await using var bob = await ConnectAsync(Bob);
        await using var carol = await ConnectAsync(Carol);
        var invited = await CreateAsync(organizer, Bob);
        var notInvited = await CreateAsync(organizer, Carol);

        await using var listener = await Listener.OpenAsync(bob,
            EventUri(notInvited.ShortCode), "scheduler://events/NOSUCHEV", "https://example.com/elsewhere", EventUri(invited.ShortCode));
        await VoteAsync(carol, notInvited, "yes");
        await VoteAsync(bob, invited, "yes");
        var update = await listener.NextAsync();

        CollectionAssert.AreEqual(new[] { EventUri(invited.ShortCode) }, listener.Granted);
        Assert.AreEqual(EventUri(invited.ShortCode), UriOf(update), "nothing about the event Bob isn't invited to came first");
    }

    [TestMethod]
    public async Task A_stream_that_watches_nothing_stays_open_without_taking_a_slot()
    {
        await using var organizer = await ConnectAsync(Organizer);
        await using var bob = await ConnectAsync(Bob);
        var notInvited = await CreateAsync(organizer, Carol);
        var invited = await CreateAsync(organizer, Bob);
        var empty = new List<Listener>();
        var watching = new List<Listener>();
        try
        {
            // Bob can't read the first event, so these watch nothing, like a client that asked only for list_changed.
            for (var i = 0; i < 6; i++)
                empty.Add(await Listener.OpenAsync(bob, EventUri(notInvited.ShortCode)));
            for (var i = 0; i < 5; i++)
                watching.Add(await Listener.OpenAsync(bob, EventUri(invited.ShortCode)));
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.IsTrue(empty.All(l => l.Granted.Length == 0));
            Assert.IsFalse(empty.Any(l => l.Completion.IsCompleted), "a stream with nothing to watch waits out its lifetime");
        }
        finally
        {
            foreach (var listener in empty.Concat(watching))
                await listener.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task A_client_on_an_older_protocol_gets_method_not_found_and_tools_still_work()
    {
        await using var organizer = await ConnectAsync(Organizer, protocolVersion: "2025-11-25");
        var ev = await CreateAsync(organizer);

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => Listener.OpenAsync(organizer, EventUri(ev.ShortCode)));

        Assert.AreEqual(McpErrorCode.MethodNotFound, error.ErrorCode);
        var access = await organizer.CallToolAsync("get_my_access");
        Assert.IsFalse(access.IsError ?? false);
    }

    [TestMethod]
    public async Task A_caller_can_hold_five_streams_and_open_another_once_one_closes()
    {
        await using var organizer = await ConnectAsync(Organizer);
        var ev = await CreateAsync(organizer);
        var listeners = new List<Listener>();
        try
        {
            for (var i = 0; i < 5; i++)
                listeners.Add(await Listener.OpenAsync(organizer, EventUri(ev.ShortCode)));

            var refused = await Assert.ThrowsAsync<McpProtocolException>(() => Listener.OpenAsync(organizer, EventUri(ev.ShortCode)));
            StringAssert.Contains(refused.Message, "5 listen streams");

            await listeners[0].DisposeAsync();
            listeners.Add(await RetryAsync(() => Listener.OpenAsync(organizer, EventUri(ev.ShortCode))));
        }
        finally
        {
            foreach (var listener in listeners)
                await listener.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task A_stream_may_watch_at_most_fifty_resources()
    {
        await using var organizer = await ConnectAsync(Organizer);
        var uris = Enumerable.Range(0, 51).Select(i => EventUri($"CODE{i:D4}")).ToArray();

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => Listener.OpenAsync(organizer, uris));

        StringAssert.Contains(error.Message, "at most 50");
    }

    [TestMethod]
    public async Task The_server_ends_a_stream_when_its_lifetime_is_up()
    {
        await _app.DisposeAsync();
        _app = new SchedulerApp(("Scheduler:Subscriptions:StreamMinutes", "0.02"));
        await using var organizer = await ConnectAsync(Organizer);
        var ev = await CreateAsync(organizer);

        await using var watching = await Listener.OpenAsync(organizer, EventUri(ev.ShortCode));
        await using var empty = await Listener.OpenAsync(organizer);

        Assert.IsNotNull(await watching.Completion.WaitAsync(Patience));
        Assert.IsNotNull(await empty.Completion.WaitAsync(Patience));
    }

    [TestMethod]
    public async Task Both_resources_can_be_read_and_their_templates_are_listed()
    {
        await using var organizer = await ConnectAsync(Organizer);
        var ev = await CreateAsync(organizer);

        var templates = await organizer.ListResourceTemplatesAsync();
        var eventRead = await organizer.ReadResourceAsync(EventUri(ev.ShortCode));
        var statusRead = await organizer.ReadResourceAsync(StatusUri(ev.ShortCode));
        var getEvent = await organizer.CallToolAsync("get_event", new Dictionary<string, object?> { ["eventId"] = ev.ShortCode });

        CollectionAssert.IsSubsetOf(
            new[] { "scheduler://events/{shortCode}", "scheduler://events/{shortCode}/status" },
            templates.Select(t => t.UriTemplate).ToArray());
        Assert.IsTrue(organizer.ServerCapabilities.Resources?.Subscribe);
        var eventText = (TextResourceContents)eventRead.Contents.Single();
        Assert.AreEqual("application/json", eventText.MimeType);
        Assert.AreEqual(((TextContentBlock)getEvent.Content[0]).Text, eventText.Text);
        using var status = JsonDocument.Parse(((TextResourceContents)statusRead.Contents.Single()).Text);
        Assert.AreEqual(ev.ShortCode, status.RootElement.GetProperty("shortCode").GetString());
        Assert.AreEqual("open", status.RootElement.GetProperty("state").GetString());
        Assert.IsFalse(status.RootElement.GetProperty("everyoneResponded").GetBoolean());
        Assert.AreEqual(16, status.RootElement.GetProperty("marker").GetString()!.Length);
    }

    [TestMethod]
    public async Task Someone_not_invited_cannot_read_the_resources()
    {
        await using var organizer = await ConnectAsync(Organizer);
        await using var bob = await ConnectAsync(Bob);
        var ev = await CreateAsync(organizer, Carol);

        await Assert.ThrowsAsync<McpException>(() => bob.ReadResourceAsync(EventUri(ev.ShortCode)).AsTask());
        await Assert.ThrowsAsync<McpException>(() => bob.ReadResourceAsync(StatusUri(ev.ShortCode)).AsTask());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed record CreatedEvent(string ShortCode, string SlotId);

    private static string EventUri(string shortCode) => $"scheduler://events/{shortCode}";

    private static string StatusUri(string shortCode) => $"scheduler://events/{shortCode}/status";

    private static string UriOf(JsonRpcNotification notification) => notification.Params!["uri"]!.GetValue<string>();

    private async Task<McpClient> ConnectAsync(string email, string? protocolVersion = null)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(SchedulerApp.Origin + "/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { [DevEmailHeader.HeaderName] = email },
            },
            _app.CreateClient());
        return await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocolVersion });
    }

    /// <summary>An event with one slot, organized by the client's caller, inviting <paramref name="participants"/> (Bob by default).</summary>
    private static async Task<CreatedEvent> CreateAsync(McpClient organizer, params string[] participants)
    {
        var result = await organizer.CallToolAsync("create_event", new Dictionary<string, object?>
        {
            ["title"] = "Quarterly planning with finance",
            ["description"] = "Agree the plan.",
            ["durationMinutes"] = 30,
            ["organizerTimeZone"] = "America/Chicago",
            ["participants"] = (participants.Length == 0 ? new[] { Bob } : participants).Select(email => new { email }).ToArray(),
            ["slots"] = new[] { new { start = DateTime.UtcNow.Date.AddDays(7).AddHours(10).ToString("yyyy-MM-dd'T'HH:mm"), timeZone = "America/Chicago" } },
        });
        var text = ((TextContentBlock)result.Content[0]).Text;
        Assert.IsFalse(result.IsError ?? false, text);
        using var ev = JsonDocument.Parse(text);
        return new CreatedEvent(
            ev.RootElement.GetProperty("shortCode").GetString()!,
            ev.RootElement.GetProperty("slots")[0].GetProperty("slotId").GetString()!);
    }

    private static async Task VoteAsync(McpClient voter, CreatedEvent ev, string vote)
    {
        var result = await voter.CallToolAsync("vote", new Dictionary<string, object?>
        {
            ["eventId"] = ev.ShortCode,
            ["votes"] = new[] { new { slotId = ev.SlotId, vote } },
        });
        Assert.IsFalse(result.IsError ?? false, ((TextContentBlock)result.Content[0]).Text);
    }

    /// <summary>Retries while the server still counts a stream the client just closed.</summary>
    private static async Task<T> RetryAsync<T>(Func<Task<T>> action)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            try
            {
                return await action();
            }
            catch (McpProtocolException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>
    /// One <c>subscriptions/listen</c> request, held open. The SDK client has no helper for it, so this sends the
    /// raw request and collects the notifications tagged with its id.
    /// </summary>
    private sealed class Listener : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly Channel<JsonRpcNotification> _updates = Channel.CreateUnbounded<JsonRpcNotification>();
        private readonly TaskCompletionSource<string[]> _ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IAsyncDisposable> _handlers = [];

        private Listener() { }

        public string Id { get; } = "listen-" + Guid.NewGuid().ToString("N");

        public string[] Granted { get; private set; } = [];

        public Task<EmptyResult> Completion { get; private set; } = null!;

        public static async Task<Listener> OpenAsync(McpClient client, params string[] uris)
        {
            var listener = new Listener();
            listener._handlers.Add(client.RegisterNotificationHandler(NotificationMethods.SubscriptionsAcknowledgedNotification, (n, _) =>
            {
                if (listener.IsMine(n))
                {
                    var granted = n.Params?["notifications"]?["resourceSubscriptions"]?.AsArray().Select(u => u!.GetValue<string>()).ToArray() ?? [];
                    listener._ack.TrySetResult(granted);
                }
                return ValueTask.CompletedTask;
            }));
            listener._handlers.Add(client.RegisterNotificationHandler(NotificationMethods.ResourceUpdatedNotification, (n, _) =>
            {
                if (listener.IsMine(n))
                    listener._updates.Writer.TryWrite(n);
                return ValueTask.CompletedTask;
            }));

            listener.Completion = client.SendRequestAsync<SubscriptionsListenRequestParams, EmptyResult>(
                RequestMethods.SubscriptionsListen,
                new SubscriptionsListenRequestParams { Notifications = new SubscriptionsListenNotifications { ResourceSubscriptions = uris } },
                requestId: new RequestId(listener.Id),
                cancellationToken: listener._cancel.Token).AsTask();

            var first = await Task.WhenAny(listener._ack.Task, listener.Completion).WaitAsync(Patience);
            if (first == listener.Completion && !listener._ack.Task.IsCompleted)
            {
                await listener.DisposeAsync();
                await listener.Completion; // throws the server's error
                Assert.Fail("The stream ended without an acknowledgement.");
            }
            listener.Granted = await listener._ack.Task;
            return listener;
        }

        public async Task<JsonRpcNotification> NextAsync() => await _updates.Reader.ReadAsync().AsTask().WaitAsync(Patience);

        private bool IsMine(JsonRpcNotification notification) =>
            notification.Params?["_meta"]?[MetaKeys.SubscriptionId]?.GetValue<string>() == Id;

        public async ValueTask DisposeAsync()
        {
            await _cancel.CancelAsync();
            foreach (var handler in _handlers)
                await handler.DisposeAsync();
            _handlers.Clear();
            try
            {
                await Completion;
            }
            catch (Exception)
            {
                // Cancelled, or the error OpenAsync already reported.
            }
        }
    }
}
