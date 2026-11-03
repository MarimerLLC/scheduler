using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Scheduler.Web.Identity;

namespace Scheduler.Web.Tests;

/// <summary>
/// MRTR input requests through the real app and the SDK client: what create_event and cancel_event ask for when
/// something is missing, what they do with the answer, and that clients that can't be asked get the old behavior.
/// </summary>
[TestClass]
public sealed class McpInputRequestTests
{
    private const string Organizer = "rocky@example.com";

    private SchedulerApp _app = null!;
    private readonly List<ElicitRequestParams> _asked = [];

    [TestInitialize]
    public void Start() => _app = new SchedulerApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task A_cancel_without_a_reason_asks_for_one_and_quotes_the_answer()
    {
        await using var client = await ConnectAsync(Accept(("reason", "The project was shelved")));
        var shortCode = await CreateAsync(client, "Quarterly planning with finance", "A long enough title.");

        var result = await client.CallToolAsync("cancel_event", new Dictionary<string, object?> { ["eventId"] = shortCode, ["reason"] = "" });

        Assert.IsFalse(result.IsError ?? false, Text(result));
        StringAssert.Contains(Text(result), "\"state\":\"cancelled\"");
        var question = _asked.Single();
        StringAssert.Contains(question.Message, "Quarterly planning with finance");
        CollectionAssert.AreEqual(new[] { "reason" }, question.RequestedSchema!.Required!.ToArray());
        Assert.IsTrue((await _app.OutboxAsync()).Any(e => e.TextBody.Contains("The project was shelved")));
    }

    [TestMethod]
    public async Task Declining_to_give_a_reason_leaves_the_event_open()
    {
        await using var client = await ConnectAsync(Decline);
        var shortCode = await CreateAsync(client, "Quarterly planning with finance", "A long enough title.");

        var result = await client.CallToolAsync("cancel_event", new Dictionary<string, object?> { ["eventId"] = shortCode, ["reason"] = " " });

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(Text(result), "not cancelled");
        StringAssert.Contains(await GetAsync(client, shortCode), "\"state\":\"open\"");
    }

    [TestMethod]
    public async Task A_cancel_by_someone_who_does_not_organize_the_event_asks_nothing()
    {
        string shortCode;
        await using (var organizer = await ConnectAsync(Decline))
            shortCode = await CreateAsync(organizer, "Quarterly planning with finance", "A long enough title.");
        await using var guest = await ConnectAsync(Accept(("reason", "nope")), "bob@example.com");

        var result = await guest.CallToolAsync("cancel_event", new Dictionary<string, object?> { ["eventId"] = shortCode, ["reason"] = "" });

        Assert.IsTrue(result.IsError ?? false);
        Assert.IsEmpty(_asked);
    }

    [TestMethod]
    public async Task A_client_without_mrtr_gets_the_reason_required_error()
    {
        await using var client = await ConnectAsync(Accept(("reason", "unused")), protocolVersion: "2025-11-25");
        var shortCode = await CreateAsync(client, "Quarterly planning with finance", "A long enough title.");

        var result = await client.CallToolAsync("cancel_event", new Dictionary<string, object?> { ["eventId"] = shortCode, ["reason"] = "" });

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(Text(result), "A reason is required.");
        Assert.IsEmpty(_asked);
    }

    [TestMethod]
    public async Task A_client_without_elicitation_gets_the_reason_required_error()
    {
        await using var client = await ConnectAsync(handler: null);
        var shortCode = await CreateAsync(client, "Quarterly planning with finance", "A long enough title.");

        var result = await client.CallToolAsync("cancel_event", new Dictionary<string, object?> { ["eventId"] = shortCode, ["reason"] = "" });

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(Text(result), "A reason is required.");
    }

    [TestMethod]
    public async Task Missing_invitees_times_and_zone_are_asked_for_in_one_form()
    {
        var (first, second) = (Future(7, 10), Future(8, 14));
        await using var client = await ConnectAsync(Accept(
            ("timeZone", "America/Chicago"),
            ("participants", "bob@example.com, carol@example.com"),
            ("slots", $"{first}, {second}")));

        var result = await client.CallToolAsync("create_event", new Dictionary<string, object?>
        {
            ["title"] = "Quarterly planning with finance",
            ["durationMinutes"] = 30,
            ["organizerTimeZone"] = "",
            ["participants"] = Array.Empty<object>(),
            ["slots"] = Array.Empty<object>(),
        });

        Assert.IsFalse(result.IsError ?? false, Text(result));
        var question = _asked.Single();
        CollectionAssert.AreEquivalent(new[] { "timeZone", "participants", "slots" }, question.RequestedSchema!.Required!.ToArray());
        Assert.IsFalse(question.RequestedSchema.Properties.ContainsKey("description"));
        using var ev = JsonDocument.Parse(Text(result));
        Assert.AreEqual("America/Chicago", ev.RootElement.GetProperty("organizerTimeZone").GetString());
        Assert.AreEqual(2, ev.RootElement.GetProperty("participants").GetArrayLength());
        Assert.AreEqual(2, ev.RootElement.GetProperty("slots").GetArrayLength());
    }

    [TestMethod]
    public async Task Declining_to_give_required_details_creates_nothing()
    {
        await using var client = await ConnectAsync(Decline);

        var result = await client.CallToolAsync("create_event", new Dictionary<string, object?>
        {
            ["title"] = "Quarterly planning with finance",
            ["durationMinutes"] = 30,
            ["organizerTimeZone"] = "America/Chicago",
            ["participants"] = Array.Empty<object>(),
            ["slots"] = new[] { new { start = Future(7, 10), timeZone = "America/Chicago" } },
        });

        Assert.IsTrue(result.IsError ?? false);
        StringAssert.Contains(Text(result), "didn't give who to invite");
        Assert.IsEmpty(await _app.OutboxAsync());
    }

    [TestMethod]
    public async Task A_short_title_without_a_description_offers_one_and_uses_the_answer()
    {
        await using var client = await ConnectAsync(Accept(("description", "Agree the Q1 hiring plan")));

        var json = await CreateAsync(client, "Sync", description: null, returnJson: true);

        var question = _asked.Single();
        Assert.IsTrue(question.RequestedSchema!.Properties.ContainsKey("description"));
        Assert.IsEmpty(question.RequestedSchema.Required ?? []);
        StringAssert.Contains(question.Message, "leave it blank to skip");
        StringAssert.Contains(json, "Agree the Q1 hiring plan");
    }

    [TestMethod]
    public async Task Skipping_the_description_creates_the_event_without_one()
    {
        await using var client = await ConnectAsync(Accept(("description", "")));

        var json = await CreateAsync(client, "Quick sync", description: null, returnJson: true);

        Assert.HasCount(1, _asked);
        Assert.DoesNotContain("\"description\"", json);
    }

    [TestMethod]
    public async Task Declining_the_description_still_creates_the_event()
    {
        await using var client = await ConnectAsync(Decline);

        var json = await CreateAsync(client, "Quick sync", description: null, returnJson: true);

        Assert.HasCount(1, _asked);
        StringAssert.Contains(json, "\"shortCode\"");
    }

    [TestMethod]
    public async Task A_descriptive_title_is_not_asked_for_a_description()
    {
        await using var client = await ConnectAsync(Decline);

        await CreateAsync(client, "Quarterly planning with finance", description: null);

        Assert.IsEmpty(_asked);
    }

    private Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> Accept(params (string Field, string Value)[] answers) =>
        (request, _) =>
        {
            _asked.Add(request!);
            return ValueTask.FromResult(new ElicitResult
            {
                Action = "accept",
                Content = answers.ToDictionary(a => a.Field, a => JsonSerializer.SerializeToElement(a.Value)),
            });
        };

    private ValueTask<ElicitResult> Decline(ElicitRequestParams? request, CancellationToken _)
    {
        _asked.Add(request!);
        return ValueTask.FromResult(new ElicitResult { Action = "decline" });
    }

    private async Task<McpClient> ConnectAsync(
        Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? handler,
        string email = Organizer,
        string? protocolVersion = null)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(SchedulerApp.Origin + "/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { [DevEmailHeader.HeaderName] = email },
            },
            _app.CreateClient());
        var options = new McpClientOptions { ProtocolVersion = protocolVersion };
        if (handler is not null)
            options.Handlers = new McpClientHandlers { ElicitationHandler = handler };
        return await McpClient.CreateAsync(transport, options);
    }

    /// <summary>Creates an event with every required field given; returns its shortCode, or the tool's JSON.</summary>
    private static async Task<string> CreateAsync(McpClient client, string title, string? description, bool returnJson = false)
    {
        var result = await client.CallToolAsync("create_event", new Dictionary<string, object?>
        {
            ["title"] = title,
            ["description"] = description,
            ["durationMinutes"] = 30,
            ["organizerTimeZone"] = "America/Chicago",
            ["participants"] = new[] { new { email = "bob@example.com" } },
            ["slots"] = new[] { new { start = Future(7, 10), timeZone = "America/Chicago" } },
        });
        Assert.IsFalse(result.IsError ?? false, Text(result));
        if (returnJson)
            return Text(result);
        using var ev = JsonDocument.Parse(Text(result));
        return ev.RootElement.GetProperty("shortCode").GetString()!;
    }

    private static async Task<string> GetAsync(McpClient client, string shortCode) =>
        Text(await client.CallToolAsync("get_event", new Dictionary<string, object?> { ["eventId"] = shortCode }));

    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;

    private static string Future(int days, int hour) =>
        DateTime.UtcNow.Date.AddDays(days).AddHours(hour).ToString("yyyy-MM-dd'T'HH:mm");
}
