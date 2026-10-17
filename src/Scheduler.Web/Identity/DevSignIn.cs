using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Scheduler.Core;

namespace Scheduler.Web.Identity;

/// <summary>
/// Development-only browser sign-in: type any address and become that person, so the web UI and the MCP
/// OAuth flow can be tried without Microsoft or Google credentials. Not mapped in any other environment.
/// </summary>
internal static class DevSignIn
{
    public const string SignInPath = "/dev/signin";

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
                <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(ExternalSignIn.LocalOrRoot(returnUrl))}}">
                <p><label>Email <input type="email" name="email" required autofocus></label></p>
                <p><label><input type="checkbox" name="unverified" value="true"> As an unverified address (try signing in by email link)</label></p>
                <button type="submit">Sign in</button>
              </form>
            </body></html>
            """, "text/html"));

        app.MapPost(SignInPath, async (HttpContext http, EmailLinkSignIn emailLinks) =>
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

            var returnUrl = ExternalSignIn.LocalOrRoot(form["returnUrl"]);
            if (form["unverified"] == "true")
                return Results.LocalRedirect(emailLinks.PageFor(new PendingEmailSignIn(
                    email, null, returnUrl, "Development", "The development sign-in is pretending its provider didn't verify this address.")));

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, email)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.LocalRedirect(returnUrl);
        }).DisableAntiforgery();
    }
}
