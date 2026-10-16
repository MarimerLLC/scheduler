using System.Security.Claims;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Web.Identity;

/// <summary>
/// Works out who a request is from. Real sign-in (OIDC for the web UI, OAuth bearer tokens for /mcp, magic
/// links for voters) is roadmap item 3; until then Development has the <see cref="DevEmailHeader"/> for MCP
/// clients and <see cref="DevSignIn"/> for the browser. See docs/design.md, "Identity".
/// </summary>
internal static class CallerResolver
{
    /// <summary>
    /// Development only: lets a local MCP client or curl act as an address without signing in.
    /// Ignored in every other environment.
    /// </summary>
    public const string DevEmailHeader = "X-Dev-Email";

    /// <summary>An HTTP request: a signed-in principal, or in Development the dev header.</summary>
    public static CallerIdentity? FromHttpContext(HttpContext? http, IHostEnvironment environment, Channel channel)
    {
        if (http is null)
            return null;

        if (FromPrincipal(http.User, channel) is { } signedIn)
            return signedIn;

        if (environment.IsDevelopment()
            && http.Request.Headers.TryGetValue(DevEmailHeader, out var devEmail)
            && !string.IsNullOrWhiteSpace(devEmail))
        {
            return new CallerIdentity(null, [EmailAddresses.Normalize(devEmail)], channel);
        }

        return null;
    }

    /// <summary>
    /// A signed-in principal, e.g. from a Blazor circuit's authentication state. Its email claims are taken
    /// as verified, so whatever signs people in must only issue verified addresses.
    /// </summary>
    public static CallerIdentity? FromPrincipal(ClaimsPrincipal? user, Channel channel)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        // TODO (roadmap item 3): resolve the UserAccount for this principal and use all of its verified
        // addresses, not just the ones in the token.
        var emails = user.FindAll(ClaimTypes.Email)
            .Concat(user.FindAll("email"))
            .Select(c => EmailAddresses.Normalize(c.Value))
            .Distinct()
            .ToList();
        return emails.Count > 0 ? new CallerIdentity(null, emails, channel) : null;
    }
}
