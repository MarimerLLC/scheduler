using System.Net;

namespace Scheduler.Web.Tests;

/// <summary>The "Use Scheduler with your agent" page, and the home page's link to it.</summary>
[TestClass]
public sealed class AgentsPageTests
{
    private SchedulerApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SchedulerApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task The_home_page_links_to_it()
    {
        var html = await _app.CreateClient().GetStringAsync("/");

        Assert.Contains("""<a class="button" href="/agents">""", html);
    }

    [TestMethod]
    public async Task It_gives_this_servers_mcp_url_and_both_ways_to_sign_in()
    {
        var http = _app.CreateClient();
        var response = await http.GetAsync("/agents");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(new Uri(http.BaseAddress!, "/mcp").AbsoluteUri, html);
        Assert.Contains("id=\"calendar\"", html);
        Assert.Contains("https://github.com/MarimerLLC/calendar-mcp", html);
        Assert.Contains("id=\"oauth\"", html);
        Assert.Contains("id=\"token\"", html);
        Assert.Contains("href=\"/account\"", html);
    }
}
