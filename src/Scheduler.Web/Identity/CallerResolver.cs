using System.Security.Claims;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Services;

namespace Scheduler.Web.Identity;

/// <summary>
/// Works out who a request is from. Whatever authenticated it (the browser cookie from Microsoft or Google
/// sign-in, an MCP OAuth access token, a personal access token, or in Development the dev sign-in and
/// <see cref="DevEmailHeader"/>) put the person's verified addresses in email claims. The service layer finds
/// their account, and its other verified addresses, from that. See docs/design.md, "Identity and authentication".
/// </summary>
internal static class CallerResolver
{
    /// <summary>The account a principal belongs to, when its sign-in knows it (a personal access token does).</summary>
    public const string AccountIdClaim = "scheduler:account_id";

    /// <summary>An HTTP request, as authenticated for its endpoint (on <c>/mcp</c>, the bearer token).</summary>
    public static CallerIdentity? FromHttpContext(HttpContext? http, Channel channel) =>
        http is null ? null : FromPrincipal(http.User, channel);

    /// <summary>
    /// A signed-in principal, e.g. from a Blazor circuit's authentication state. Its email claims are taken
    /// as verified, so whatever signs people in must only issue verified addresses.
    /// </summary>
    public static CallerIdentity? FromPrincipal(ClaimsPrincipal? user, Channel channel)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        var emails = user.FindAll(ClaimTypes.Email)
            .Concat(user.FindAll("email"))
            .Select(c => EmailAddresses.Normalize(c.Value))
            .Distinct()
            .ToList();
        Guid? accountId = Guid.TryParse(user.FindFirst(AccountIdClaim)?.Value, out var id) ? id : null;
        return emails.Count > 0 ? new CallerIdentity(accountId, emails, channel) : null;
    }
}
