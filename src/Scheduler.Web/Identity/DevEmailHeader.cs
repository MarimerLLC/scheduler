using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Scheduler.Core;

namespace Scheduler.Web.Identity;

/// <summary>
/// Development only: an <see cref="HeaderName"/> header on <c>/mcp</c> acts as that address, so a local MCP
/// client or curl can skip the OAuth sign-in. The scheme is registered only in Development.
/// </summary>
internal sealed class DevEmailHeader(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevEmailHeader";
    public const string HeaderName = "X-Dev-Email";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var value) || string.IsNullOrWhiteSpace(value))
            return Task.FromResult(AuthenticateResult.NoResult());

        string email;
        try
        {
            email = EmailAddresses.Normalize(value);
        }
        catch (SchedulingValidationException ex)
        {
            return Task.FromResult(AuthenticateResult.Fail(ex.Message));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, email)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    // The bearer scheme's challenge says how to sign in (401 + resource_metadata); this one adds nothing.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => Task.CompletedTask;
}
