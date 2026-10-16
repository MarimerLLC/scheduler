using NodaTime;
using NodaTime.Text;

namespace Scheduler.Core.Time;

/// <summary>A slot start as the organizer meant it (wall time in a zone) and as the instant it maps to.</summary>
public readonly record struct SlotTime(LocalDateTime LocalStart, string TimeZoneId, Instant Start, Offset Offset);

/// <summary>
/// Turns a caller's "local time + IANA zone" into a <see cref="SlotTime"/>. The server does every
/// local-to-UTC conversion so agents never have to work out an offset themselves, which they often get
/// wrong across a daylight-saving change. See docs/design.md, "Time handling".
/// </summary>
public static class SlotTimeResolver
{
    private static readonly IDateTimeZoneProvider Zones = DateTimeZoneProviders.Tzdb;

    private static readonly LocalDateTimePattern[] LocalPatterns =
    [
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm"),
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm':'ss"),
    ];

    private static readonly OffsetDateTimePattern[] OffsetPatterns =
    [
        OffsetDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mmo<G>"),
        OffsetDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm':'sso<G>"),
    ];

    /// <summary>Looks up an IANA zone ID, rejecting abbreviations ("CST") and fixed offsets ("-06:00").</summary>
    public static DateTimeZone GetZone(string? timeZoneId)
    {
        var id = timeZoneId?.Trim() ?? "";
        return Zones.GetZoneOrNull(id)
            ?? throw new SchedulingValidationException(
                $"'{id}' is not an IANA time zone ID. Use a name like America/Chicago or Europe/London; " +
                "abbreviations such as CST and fixed offsets such as -06:00 are not accepted.");
    }

    /// <summary>
    /// Resolves <paramref name="start"/> in <paramref name="timeZoneId"/>. <paramref name="start"/> is a
    /// local wall time (<c>2026-11-03T10:00</c>). It may carry an offset (<c>2026-11-03T10:00-06:00</c>), but
    /// only if the offset is the zone's real offset at that moment; a mismatch is rejected, because it
    /// usually means the caller used today's offset for a date on the other side of a DST change.
    /// Times that don't exist (spring forward) or happen twice (fall back) are rejected rather than guessed.
    /// </summary>
    public static SlotTime Resolve(string? start, string? timeZoneId)
    {
        var zone = GetZone(timeZoneId);
        var (local, claimedOffset) = Parse(start);

        var mapping = zone.MapLocal(local);
        if (mapping.Count == 0)
            throw new SchedulingValidationException(
                $"{Describe(local)} doesn't exist in {zone.Id}: the clocks skip it when daylight saving starts. Pick another time.");
        if (mapping.Count > 1)
            throw new SchedulingValidationException(
                $"{Describe(local)} happens twice in {zone.Id}: the clocks repeat it when daylight saving ends. Pick another time.");

        var zoned = mapping.Single();
        if (claimedOffset is { } claimed && claimed != zoned.Offset)
            throw new SchedulingValidationException(
                $"{Describe(local)} in {zone.Id} is UTC{FormatOffset(zoned.Offset)}, not UTC{FormatOffset(claimed)}. " +
                $"Send the local time without an offset (e.g. {LocalPatterns[0].Format(local)}) and the server will convert it.");

        return new SlotTime(local, zone.Id, zoned.ToInstant(), zoned.Offset);
    }

    private static (LocalDateTime Local, Offset? Offset) Parse(string? start)
    {
        var text = start?.Trim() ?? "";

        foreach (var pattern in LocalPatterns)
        {
            var result = pattern.Parse(text);
            if (result.Success)
                return (result.Value, null);
        }

        foreach (var pattern in OffsetPatterns)
        {
            var result = pattern.Parse(text);
            if (result.Success)
                return (result.Value.LocalDateTime, result.Value.Offset);
        }

        throw new SchedulingValidationException(
            $"'{text}' isn't a start time this server understands. Use local wall time in ISO 8601 form, " +
            "e.g. 2026-11-03T10:00, together with an IANA time zone.");
    }

    private static string Describe(LocalDateTime local) =>
        local.ToString("dddd MMM d, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

    internal static string FormatOffset(Offset offset) =>
        offset.ToString("+HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
