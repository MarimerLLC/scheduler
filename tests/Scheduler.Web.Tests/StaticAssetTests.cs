using System.Net;
using System.Text.RegularExpressions;

namespace Scheduler.Web.Tests;

/// <summary>The icons: browsers ask for /favicon.ico whatever the page says, and the page links the rest.</summary>
[TestClass]
public sealed partial class StaticAssetTests
{
    private SchedulerApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SchedulerApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task The_favicon_is_served_at_the_root()
    {
        var response = await _app.CreateClient().GetAsync("/favicon.ico");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 1, 0 }, bytes[..4], "an icon file (ICONDIR, type 1)");
        Assert.AreEqual(7, BitConverter.ToUInt16(bytes, 4), "16, 24, 32, 48, 64, 128 and 256 px");
    }

    [TestMethod]
    public async Task Every_icon_the_page_links_is_served()
    {
        var http = _app.CreateClient();
        var html = await http.GetStringAsync("/");
        var links = IconLink().Matches(html).Select(m => m.Groups[1].Value).ToList();

        Assert.HasCount(3, links, html);
        foreach (var link in links)
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync(link)).StatusCode, link);
    }

    [GeneratedRegex("""<link rel="(?:icon|apple-touch-icon)" href="([^"]+)"[^>]*>""")]
    private static partial Regex IconLink();
}
