using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Client.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Scheduler.Web.Identity;

/// <summary>
/// Browser sign-in through Microsoft or Google (OpenIddict's client), ending in this app's own cookie. MCP
/// clients go through the same pages: the authorization endpoint sends an unsigned-in browser here first.
/// </summary>
internal static class ExternalSignIn
{
    public const string SignInPath = "/signin";
    public const string SignOutPath = "/signout";
    public const string CallbackPath = "signin/callback/";

    public static void MapExternalSignIn(this WebApplication app)
    {
        app.MapGet(SignInPath + "/{provider}", (string provider, string? returnUrl, IOptions<SchedulerIdentityOptions> options) =>
        {
            if (options.Value.Provider(provider) is not { } name)
                return Results.Redirect(SignInPage(returnUrl, "That sign-in provider isn't set up on this server."));

            var properties = new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictClientAspNetCoreConstants.Properties.ProviderName] = name,
            })
            {
                RedirectUri = LocalOrRoot(returnUrl),
            };
            return Results.Challenge(properties, [OpenIddictClientAspNetCoreDefaults.AuthenticationScheme]);
        });

        // Microsoft and Google return with a GET; form_post responses arrive as a POST.
        app.MapMethods("/" + CallbackPath + "{provider}", [HttpMethods.Get, HttpMethods.Post], CallbackAsync).DisableAntiforgery();

        app.MapGet(SignOutPath, async (HttpContext http, string? returnUrl) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect(LocalOrRoot(returnUrl));
        });
    }

    private static async Task<IResult> CallbackAsync(HttpContext http, ILoggerFactory loggers)
    {
        var result = await http.AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        var returnUrl = LocalOrRoot(result.Properties?.RedirectUri);
        if (result is not { Succeeded: true, Principal: { } principal })
        {
            loggers.CreateLogger(typeof(ExternalSignIn)).LogInformation(result.Failure, "External sign-in failed");
            return Results.Redirect(SignInPage(returnUrl, "Sign-in didn't complete. Try again."));
        }

        var provider = principal.GetClaim(Claims.Private.ProviderName) ?? "";
        var (email, displayName, error) = ExternalEmail.Read(provider, principal);
        if (email is null)
            return Results.Redirect(SignInPage(returnUrl, error));

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, displayName ?? email),
                new Claim(Claims.Private.ProviderName, provider),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
        return Results.LocalRedirect(returnUrl);
    }

    public static string SignInPage(string? returnUrl, string? error = null)
    {
        var query = QueryString.Create("returnUrl", LocalOrRoot(returnUrl));
        return SignInPath + (error is null ? query : query.Add("error", error));
    }

    /// <summary>Only same-site paths: a sign-in link must never bounce the browser to another site.</summary>
    public static string LocalOrRoot(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
            && !url.StartsWith("/\\", StringComparison.Ordinal) ? url : "/";
}
