using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using NodaTime;

namespace Scheduler.Core.Security;

/// <summary>
/// Tokens for the links that confirm an address: 256 random bits in base64url, stored only as a SHA-256 hash,
/// like personal access tokens. See docs/design.md, "Verifying an address by email".
/// </summary>
public static class VerificationLinks
{
    /// <summary>How long a link works. Long enough to switch to a mail app, short enough that an old email is useless.</summary>
    public static readonly Duration Lifetime = Duration.FromMinutes(30);

    /// <summary>Links sent to one address in an hour, whoever asked for them, so the server can't be used to flood an inbox.</summary>
    public const int MaxPerAddressPerHour = 3;

    /// <summary>Links one account can ask for in an hour, across all addresses.</summary>
    public const int MaxPerAccountPerHour = 10;

    /// <summary>Addresses one account can own.</summary>
    public const int MaxAddressesPerAccount = 10;

    /// <summary>The path of the page a link opens; the token is its <c>t</c> query parameter.</summary>
    public const string Path = "/verify";

    private const int RandomBytes = 32;

    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));

    /// <summary>Whether a value has exactly the shape <see cref="New"/> produces.</summary>
    public static bool IsWellFormed(string? value) =>
        value is not null && value.Length == Base64Url.GetEncodedLength(RandomBytes) && Base64Url.IsValid(value);

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static string Url(string publicBaseUrl, string token) => $"{publicBaseUrl.TrimEnd('/')}{Path}?t={token}";
}
