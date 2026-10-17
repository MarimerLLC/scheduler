using System.Text.Json;
using System.Text.Json.Serialization;
using OpenIddict.Abstractions;

namespace Scheduler.Web.Identity;

/// <summary>
/// OAuth Dynamic Client Registration (RFC 7591), which MCP 2026-07-28 deprecates in favour of client ID
/// metadata documents but older clients still use. Registers public clients only: any requested
/// authentication method is replaced with <c>none</c> (RFC 7591 lets the server substitute values), so
/// PKCE is what protects the code. Rate limited per address, because anyone can call it.
/// </summary>
internal static class DynamicClientRegistration
{
    public const string RateLimitPolicy = "client-registration";
    public const string UnnamedClient = "Unnamed MCP client";

    public static void MapDynamicClientRegistration(this WebApplication app) =>
        app.MapPost("/" + AttachClientRegistrationMetadata.RegistrationPath, RegisterAsync)
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimitPolicy);

    private static async Task<IResult> RegisterAsync(HttpRequest request, IOpenIddictApplicationManager applications, CancellationToken cancellationToken)
    {
        RegistrationRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<RegistrationRequest>(cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Error("The request body must be JSON client metadata.");
        }
        if (body is null)
            return Error("The request body must be JSON client metadata.");

        if (body.GrantTypes is { } grants && !grants.Contains("authorization_code"))
            return Error("Only the authorization_code grant (with refresh_token) is supported.");
        if (body.ResponseTypes is { } responses && !responses.Contains("code"))
            return Error("Only the code response type is supported.");

        var (redirectUris, redirectError) = ClientRegistration.ParseRedirectUris(body.RedirectUris);
        if (redirectError is not null)
            return Error(redirectError, "invalid_redirect_uri");

        var clientId = "dcr_" + Guid.NewGuid().ToString("N");
        var name = ClientRegistration.CleanName(body.ClientName) ?? UnnamedClient;
        var descriptor = ClientRegistration.Descriptor(clientId, name, redirectUris, ClientRegistration.Dynamic);
        await applications.CreateAsync(descriptor, cancellationToken);

        return Results.Json(new RegistrationResponse
        {
            ClientId = clientId,
            ClientIdIssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ClientName = name,
            RedirectUris = [.. redirectUris.Select(u => u.OriginalString)],
            GrantTypes = ["authorization_code", "refresh_token"],
            ResponseTypes = ["code"],
            TokenEndpointAuthMethod = "none",
            ApplicationType = descriptor.ApplicationType!,
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult Error(string description, string error = "invalid_client_metadata") =>
        Results.Json(new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);

    private sealed class RegistrationRequest
    {
        [JsonPropertyName("client_name")] public string? ClientName { get; set; }
        [JsonPropertyName("redirect_uris")] public List<string?>? RedirectUris { get; set; }
        [JsonPropertyName("grant_types")] public List<string>? GrantTypes { get; set; }
        [JsonPropertyName("response_types")] public List<string>? ResponseTypes { get; set; }
    }

    private sealed class RegistrationResponse
    {
        [JsonPropertyName("client_id")] public required string ClientId { get; init; }
        [JsonPropertyName("client_id_issued_at")] public long ClientIdIssuedAt { get; init; }
        [JsonPropertyName("client_name")] public required string ClientName { get; init; }
        [JsonPropertyName("redirect_uris")] public required List<string> RedirectUris { get; init; }
        [JsonPropertyName("grant_types")] public required List<string> GrantTypes { get; init; }
        [JsonPropertyName("response_types")] public required List<string> ResponseTypes { get; init; }
        [JsonPropertyName("token_endpoint_auth_method")] public required string TokenEndpointAuthMethod { get; init; }
        [JsonPropertyName("application_type")] public required string ApplicationType { get; init; }
    }
}
