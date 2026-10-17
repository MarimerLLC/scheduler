using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Scheduler.Core.Model;
using Scheduler.Core.Security;
using Scheduler.Core.Services;

namespace Scheduler.Web.Identity;

/// <summary>
/// Authenticates <c>Authorization: Bearer sched_pat_...</c> on <c>/mcp</c>: a personal access token for an
/// agent that can't do the interactive OAuth sign-in. The principal carries the account and all of its
/// verified addresses. Other bearer values go to OpenIddict's validation instead (see <see cref="IsPersonalAccessToken"/>).
/// </summary>
internal sealed class PersonalAccessTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAccessTokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PersonalAccessToken";

    /// <summary>Whether the request's bearer token has the personal access token prefix.</summary>
    public static bool IsPersonalAccessToken(HttpContext http) => AccessTokens.LooksLikeToken(BearerToken(http.Request));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = BearerToken(Request);
        if (!AccessTokens.LooksLikeToken(token))
            return AuthenticateResult.NoResult();

        var caller = await tokens.AuthenticateAsync(token!, Channel.Mcp, Context.RequestAborted);
        if (caller is null)
            return AuthenticateResult.Fail("The personal access token is invalid, expired or revoked.");

        var identity = new ClaimsIdentity(
            caller.VerifiedEmails.Select(email => new Claim(ClaimTypes.Email, email))
                .Append(new Claim(ClaimTypes.Name, caller.VerifiedEmails[0]))
                .Append(new Claim(CallerResolver.AccountIdClaim, caller.UserId!.Value.ToString())),
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}
