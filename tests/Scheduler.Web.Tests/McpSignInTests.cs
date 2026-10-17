using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Scheduler.Web.Identity;

namespace Scheduler.Web.Tests;

/// <summary>
/// The MCP authorization flow end to end, as an MCP client sees it: 401 with resource metadata, discovery,
/// client registration (metadata document or dynamic), sign-in, consent, code, token, tool call.
/// </summary>
[TestClass]
public sealed class McpSignInTests
{
    private const string Email = "pat@example.com";
    private static readonly Uri MetadataDocumentUrl = new("https://agent.example/oauth/client.json");

    private SchedulerApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SchedulerApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task Mcp_without_a_token_is_401_pointing_at_resource_metadata()
    {
        var response = await _app.CreateClient().PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.Single();
        Assert.AreEqual("Bearer", challenge.Scheme);
        StringAssert.Contains(challenge.Parameter, "resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"");
        Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length, "no error page for a protocol client");
    }

    [TestMethod]
    public async Task Resource_metadata_names_the_mcp_uri_and_this_server_as_issuer()
    {
        var metadata = await _app.CreateClient().GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp");

        Assert.AreEqual("http://localhost/mcp", metadata.GetProperty("resource").GetString());
        Assert.AreEqual("http://localhost/", metadata.GetProperty("authorization_servers")[0].GetString());
        Assert.AreEqual(McpResource.Scope, metadata.GetProperty("scopes_supported")[0].GetString());
    }

    [TestMethod]
    public async Task Authorization_server_metadata_has_what_mcp_clients_require()
    {
        var metadata = await _app.CreateClient().GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");

        Assert.AreEqual("http://localhost/", metadata.GetProperty("issuer").GetString());
        CollectionAssert.Contains(Strings(metadata, "code_challenge_methods_supported"), "S256");
        Assert.IsTrue(metadata.GetProperty("client_id_metadata_document_supported").GetBoolean());
        Assert.IsTrue(metadata.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
        Assert.AreEqual("http://localhost/connect/register", metadata.GetProperty("registration_endpoint").GetString());
        CollectionAssert.Contains(Strings(metadata, "scopes_supported"), "offline_access");
    }

    [TestMethod]
    public async Task Client_with_a_metadata_document_signs_in_and_calls_tools_as_the_person()
    {
        ServeMetadataDocument();
        var browser = _app.CreateBrowser();
        var tokens = new CapturingTokenCache();

        await using var client = await _app.ConnectAsync(OAuth(browser, tokens, MetadataDocumentUrl, new Uri("http://127.0.0.1:43123/callback")));

        Assert.AreEqual(MetadataDocumentUrl.AbsoluteUri, tokens.Tokens!.ClientId);
        Assert.IsNotNull(tokens.Tokens.RefreshToken, "a refresh token keeps the connection alive past the access token");
        StringAssert.Contains(browser.ConsentPage, "Test Agent");
        StringAssert.Contains(browser.ConsentPage, "agent.example");
        StringAssert.Contains(browser.ConsentPage, "127.0.0.1:43123");
        StringAssert.Contains(browser.ConsentPage, "a program on this computer");
        await AssertActingAsAsync(client, Email);
    }

    [TestMethod]
    public async Task Client_using_dynamic_registration_signs_in_and_calls_tools_as_the_person()
    {
        var browser = _app.CreateBrowser();
        var tokens = new CapturingTokenCache();
        var oauth = OAuth(browser, tokens, metadataDocument: null, new Uri("http://localhost:43124/callback"));
        oauth.DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Legacy Agent" };

        await using var client = await _app.ConnectAsync(oauth);

        StringAssert.StartsWith(tokens.Tokens!.ClientId, "dcr_");
        StringAssert.Contains(browser.ConsentPage, "Legacy Agent");
        StringAssert.Contains(browser.ConsentPage, "isn't verified");
        await AssertActingAsAsync(client, Email);
    }

    [TestMethod]
    public async Task A_token_is_only_accepted_at_the_mcp_uri_it_was_issued_for()
    {
        ServeMetadataDocument();
        var tokens = new CapturingTokenCache();
        await using (await _app.ConnectAsync(OAuth(_app.CreateBrowser(), tokens, MetadataDocumentUrl, new Uri("http://127.0.0.1:43125/callback"))))
        {
        }

        // A bare request: whatever the MCP layer makes of it, getting past authorization is what counts.
        var response = await PostToolsListAsync(tokens.Tokens!.AccessToken, host: null);
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode, "the token works where it was issued");

        response = await PostToolsListAsync(tokens.Tokens.AccessToken, host: "other.example");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Denying_consent_returns_access_denied_to_the_client()
    {
        ServeMetadataDocument();
        var redirect = await _app.CreateBrowser().AuthorizeAsync(AuthorizationUrl(), Email, decision: "deny");

        var query = HttpUtility.ParseQueryString(redirect.Query);
        Assert.AreEqual("127.0.0.1", redirect.Host);
        Assert.AreEqual("access_denied", query["error"]);
        Assert.AreEqual("http://localhost/", query["iss"]);
    }

    [TestMethod]
    public async Task A_resource_other_than_this_mcp_server_is_refused()
    {
        ServeMetadataDocument();

        var response = await _app.CreateClient(new() { AllowAutoRedirect = false })
            .GetAsync(AuthorizationUrl(resource: "https://elsewhere.example/mcp"));

        var redirect = SchedulerApp.Browser.RedirectOf(response);
        Assert.AreEqual("127.0.0.1", redirect.Host);
        Assert.AreEqual("invalid_target", HttpUtility.ParseQueryString(redirect.Query)["error"]);
    }

    [TestMethod]
    public async Task A_metadata_document_whose_client_id_does_not_match_its_url_is_refused()
    {
        _app.MetadataHost.Serve(MetadataDocumentUrl, JsonSerializer.Serialize(new
        {
            client_id = "https://agent.example/someone-else.json",
            client_name = "Test Agent",
            redirect_uris = new[] { "http://127.0.0.1/callback" },
        }));

        var response = await _app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync(AuthorizationUrl());

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "match its URL");
    }

    [TestMethod]
    public async Task A_redirect_uri_not_in_the_metadata_document_is_refused()
    {
        ServeMetadataDocument();

        var response = await _app.CreateClient(new() { AllowAutoRedirect = false })
            .GetAsync(AuthorizationUrl(redirectUri: "https://attacker.example/callback"));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task The_metadata_document_is_cached_between_sign_ins()
    {
        ServeMetadataDocument();
        var http = _app.CreateClient(new() { AllowAutoRedirect = false });
        await http.GetAsync(AuthorizationUrl());
        await http.GetAsync(AuthorizationUrl());

        Assert.AreEqual(1, _app.MetadataHost.Fetches);
    }

    [TestMethod]
    public async Task Development_header_still_works_without_oauth()
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(SchedulerApp.Origin + "/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { [DevEmailHeader.HeaderName] = "dev@example.com" },
            },
            _app.CreateClient());
        await using var client = await McpClient.CreateAsync(transport);

        await AssertActingAsAsync(client, "dev@example.com");
    }

    [TestMethod]
    public async Task Dynamic_registration_refuses_redirects_that_are_neither_https_nor_localhost()
    {
        var response = await _app.CreateClient().PostAsJsonAsync("/connect/register", new
        {
            client_name = "Sketchy",
            redirect_uris = new[] { "http://attacker.example/callback" },
        });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("invalid_redirect_uri", body.GetProperty("error").GetString());
    }

    private void ServeMetadataDocument() =>
        _app.MetadataHost.Serve(MetadataDocumentUrl, JsonSerializer.Serialize(new
        {
            client_id = MetadataDocumentUrl.AbsoluteUri,
            client_name = "Test Agent",
            redirect_uris = new[] { "http://127.0.0.1/callback" },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        }));

    private async Task<HttpResponseMessage> PostToolsListAsync(string accessToken, string? host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        };
        request.Headers.Authorization = new("Bearer", accessToken);
        if (host is not null)
            request.Headers.Host = host;
        return await _app.CreateClient().SendAsync(request);
    }

    /// <summary>A hand-built authorization request, as a client using the metadata document would send it.</summary>
    private static Uri AuthorizationUrl(string resource = "http://localhost/mcp", string redirectUri = "http://127.0.0.1:43126/callback")
    {
        var query = HttpUtility.ParseQueryString("");
        query["response_type"] = "code";
        query["client_id"] = MetadataDocumentUrl.AbsoluteUri;
        query["redirect_uri"] = redirectUri;
        query["scope"] = McpResource.Scope;
        query["state"] = "xyz";
        query["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
        query["code_challenge_method"] = "S256";
        query["resource"] = resource;
        return new Uri($"{SchedulerApp.Origin}{AuthorizationEndpoint.Path}?{query}");
    }

    private static ClientOAuthOptions OAuth(SchedulerApp.Browser browser, ITokenCache tokens, Uri? metadataDocument, Uri redirectUri) => new()
    {
        RedirectUri = redirectUri,
        ClientMetadataDocumentUri = metadataDocument,
        TokenCache = tokens,
        AuthorizationCallbackHandler = async (context, _) =>
        {
            var redirect = await browser.AuthorizeAsync(context.AuthorizationUri, Email);
            var query = HttpUtility.ParseQueryString(redirect.Query);
            return new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query["iss"] };
        },
    };

    private static async Task AssertActingAsAsync(McpClient client, string email)
    {
        var tools = await client.ListToolsAsync();
        Assert.IsTrue(tools.Any(t => t.Name == "get_my_access"));

        var result = await client.CallToolAsync("get_my_access");
        Assert.IsFalse(result.IsError ?? false);
        StringAssert.Contains(((TextContentBlock)result.Content[0]).Text, email);
    }

    private static List<string?> Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(e => e.GetString()).ToList();

    private sealed class CapturingTokenCache : ITokenCache
    {
        public TokenContainer? Tokens { get; private set; }

        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            Tokens = tokens;
            return ValueTask.CompletedTask;
        }

        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Tokens);
    }
}
