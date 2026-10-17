using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using Scheduler.Core.Model;
using Scheduler.Data;
using Scheduler.Web.Identity;

namespace Scheduler.Web.Tests;

/// <summary>
/// The real web app in Development (dev sign-in on, development certificates) over a fresh database, with
/// client metadata documents served by <see cref="MetadataHost"/> instead of the internet.
/// </summary>
/// <param name="settings">Configuration to add, e.g. lower rate limits.</param>
internal sealed partial class SchedulerApp(params (string Key, string Value)[] settings) : WebApplicationFactory<Program>
{
    public const string Origin = "http://localhost";

    private readonly string _connectionString = PostgresContainer.NewDatabase();

    public FakeMetadataHost MetadataHost { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:schedulerdb", _connectionString);
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(ClientMetadataDocuments.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => MetadataHost));
    }

    /// <summary>The emails waiting to be sent, oldest first.</summary>
    public async Task<List<OutboxEmail>> OutboxAsync()
    {
        await using var db = await Services.GetRequiredService<IDbContextFactory<SchedulerDbContext>>().CreateDbContextAsync();
        return await db.Outbox.OrderBy(o => o.Id).ToListAsync();
    }

    /// <summary>An MCP client for <c>/mcp</c> that signs in with the given OAuth options.</summary>
    public async Task<McpClient> ConnectAsync(ClientOAuthOptions oauth)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(Origin + "/mcp"), OAuth = oauth },
            CreateClient());
        return await McpClient.CreateAsync(transport);
    }

    /// <summary>A browser with its own cookies that doesn't follow redirects, so each step can be seen.</summary>
    public Browser CreateBrowser() =>
        new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    internal sealed partial class Browser(HttpClient http)
    {
        public HttpClient Http { get; } = http;

        /// <summary>
        /// What a person does after an MCP client opens the authorization URL: signs in (dev sign-in as
        /// <paramref name="email"/>), then allows or denies on the consent page. Returns the redirect to the
        /// client's redirect URI.
        /// </summary>
        public async Task<Uri> AuthorizeAsync(Uri authorizationUri, string email, string decision = AuthorizationEndpoint.Allow)
        {
            var response = await Http.GetAsync(authorizationUri);
            var location = RedirectOf(response);
            if (location.AbsolutePath == ExternalSignIn.SignInPath)
            {
                var returnUrl = System.Web.HttpUtility.ParseQueryString(location.Query)["ReturnUrl"]!;
                response = await Http.PostAsync(DevSignIn.SignInPath, new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["email"] = email, ["returnUrl"] = returnUrl }));
                response = await Http.GetAsync(RedirectOf(response));
                location = RedirectOf(response);
            }

            Assert.AreEqual(AuthorizationEndpoint.ConsentPath, location.AbsolutePath, "expected the consent page");
            var consent = await Http.GetAsync(location);
            var html = await consent.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, consent.StatusCode, html);
            Assert.AreEqual("DENY", consent.Headers.GetValues("X-Frame-Options").Single());
            ConsentPage = html;

            var fields = HiddenInput().Matches(html)
                .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
                .Append(new(AuthorizationEndpoint.DecisionField, decision))
                .ToList();
            response = await Http.PostAsync(AuthorizationEndpoint.Path, new FormUrlEncodedContent(fields));
            return RedirectOf(response);
        }

        /// <summary>
        /// Submits the form on a static page back to the page's URL, as pressing its button does: the hidden
        /// fields Blazor renders (antiforgery token, handler name) plus <paramref name="fields"/>. A page with
        /// several forms gets the first one's handler.
        /// </summary>
        public async Task<HttpResponseMessage> SubmitFormAsync(string url, params (string Name, string Value)[] fields)
        {
            var page = await Http.GetAsync(url);
            var html = await page.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
            var hidden = HiddenInput().Matches(html)
                .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
                .ToList();
            Assert.IsTrue(hidden.Any(f => f.Key == "_handler"), $"expected a form on {url}: {html}");
            return await Http.PostAsync(url, new FormUrlEncodedContent(hidden.Concat(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)))));
        }

        /// <summary>The last consent page shown, for checking what the person was told.</summary>
        public string ConsentPage { get; private set; } = "";

        public static Uri RedirectOf(HttpResponseMessage response)
        {
            Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode,
                $"expected a redirect from {response.RequestMessage?.RequestUri}, got: {response.Content.ReadAsStringAsync().Result}");
            var location = response.Headers.Location!;
            return location.IsAbsoluteUri ? location : new Uri(new Uri(Origin), location);
        }

        [GeneratedRegex("""<input type="hidden" name="([^"]*)" value="([^"]*)"\s*/?>""")]
        private static partial Regex HiddenInput();
    }
}

/// <summary>Serves client ID metadata documents for made-up HTTPS URLs, and counts the fetches.</summary>
internal sealed class FakeMetadataHost : HttpMessageHandler
{
    private readonly Dictionary<Uri, string> _documents = [];

    public int Fetches { get; private set; }

    public void Serve(Uri url, string json) => _documents[url] = json;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Fetches++;
        return Task.FromResult(_documents.TryGetValue(request.RequestUri!, out var json)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    // Shared across the app's handler pipelines; nothing to release.
    protected override void Dispose(bool disposing) { }
}
