using Scheduler.Core.Model;

namespace Scheduler.Core.Services;

/// <summary>One of the caller's addresses. <see cref="SignedInWith"/> addresses can't be removed in this session.</summary>
public sealed record AccountAddressView(string Email, string? VerifiedAt, bool SignedInWith);

/// <summary>What a confirmation link is for, shown before the person confirms it.</summary>
public sealed record VerificationView(string Email, EmailVerificationPurpose Purpose);

/// <summary>A confirmed sign-in link: sign the browser in as <see cref="Email"/> and go to <see cref="ReturnUrl"/>.</summary>
public sealed record EmailSignIn(string Email, string? DisplayName, string ReturnUrl);

/// <summary>
/// The addresses an account owns, and proving an address by emailing it a link: to add it to the caller's
/// account, or to sign in when a provider gave the address without verifying it. Links are single use, last
/// 30 minutes and are rate limited per address and per account. Address changes are audited. Account changes
/// are made only on the web, signed in. See docs/design.md, "Verifying an address by email".
/// </summary>
public interface IAccountService
{
    /// <summary>The caller's addresses: their account's, or before they have one, the address they signed in with.</summary>
    Task<IReadOnlyList<AccountAddressView>> ListAddressesAsync(CallerIdentity caller, CancellationToken cancellationToken);

    /// <summary>
    /// Emails <paramref name="email"/> a link that adds it to the caller's account, creating the account if
    /// needed. The link only works in a browser signed in to that account.
    /// </summary>
    Task RequestAddressAsync(CallerIdentity caller, string email, CancellationToken cancellationToken);

    /// <summary>Adds the link's address to the caller's account. Returns the address.</summary>
    Task<string> ConfirmAddressAsync(CallerIdentity caller, string token, CancellationToken cancellationToken);

    /// <summary>Removes an address from the caller's account. The address they are signed in with can't be removed.</summary>
    Task RemoveAddressAsync(CallerIdentity caller, string email, CancellationToken cancellationToken);

    /// <summary>
    /// Emails <paramref name="email"/> a link that signs a browser in as it. For addresses a sign-in provider
    /// returned without verifying; the provider's sign-in is what lets someone ask.
    /// </summary>
    Task RequestSignInLinkAsync(string email, string? displayName, string returnUrl, CancellationToken cancellationToken);

    /// <summary>Uses up a sign-in link and returns who to sign in as.</summary>
    Task<EmailSignIn> ConfirmSignInAsync(string token, CancellationToken cancellationToken);

    /// <summary>What a link is for, or null if it is unknown, used or expired. Doesn't use it up.</summary>
    Task<VerificationView?> FindVerificationAsync(string token, CancellationToken cancellationToken);
}
