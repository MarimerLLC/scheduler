using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Scheduler.Core;

namespace Scheduler.Web.Identity;

/// <summary>
/// Development-only browser sign-in: type any address and become that person, so the web UI can be built
/// and tried before real OIDC sign-in (roadmap item 3) exists. Not mapped in any other environment.
/// </summary>
internal static class DevSignIn
{
    public const string SignInPath = "/dev/signin";
    public const string SignOutPath = "/dev/signout";

    public static void MapDevSignIn(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return;

        app.MapGet(SignInPath, (string? returnUrl) => Results.Content($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>Development sign-in</title></head>
            <body style="font-family: system-ui, sans-serif; max-width: 30rem; margin: 3rem auto; padding: 0 1rem">
              <h1>Development sign-in</h1>
              <p>Development only: sign in as any address. Admins are listed in <code>Scheduler:Admins</code>.</p>
              <form method="post">
                <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(LocalOrRoot(returnUrl))}}">
                <label>Email <input type="email" name="email" required autofocus></label>
                <button type="submit">Sign in</button>
              </form>
            </body></html>
            """, "text/html"));

        app.MapPost(SignInPath, async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            string email;
            try
            {
                email = EmailAddresses.Normalize(form["email"]);
            }
            catch (SchedulingValidationException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, email)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.LocalRedirect(LocalOrRoot(form["returnUrl"]));
        }).DisableAntiforgery();

        app.MapGet(SignOutPath, async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/");
        });
    }

    private static string LocalOrRoot(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) ? url : "/";
}
