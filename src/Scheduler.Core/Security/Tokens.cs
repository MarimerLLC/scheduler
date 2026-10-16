using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Scheduler.Core.Security;

/// <summary>Random identifiers: magic-link tokens (secret) and event short codes (not secret).</summary>
public static class Tokens
{
    /// <summary>Crockford base32: no I, L, O or U, so codes survive being read aloud or retyped.</summary>
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A 256-bit URL-safe token for a participant's voting link. Store only its hash.</summary>
    public static string NewMagicToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static bool Matches(string token, byte[] hash) =>
        CryptographicOperations.FixedTimeEquals(Hash(token), hash);

    /// <summary>An 8-character code (40 bits) for email subjects. Uniqueness is enforced by the database.</summary>
    public static string NewShortCode(int length = 8) =>
        new(RandomNumberGenerator.GetItems<char>(Crockford, length));
}
