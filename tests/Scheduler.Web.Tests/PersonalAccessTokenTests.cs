using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Scheduler.Core.Model;
using Scheduler.Core.Security;
using Scheduler.Core.Services;

namespace Scheduler.Web.Tests;

/// <summary>A headless agent (RockBot) using a personal access token on <c>/mcp</c>, as its mcp.json would.</summary>
[TestClass]
public sealed class PersonalAccessTokenTests
{
    private SchedulerApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SchedulerApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task An_agent_with_a_token_calls_tools_as_its_owner()
    {
        var token = await CreateTokenAsync("pat@example.com");

        await using var client = await ConnectAsync(token.Token);
        var result = await client.CallToolAsync("get_my_access");

        Assert.IsFalse(result.IsError ?? false);
        StringAssert.Contains(((TextContentBlock)result.Content[0]).Text, "pat@example.com");
    }

    [TestMethod]
    public async Task A_revoked_token_gets_the_401_challenge()
    {
        var token = await CreateTokenAsync("pat@example.com");
        await WithTokensAsync(tokens => tokens.RevokeAsync(Web("pat@example.com"), token.View.Id, CancellationToken.None));

        var response = await PostAsync("Bearer " + token.Token);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        StringAssert.Contains(response.Headers.WwwAuthenticate.Single().Parameter, "resource_metadata=");
    }

    [TestMethod]
    public async Task An_unknown_token_gets_the_401_challenge()
    {
        var response = await PostAsync("Bearer " + AccessTokens.New());

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<CreatedAccessToken> CreateTokenAsync(string email)
    {
        CreatedAccessToken? created = null;
        await WithTokensAsync(async tokens => created = await tokens.CreateAsync(Web(email), "RockBot", 30, CancellationToken.None));
        return created!;
    }

    private async Task WithTokensAsync(Func<IAccessTokenService, Task> action)
    {
        using var scope = _app.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<IAccessTokenService>());
    }

    private static CallerIdentity Web(string email) => new(null, [email], Channel.Web);

    private async Task<McpClient> ConnectAsync(string token) =>
        await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(SchedulerApp.Origin + "/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            },
            _app.CreateClient()));

    private async Task<HttpResponseMessage> PostAsync(string authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return await _app.CreateClient().SendAsync(request);
    }
}
