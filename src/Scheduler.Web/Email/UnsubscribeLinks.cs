using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Scheduler.Core.Email;
using Scheduler.Core.Services;

namespace Scheduler.Web.Email;

/// <summary>
/// Unsubscribe links: the token is the address, protected by Data Protection, so it needs no table and never
/// expires (an old email's link still works). Anyone holding the link can unsubscribe that address, which is
/// what a link in an email to it is for. See docs/design.md, "Email".
/// </summary>
internal sealed class UnsubscribeLinks(IDataProtectionProvider dataProtection, IOptions<SchedulingOptions> options) : IUnsubscribeLinks
{
    public const string PagePath = "/unsubscribe";
    public const string OneClickPath = "/unsubscribe/one-click";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Scheduler.Unsubscribe");

    public string PageUrl(string email) => Url(PagePath, email);

    public string OneClickUrl(string email) => Url(OneClickPath, email);

    /// <summary>The address in a token, or null if it was tampered with or made with another key.</summary>
    public string? Read(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        try
        {
            return _protector.Unprotect(token);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private string Url(string path, string email) =>
        options.Value.PublicBaseUrl.TrimEnd('/') + path + QueryString.Create("t", _protector.Protect(email));
}
