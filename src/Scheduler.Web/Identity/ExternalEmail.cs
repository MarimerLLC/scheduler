using System.Security.Claims;
using Scheduler.Core;

namespace Scheduler.Web.Identity;

/// <summary>
/// A verified address from an external sign-in, or the reason there isn't one. When the provider gave an
/// address without verifying it, <see cref="UnverifiedEmail"/> holds it, so it can be proved by email instead.
/// </summary>
internal sealed record ExternalEmailResult(string? Email, string? DisplayName, string? Error, string? UnverifiedEmail = null);

/// <summary>
/// Reads a verified email address from a Microsoft or Google sign-in. Not every <c>email</c> claim is
/// verified: Google says so with <c>email_verified</c>, but Entra's <c>email</c> can be an attribute a tenant
/// admin or user typed in. Only a personal Microsoft account, or an Entra token carrying
/// <c>xms_edov</c> (email domain owner verified), proves the address. Anything else can still be proved by
/// emailing the address a sign-in link (<see cref="EmailLinkSignIn"/>). See docs/design.md, "Identity and authentication".
/// </summary>
internal static class ExternalEmail
{
    public const string Microsoft = "Microsoft";
    public const string Google = "Google";

    /// <summary>The tenant that personal Microsoft accounts (outlook.com, hotmail.com, ...) sign in through.</summary>
    public const string ConsumerTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    public static ExternalEmailResult Read(string provider, ClaimsPrincipal principal)
    {
        var raw = Claim(principal, "email") ?? Claim(principal, ClaimTypes.Email);
        var name = Claim(principal, "name") ?? Claim(principal, ClaimTypes.Name);
        if (string.IsNullOrWhiteSpace(raw))
            return Fail($"Your {provider} account didn't share an email address, so it can't be matched to meeting invitations.");

        string email;
        try
        {
            email = EmailAddresses.Normalize(raw);
        }
        catch (SchedulingValidationException ex)
        {
            return Fail(ex.Message);
        }

        var verified = provider switch
        {
            Google => IsTrue(Claim(principal, "email_verified")),
            Microsoft => Claim(principal, "tid") == ConsumerTenantId || IsTrue(Claim(principal, "xms_edov")),
            _ => false,
        };
        var displayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (!verified)
            return new ExternalEmailResult(null, displayName,
                provider == Microsoft
                    ? $"Your organization's Microsoft sign-in doesn't confirm that you own {email}."
                    : $"{provider} hasn't verified {email}.",
                UnverifiedEmail: email);

        return new ExternalEmailResult(email, displayName, null);
    }

    private static ExternalEmailResult Fail(string error) => new(null, null, error);

    private static string? Claim(ClaimsPrincipal principal, string type) => principal.FindFirst(type)?.Value;

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
}
