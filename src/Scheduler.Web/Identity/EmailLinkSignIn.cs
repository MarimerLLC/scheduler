using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Scheduler.Web.Identity;

/// <summary>
/// A sign-in that returned an address its provider didn't verify, waiting for the person to ask for a link
/// to it. <see cref="Reason"/> says why the provider's word wasn't enough.
/// </summary>
internal sealed record PendingEmailSignIn(string Email, string? DisplayName, string ReturnUrl, string Provider, string? Reason);

/// <summary>
/// Carries a <see cref="PendingEmailSignIn"/> from the provider callback to the page that offers to email a
/// sign-in link (<see cref="Path"/>), in the URL and protected by Data Protection so nobody can change the
/// address on the way. It expires after <see cref="Lifetime"/>. See docs/design.md, "Verifying an address by email".
/// </summary>
internal sealed class EmailLinkSignIn(IDataProtectionProvider dataProtection)
{
    public const string Path = "/signin/email";

    /// <summary>The provider name a browser signed in by an emailed link gets.</summary>
    public const string ProviderName = "Email";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("Scheduler.EmailLinkSignIn").ToTimeLimitedDataProtector();

    public string PageFor(PendingEmailSignIn pending) =>
        Path + QueryString.Create("p", _protector.Protect(JsonSerializer.Serialize(pending), Lifetime));

    /// <summary>The pending sign-in in <paramref name="protectedValue"/>, or null if it was tampered with or expired.</summary>
    public PendingEmailSignIn? Read(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
            return null;
        try
        {
            return JsonSerializer.Deserialize<PendingEmailSignIn>(_protector.Unprotect(protectedValue));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }
}
