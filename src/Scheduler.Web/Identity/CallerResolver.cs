using System.Security.Claims;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Web.Identity;

/// <summary>
/// Works out who an HTTP request is from. Scaffold: real sign-in (OIDC for the web UI, OAuth bearer
/// tokens for /mcp, magic links for voters) is not wired up yet; see docs/design.md, "Identity".
/// </summary>
internal static class CallerResolver
{
    /// <summary>
    /// Development only: lets a local MCP client or curl act as an address without signing in.
    /// Ignored in every other environment.
    /// </summary>
    public const string DevEmailHeader = "X-Dev-Email";

    public static CallerIdentity? FromHttpContext(HttpContext? http, IHostEnvironment environment, Channel channel)
    {
        if (http is null)
            return null;

        if (http.User.Identity?.IsAuthenticated == true)
        {
            // TODO: resolve the UserAccount for this principal and use all of its verified addresses,
            // not just the one in the token.
            var emails = http.User.FindAll(ClaimTypes.Email)
                .Concat(http.User.FindAll("email"))
                .Select(c => EmailAddresses.Normalize(c.Value))
                .Distinct()
                .ToList();
            return emails.Count > 0 ? new CallerIdentity(null, emails, channel) : null;
        }

        if (environment.IsDevelopment()
            && http.Request.Headers.TryGetValue(DevEmailHeader, out var devEmail)
            && !string.IsNullOrWhiteSpace(devEmail))
        {
            return new CallerIdentity(null, [EmailAddresses.Normalize(devEmail)], channel);
        }

        return null;
    }
}
