using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>Why an address was sent a confirmation link.</summary>
public enum EmailVerificationPurpose
{
    /// <summary>A signed-in person is adding the address to their account.</summary>
    AddAddress,
    /// <summary>A sign-in provider gave the address without verifying it; the link signs the browser in as it.</summary>
    SignIn
}

/// <summary>
/// A link emailed to an address to prove the person can read its mail. Single use and short-lived. Only a
/// hash of the link's token is stored. See docs/design.md, "Verifying an address by email".
/// </summary>
public sealed class EmailVerification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Normalized address the link was sent to.</summary>
    public required string Email { get; set; }

    public EmailVerificationPurpose Purpose { get; set; }

    /// <summary>For <see cref="EmailVerificationPurpose.AddAddress"/>, the account the address is being added to.</summary>
    public Guid? UserId { get; set; }

    /// <summary>For <see cref="EmailVerificationPurpose.SignIn"/>, the name the sign-in provider gave.</summary>
    public string? DisplayName { get; set; }

    /// <summary>For <see cref="EmailVerificationPurpose.SignIn"/>, the local path to return to afterwards.</summary>
    public string? ReturnUrl { get; set; }

    /// <summary>SHA-256 of the link's token (256 random bits).</summary>
    public required byte[] TokenHash { get; set; }

    public Instant CreatedAt { get; set; }
    public Instant ExpiresAt { get; set; }
    public Instant? ConsumedAt { get; set; }

    public bool IsUsable(Instant now) => ConsumedAt is null && ExpiresAt > now;
}
