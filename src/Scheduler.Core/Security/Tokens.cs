using System.Security.Cryptography;

namespace Scheduler.Core.Security;

/// <summary>Random identifiers that aren't secrets.</summary>
public static class Tokens
{
    /// <summary>Crockford base32: no I, L, O or U, so codes survive being read aloud or retyped.</summary>
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>An 8-character code (40 bits) for email subjects. Uniqueness is enforced by the database.</summary>
    public static string NewShortCode(int length = 8) =>
        new(RandomNumberGenerator.GetItems<char>(Crockford, length));

    /// <summary>
    /// Normalizes a short code as a person or agent might pass it: trimmed, upper-case, and with the
    /// email-subject wrapper removed (<c>[sched:7KQ2M9XD]</c> and <c>sched:7KQ2M9XD</c> both give <c>7KQ2M9XD</c>).
    /// </summary>
    public static string NormalizeShortCode(string code)
    {
        var value = code.Trim().TrimStart('[').TrimEnd(']').Trim();
        if (value.StartsWith("sched:", StringComparison.OrdinalIgnoreCase))
            value = value["sched:".Length..];
        return value.Trim().ToUpperInvariant();
    }
}
