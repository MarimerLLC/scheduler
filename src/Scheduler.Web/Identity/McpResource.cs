using Microsoft.AspNetCore.Http.Extensions;

namespace Scheduler.Web.Identity;

/// <summary>
/// The MCP endpoint as an OAuth protected resource. The issuer and the resource URI both come from the
/// request (scheme, host, path base), the same way OpenIddict infers its issuer, so they agree with each
/// other in every environment. In production that relies on the proxy passing the original scheme
/// (forwarded headers) and on <c>AllowedHosts</c> naming the public host. See docs/design.md, "MCP".
/// </summary>
internal static class McpResource
{
    public const string Path = "/mcp";

    /// <summary>The one scope this server grants; it covers every tool.</summary>
    public const string Scope = "scheduler";

    /// <summary>The authorization server's issuer, exactly as OpenIddict writes it (with a trailing slash).</summary>
    public static string Issuer(HttpRequest request) =>
        UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase);

    /// <summary>The canonical MCP server URI (RFC 8707 resource, RFC 9728 <c>resource</c>, token audience).</summary>
    public static string Canonical(HttpRequest request) =>
        UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, Path);
}
