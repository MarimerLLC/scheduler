using System.Buffers.Text;
using System.Security.Cryptography;

namespace Scheduler.Core.Security;

/// <summary>
/// Voting-link tokens. A token is HMAC-SHA256(server key, participant ID + nonce), so the server can
/// put the same working link in every email (invitation, new times, reminder) without storing the token,
/// and revoke a participant's link by giving them a new nonce. See docs/design.md, "Magic links".
/// </summary>
public sealed class MagicLinks
{
    public const int KeyBytes = 32;
    public const int NonceBytes = 16;

    private readonly byte[] _key;

    public MagicLinks(byte[] key)
    {
        if (key.Length < KeyBytes)
            throw new ArgumentException($"The magic-link key must be at least {KeyBytes} bytes.", nameof(key));
        _key = key;
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceBytes);

    /// <summary>The URL-safe token for a participant (43 characters).</summary>
    public string CreateToken(Guid participantId, byte[] nonce) => Base64Url.EncodeToString(Compute(participantId, nonce));

    /// <summary>Whether <paramref name="token"/> is this participant's current token. Constant time.</summary>
    public bool Matches(string? token, Guid participantId, byte[] nonce)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        Span<byte> presented = stackalloc byte[64];
        if (!Base64Url.IsValid(token) || !Base64Url.TryDecodeFromChars(token, presented, out var written) || written != 32)
            return false;

        return CryptographicOperations.FixedTimeEquals(presented[..written], Compute(participantId, nonce));
    }

    private byte[] Compute(Guid participantId, byte[] nonce)
    {
        var message = new byte[16 + nonce.Length];
        participantId.TryWriteBytes(message);
        nonce.CopyTo(message, 16);
        return HMACSHA256.HashData(_key, message);
    }
}
