using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Scheduler.Core.Security;

/// <summary>
/// Personal access token format: <c>sched_pat_</c> followed by 256 random bits in base64url. The fixed
/// prefix lets the server tell a token from an OAuth access token without a lookup, and lets secret
/// scanners recognize a leaked one.
/// </summary>
public static class AccessTokens
{
    public const string Prefix = "sched_pat_";
    public const int NameMax = 60;
    public const int MaxLifetimeDays = 365;
    public const int MaxActivePerAccount = 10;

    private const int RandomBytes = 32;

    /// <summary>Characters of the token kept for display: the prefix plus four random characters.</summary>
    private const int DisplayLength = 14;

    /// <summary>The token's length: the prefix plus 43 base64url characters.</summary>
    public static int Length { get; } = Prefix.Length + Base64Url.GetEncodedLength(RandomBytes);

    public static string New() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));

    /// <summary>Whether a bearer value is meant as a personal access token (shape only, not validity).</summary>
    public static bool LooksLikeToken(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Whether a value has exactly the shape <see cref="New"/> produces.</summary>
    public static bool IsWellFormed(string? value) =>
        LooksLikeToken(value) && value!.Length == Length && Base64Url.IsValid(value.AsSpan(Prefix.Length));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>The part that is safe to show, e.g. <c>sched_pat_Xy3q…</c>.</summary>
    public static string DisplayPrefix(string token) => token[..DisplayLength];
}
