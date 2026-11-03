namespace Scheduler.Web.Mcp;

/// <summary>
/// The two resource URIs for an event: <c>scheduler://events/{shortCode}</c> (what <c>get_event</c> returns) and
/// <c>scheduler://events/{shortCode}/status</c> (its milestones). See docs/design.md, "Push updates".
/// </summary>
/// <param name="EventRef">The shortCode (or ID) as written in the URI; the service normalizes it.</param>
/// <param name="IsStatus">True for the <c>/status</c> resource.</param>
internal sealed record EventResourceUri(string EventRef, bool IsStatus)
{
    public const string Prefix = "scheduler://events/";
    public const string StatusSuffix = "/status";
    public const string EventTemplate = Prefix + "{shortCode}";
    public const string StatusTemplate = Prefix + "{shortCode}" + StatusSuffix;

    /// <summary>Longer than any shortCode or GUID; anything longer isn't an event reference.</summary>
    private const int MaxRefLength = 64;

    public static string Event(string shortCode) => Prefix + shortCode;

    public static string Status(string shortCode) => Prefix + shortCode + StatusSuffix;

    /// <summary>The event a URI names, or null if it isn't one of the two forms.</summary>
    public static EventResourceUri? TryParse(string? uri)
    {
        if (uri is null || !uri.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = uri[Prefix.Length..];
        var isStatus = rest.EndsWith(StatusSuffix, StringComparison.Ordinal);
        if (isStatus)
            rest = rest[..^StatusSuffix.Length];
        return rest.Length is > 0 and <= MaxRefLength && rest.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? new EventResourceUri(rest, isStatus)
            : null;
    }
}
