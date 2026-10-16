namespace Scheduler.Core;

/// <summary>Organizer allowlist entries: an exact address or <c>*@domain</c>.</summary>
public static class OrganizerPatterns
{
    public const int MaxLength = FieldLimits.EmailMax;

    /// <summary>
    /// Normalizes an entry an admin typed: <c>Bob@Example.com</c> becomes <c>bob@example.com</c>, and
    /// <c>@Marimer.LLC</c> or <c>*@Marimer.LLC</c> becomes <c>*@marimer.llc</c>. A bare <c>*</c> is refused:
    /// letting anyone organize is a configuration decision, not something to grant at runtime.
    /// </summary>
    public static string Normalize(string? entry)
    {
        var value = entry?.Trim() ?? "";
        if (value == "*")
            throw new SchedulingValidationException(
                "Letting anyone organize can only be set in the server's configuration, not granted here.");

        if (value.StartsWith('@'))
            value = "*" + value;

        if (value.StartsWith("*@", StringComparison.Ordinal))
        {
            var domain = value[2..];
            // Validate the domain by validating an address on it.
            if (domain.Length == 0 || !domain.Contains('.') || domain.Contains('*'))
                throw new SchedulingValidationException($"'{entry}' isn't a valid domain entry. Use *@example.com.");
            EmailAddresses.Normalize("x@" + domain);
            return "*@" + domain.ToLowerInvariant();
        }

        try
        {
            return EmailAddresses.Normalize(value);
        }
        catch (SchedulingValidationException)
        {
            throw new SchedulingValidationException(
                $"'{entry}' isn't an organizer entry. Use an address (bob@example.com) or a domain (*@example.com).");
        }
    }

    /// <summary>The entries that would let <paramref name="email"/> organize: itself and its domain.</summary>
    public static IEnumerable<string> CandidatesFor(string email)
    {
        yield return email;
        var at = email.LastIndexOf('@');
        if (at > 0)
            yield return "*" + email[at..];
    }

    public static bool IsDomain(string pattern) => pattern.StartsWith("*@", StringComparison.Ordinal);
}
