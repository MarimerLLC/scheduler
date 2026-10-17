using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Web.Tests;

/// <summary>The rate limits on <c>/mcp</c> (per caller) and on the pages opened from email links (per client IP).</summary>
[TestClass]
public sealed class RateLimitTests
{
    private SchedulerApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SchedulerApp(("RateLimits:McpPerMinute", "2"), ("RateLimits:PagesPerMinute", "2"));

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task Mcp_is_limited_per_caller()
    {
        var pat = await CreateTokenAsync("pat@example.com");
        var sam = await CreateTokenAsync("sam@example.com");

        Assert.AreNotEqual(HttpStatusCode.TooManyRequests, (await PostMcpAsync(pat)).StatusCode);
        Assert.AreNotEqual(HttpStatusCode.TooManyRequests, (await PostMcpAsync(pat)).StatusCode);
        var limited = await PostMcpAsync(pat);

        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.IsTrue(int.Parse(limited.Headers.GetValues("Retry-After").Single()) > 0);
        Assert.AreNotEqual(HttpStatusCode.TooManyRequests, (await PostMcpAsync(sam)).StatusCode, "another caller has their own limit");
    }

    [TestMethod]
    public async Task Requests_without_a_token_still_get_the_401_challenge()
    {
        for (var i = 0; i < 4; i++)
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await PostMcpAsync(token: null)).StatusCode);
    }

    [TestMethod]
    public async Task Pages_opened_from_email_links_are_limited_per_client()
    {
        var http = _app.CreateClient();

        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/verify?t=x")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/e/7KQ2M9XD?t=x")).StatusCode);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await http.GetAsync("/verify?t=y")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("/")).StatusCode, "other pages aren't limited");
    }

    private async Task<string> CreateTokenAsync(string email)
    {
        using var scope = _app.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<IAccessTokenService>()
            .CreateAsync(new CallerIdentity(null, [email], Channel.Web), "RockBot", 30, CancellationToken.None);
        return created.Token;
    }

    private async Task<HttpResponseMessage> PostMcpAsync(string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        };
        if (token is not null)
            request.Headers.Authorization = new("Bearer", token);
        return await _app.CreateClient().SendAsync(request);
    }
}
