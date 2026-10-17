using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit.Security;
using Microsoft.Extensions.Options;
using Scheduler.Core.Email;
using Scheduler.Web.Email;

namespace Scheduler.Web.Tests;

/// <summary>
/// The production SMTP sender against a real SMTP server: Mailpit in a container, requiring a login, and refusing
/// every recipient outside <c>ok.example</c> the way a relay refuses an unknown mailbox.
/// </summary>
[TestClass]
public sealed class SmtpEmailSenderTests
{
    private static IContainer _mailpit = null!;

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _mailpit = new ContainerBuilder("axllent/mailpit:v1.31.2")
            .WithCommand("--smtp-allowed-recipients", @"@ok\.example$", "--smtp-auth-accept-any", "--smtp-auth-allow-insecure")
            .WithPortBinding(1025, assignRandomHostPort: true)
            .WithPortBinding(8025, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/livez")))
            .Build();
        await _mailpit.StartAsync();
    }

    [ClassCleanup]
    public static async Task StopAsync() => await _mailpit.DisposeAsync();

    [TestMethod]
    public async Task A_message_goes_out_with_both_bodies_reply_to_and_one_click_unsubscribe()
    {
        var id = await Sender().SendAsync(new OutboundEmail(
            "bob@ok.example", "rocky@example.com", "[sched:7KQ2M9XD] Rocky asks: Planning",
            "Plain text", "<p>HTML</p>", "https://scheduler.example/unsubscribe/one-click?t=x"), CancellationToken.None);

        StringAssert.EndsWith(id, "@sched.example", "the Message-ID is on the sending domain");
        var api = new HttpClient { BaseAddress = new Uri($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(8025)}") };
        var list = await api.GetFromJsonAsync<JsonElement>("/api/v1/messages");
        var summary = list.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("Subject").GetString() == "[sched:7KQ2M9XD] Rocky asks: Planning");
        var messageId = summary.GetProperty("ID").GetString();
        var message = await api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{messageId}");
        var headers = await api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{messageId}/headers");

        Assert.AreEqual("Plain text", message.GetProperty("Text").GetString()!.Trim());
        StringAssert.Contains(message.GetProperty("HTML").GetString(), "<p>HTML</p>");
        Assert.AreEqual("scheduler@sched.example", message.GetProperty("From").GetProperty("Address").GetString());
        Assert.AreEqual("rocky@example.com", message.GetProperty("ReplyTo")[0].GetProperty("Address").GetString());
        Assert.AreEqual("<https://scheduler.example/unsubscribe/one-click?t=x>", headers.GetProperty("List-Unsubscribe")[0].GetString());
        Assert.AreEqual("List-Unsubscribe=One-Click", headers.GetProperty("List-Unsubscribe-Post")[0].GetString());
    }

    [TestMethod]
    public async Task A_refused_recipient_is_a_permanent_failure()
    {
        var ex = await Assert.ThrowsExactlyAsync<PermanentDeliveryException>(() => Sender().SendAsync(
            new OutboundEmail("nobody@elsewhere.example", null, "Hello", "Text", null, null), CancellationToken.None));

        StringAssert.StartsWith(ex.Message, "5");
    }

    private static SmtpEmailSender Sender() => new(
        new SmtpOptions
        {
            Host = _mailpit.Hostname,
            Port = _mailpit.GetMappedPublicPort(1025),
            Security = SecureSocketOptions.None,
            Username = "scheduler",
            Password = "secret",
        },
        Options.Create(new EmailOptions { From = "scheduler@sched.example", FromName = "Scheduler" }));
}
