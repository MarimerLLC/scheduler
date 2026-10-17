using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Scheduler.Core.Model;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Scheduler.Web.Identity;

/// <summary>
/// The OAuth authorization endpoint for MCP clients. OpenIddict has already checked the client, redirect
/// URI and PKCE by the time this runs. It makes sure the person is signed in (federated: Microsoft or
/// Google, through the sign-in page), shows the consent page, and on "Allow" issues a code for a token whose
/// audience is this server's MCP endpoint and nothing else. See docs/design.md, "MCP".
/// </summary>
internal static class AuthorizationEndpoint
{
    public const string Path = "/connect/authorize";
    public const string ConsentPath = "/connect/consent";
    public const string DecisionField = "decision";
    public const string Allow = "allow";

    /// <summary>A sign-in older than this is redone when the client asks for prompt=login or a max_age.</summary>
    private static readonly TimeSpan FreshSignIn = TimeSpan.FromMinutes(1);

    private static readonly HashSet<string> GrantableScopes =
        [Scopes.OpenId, Scopes.Email, Scopes.Profile, Scopes.OfflineAccess, McpResource.Scope];

    public static void MapAuthorizationEndpoint(this WebApplication app) =>
        // Antiforgery is checked by hand below: only the consent form's POST carries a token, while a
        // client's own authorization request (GET, or a form POST per OpenID Connect) never does.
        app.MapMethods(Path, [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync).DisableAntiforgery();

    private static async Task<IResult> AuthorizeAsync(HttpContext http, IAntiforgery antiforgery)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request can't be retrieved.");

        // RFC 8707: this server issues tokens for its own MCP endpoint only.
        var resource = McpResource.Canonical(http.Request);
        if (request.GetResources().Any(r => r != resource))
            return Reject(Errors.InvalidTarget, $"This server only issues tokens for {resource}.");

        var isConsentPost = HttpMethods.IsPost(http.Request.Method) && http.Request.HasFormContentType
            && http.Request.Form.ContainsKey(DecisionField);
        var signedIn = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var caller = signedIn.Succeeded ? CallerResolver.FromPrincipal(signedIn.Principal, Channel.Web) : null;
        if (caller is null || (!isConsentPost && NeedsFreshSignIn(request, signedIn.Properties)))
        {
            if (request.HasPromptValue(PromptValues.None))
                return Reject(Errors.LoginRequired, "You aren't signed in to the scheduling server.");
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = http.Request.PathBase + http.Request.Path + RequestParameters(http.Request) },
                [CookieAuthenticationDefaults.AuthenticationScheme]);
        }

        if (!isConsentPost)
        {
            if (request.HasPromptValue(PromptValues.None))
                return Reject(Errors.ConsentRequired, "Connecting an app needs your approval on the consent page.");
            return Results.Redirect(http.Request.PathBase + ConsentPath + RequestParameters(http.Request));
        }

        if (!await antiforgery.IsRequestValidAsync(http))
            return Results.BadRequest("The consent form expired. Start connecting the app again.");
        if (http.Request.Form[DecisionField] != Allow)
            return Reject(Errors.AccessDenied, "You didn't allow the app to connect.");

        var email = caller.VerifiedEmails[0];
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, email)
            .SetClaim(Claims.Email, email)
            .SetClaim(Claims.Name, signedIn.Principal!.FindFirstValue(ClaimTypes.Name) ?? email);

        // offline_access is always granted so the connection survives the one-hour access token; MCP clients
        // don't all ask for it, and the authorization server may issue a refresh token at its discretion.
        identity.SetScopes(request.GetScopes().Where(GrantableScopes.Contains).Append(McpResource.Scope).Append(Scopes.OfflineAccess).Distinct());
        identity.SetResources(resource);
        identity.SetDestinations(static claim => claim.Type is Claims.Email or Claims.Name
            ? [Destinations.AccessToken, Destinations.IdentityToken]
            : [Destinations.AccessToken]);

        return Results.SignIn(new ClaimsPrincipal(identity), properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static bool NeedsFreshSignIn(OpenIddictRequest request, AuthenticationProperties? properties)
    {
        long? maxAge = request.HasPromptValue(PromptValues.Login) ? 0 : request.MaxAge;
        if (maxAge is null)
            return false;
        var limit = TimeSpan.FromSeconds(maxAge.Value) > FreshSignIn ? TimeSpan.FromSeconds(maxAge.Value) : FreshSignIn;
        return properties?.IssuedUtc is not { } issued || DateTimeOffset.UtcNow - issued > limit;
    }

    /// <summary>The authorization request's parameters as a query string, without the consent form's own fields.</summary>
    private static QueryString RequestParameters(HttpRequest request)
    {
        IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> source =
            request.HasFormContentType ? request.Form : request.Query;
        return QueryString.Create(source.Where(p => p.Key != DecisionField && p.Key != "__RequestVerificationToken"));
    }

    private static IResult Reject(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
