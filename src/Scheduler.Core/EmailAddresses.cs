using System.Net.Mail;

namespace Scheduler.Core;

public static class EmailAddresses
{
    /// <summary>Trims and lower-cases an address, rejecting anything that isn't a single plain address.</summary>
    public static string Normalize(string? email)
    {
        var trimmed = email?.Trim() ?? "";
        if (trimmed.Length is 0 or > FieldLimits.EmailMax
            || !MailAddress.TryCreate(trimmed, out var parsed)
            || !string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            throw new SchedulingValidationException($"'{trimmed}' isn't a valid email address.");
        }

        return parsed.Address.ToLowerInvariant();
    }
}
