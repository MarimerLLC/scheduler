using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Scheduler.Core.Model;
using Scheduler.Web.Identity;

namespace Scheduler.Web;

/// <summary>The "RateLimits" configuration section. The defaults suit production; tests lower them.</summary>
internal sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>Requests to <c>/mcp</c> per caller (account, or address) per minute.</summary>
    public int McpPerMinute { get; set; } = 120;

    /// <summary>Requests per client IP per minute to the pages opened from email links (voting, confirming an address).</summary>
    public int PagesPerMinute { get; set; } = 30;

    /// <summary>Sign-in links one client IP can ask to have emailed per hour.</summary>
    public int EmailLinksPerHour { get; set; } = 10;

    /// <summary>Dynamic client registrations per client IP per hour.</summary>
    public int RegistrationsPerHour { get; set; } = 10;
}

/// <summary>
/// ASP.NET Core rate limiting policies. Client IPs are only meaningful behind a proxy when forwarded headers
/// are on (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>); otherwise every request comes from the proxy. Limits on
/// what a person or account may do (links per address, tokens per account) are in the services instead.
/// See docs/design.md, "Abuse prevention".
/// </summary>
internal static class RateLimits
{
    /// <summary><c>/mcp</c>, per caller. Runs after authorization, so the caller is known.</summary>
    public const string Mcp = "mcp";

    /// <summary>Pages opened from email links, per client IP: <c>[EnableRateLimiting(RateLimits.Pages)]</c>.</summary>
    public const string Pages = "pages";

    /// <summary>Asking for a sign-in link by email (form posts only), per client IP.</summary>
    public const string EmailLinks = "email-links";

    /// <summary>Dynamic client registration, per client IP.</summary>
    public const string ClientRegistration = "client-registration";

    public static void AddSchedulerRateLimits(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new();

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                // Not the friendly "not found" page that other error statuses get.
                if (context.HttpContext.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
                    statusCodePages.Enabled = false;
                await response.WriteAsync("Too many requests. Wait a minute and try again.", cancellationToken);
            };

            limiter.AddPolicy(Mcp, http => FixedWindow("mcp:" + Caller(http), options.McpPerMinute, TimeSpan.FromMinutes(1)));
            limiter.AddPolicy(Pages, http => FixedWindow("pages:" + ClientIp(http), options.PagesPerMinute, TimeSpan.FromMinutes(1)));
            limiter.AddPolicy(EmailLinks, http => HttpMethods.IsPost(http.Request.Method)
                ? FixedWindow("email-links:" + ClientIp(http), options.EmailLinksPerHour, TimeSpan.FromHours(1))
                : RateLimitPartition.GetNoLimiter(""));
            limiter.AddPolicy(ClientRegistration, http =>
                FixedWindow("registration:" + ClientIp(http), options.RegistrationsPerHour, TimeSpan.FromHours(1)));
        });
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window });

    /// <summary>The account or address the request authenticated as; the client IP if it didn't.</summary>
    private static string Caller(HttpContext http) =>
        CallerResolver.FromPrincipal(http.User, Channel.Mcp) switch
        {
            { UserId: { } id } => "account:" + id,
            { VerifiedEmails: [var email, ..] } => "email:" + email,
            _ => "ip:" + ClientIp(http),
        };

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
