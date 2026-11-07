using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Scheduler.Core.Email;
using Scheduler.Core.Model;
using Scheduler.Core.Services;
using Scheduler.Web.Components.Pages;
using Scheduler.Web.Components.Pages.Admin;

namespace Scheduler.Web.Tests;

/// <summary>
/// Requests to organize through the real app: the pages as they render once interactive, the emails' HTML, and the
/// MCP tools. The rules are tested in Scheduler.Data.Tests. Development lets anyone organize, so here only Rocky can.
/// </summary>
[TestClass]
public sealed class OrganizerRequestPageTests
{
    private const string AdminEmail = "admin@localhost.test";

    private SchedulerApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SchedulerApp(("Scheduler:Organizers:0", "rocky@example.com"));

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task Someone_who_cant_organize_is_offered_the_request_form_and_then_sees_it_waiting()
    {
        var before = await RenderAsync<AskToOrganize>("bob@example.com");
        StringAssert.Contains(before, "Note to the admins");
        StringAssert.Contains(before, "Send request");

        await AskAsync("bob@example.com", "Contoso PM");

        var after = await RenderAsync<AskToOrganize>("bob@example.com");
        StringAssert.Contains(after, "Your request for bob@example.com has been waiting");
        Assert.DoesNotContain("Send request", after);
    }

    [TestMethod]
    public async Task An_organizer_is_told_they_already_can()
    {
        var html = await RenderAsync<AskToOrganize>("rocky@example.com");

        StringAssert.Contains(html, "You can already create scheduling events");
    }

    [TestMethod]
    public async Task The_compose_page_links_a_non_organizer_to_the_request()
    {
        var html = await RenderAsync<NewEvent>("bob@example.com");

        StringAssert.Contains(html, "href=\"/ask-to-organize\"");
        Assert.DoesNotContain("Send invitations", html);
    }

    [TestMethod]
    public async Task The_admin_page_lists_waiting_requests_with_their_notes()
    {
        await AskAsync("bob@example.com", "Contoso PM <b>bold</b>");

        var html = await RenderAsync<Organizers>(AdminEmail, decode: false);

        StringAssert.Contains(html, "Requests to organize");
        StringAssert.Contains(html, "bob@example.com");
        StringAssert.Contains(html, "Contoso PM &lt;b&gt;bold&lt;/b&gt;", "the note is shown as text, never markup");
        StringAssert.Contains(html, "Approve");
        StringAssert.Contains(html, "Decline");
    }

    [TestMethod]
    public async Task The_request_emails_have_html_bodies()
    {
        var composer = _app.Services.GetRequiredService<IEmailComposer>();

        var waiting = await composer.OrganizerRequestWaitingAsync("bob@example.com", "https://sched.test/admin/organizers");
        var approved = await composer.OrganizerRequestApprovedAsync("bob@example.com", "https://sched.test/events/new");
        var declined = await composer.OrganizerRequestDeclinedAsync("bob@example.com", "Sun Nov 15");

        StringAssert.Contains(waiting.HtmlBody, "https://sched.test/admin/organizers");
        StringAssert.Contains(waiting.HtmlBody, "Review requests");
        StringAssert.Contains(approved.HtmlBody, "Schedule a meeting");
        StringAssert.Contains(approved.HtmlBody, "/agents\" style=\"color:#0969da;\">How to connect it</a>");
        StringAssert.Contains(declined.HtmlBody, "Sun Nov 15");
        Assert.DoesNotContain("<a href", declined.HtmlBody);
    }

    [TestMethod]
    public async Task Agents_can_ask_and_only_admins_see_the_request_tools()
    {
        await using var bob = await ConnectAsync("bob@example.com");
        await using var admin = await ConnectAsync(AdminEmail);

        var bobTools = (await bob.ListToolsAsync()).Select(t => t.Name).ToList();
        var adminTools = (await admin.ListToolsAsync()).Select(t => t.Name).ToList();
        CollectionAssert.Contains(bobTools, "request_to_organize");
        CollectionAssert.DoesNotContain(bobTools, "list_organizer_requests");
        CollectionAssert.IsSubsetOf(
            new[] { "list_organizer_requests", "approve_organizer_request", "decline_organizer_request" }, adminTools);

        var asked = await bob.CallToolAsync("request_to_organize", new Dictionary<string, object?> { ["note"] = "Contoso PM" });
        Assert.IsFalse(asked.IsError ?? false, Text(asked));
        var id = JsonDocument.Parse(Text(asked)).RootElement.GetProperty("id").GetString();

        var refused = await bob.CallToolAsync("approve_organizer_request", new Dictionary<string, object?> { ["requestId"] = id });
        Assert.IsTrue(refused.IsError);

        var approved = await admin.CallToolAsync("approve_organizer_request", new Dictionary<string, object?> { ["requestId"] = id });
        Assert.IsFalse(approved.IsError ?? false, Text(approved));
        StringAssert.Contains(Text(await bob.CallToolAsync("get_my_access")), "\"canOrganize\":true");
    }

    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;

    private async Task AskAsync(string email, string? note)
    {
        using var scope = _app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAdminService>()
            .RequestToOrganizeAsync(new CallerIdentity(null, [email], Channel.Web), null, note, CancellationToken.None);
    }

    /// <summary>An MCP client signed in as <paramref name="email"/> with a personal access token.</summary>
    private async Task<McpClient> ConnectAsync(string email)
    {
        using var scope = _app.Services.CreateScope();
        var token = await scope.ServiceProvider.GetRequiredService<IAccessTokenService>()
            .CreateAsync(new CallerIdentity(null, [email], Channel.Web), "Test agent", 30, CancellationToken.None);
        return await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(SchedulerApp.Origin + "/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token.Token },
            },
            _app.CreateClient()));
    }

    /// <summary>A page as it shows once interactive and loaded, signed in as <paramref name="email"/>.</summary>
    private async Task<string> RenderAsync<TPage>(string email, bool decode = true) where TPage : IComponent
    {
        using var scope = _app.Services.CreateScope();
        await using var bunit = new BunitContext();
        bunit.Services.AddFallbackServiceProvider(scope.ServiceProvider);
        var auth = bunit.AddAuthorization();
        auth.SetAuthorized(email);
        auth.SetClaims(new Claim(ClaimTypes.Email, email));
        bunit.JSInterop.Mode = JSRuntimeMode.Loose;
        bunit.SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        var page = bunit.Render<TPage>();
        page.WaitForAssertion(() => Assert.DoesNotContain("Loading", page.Markup));
        return decode ? WebUtility.HtmlDecode(page.Markup) : page.Markup;
    }
}
