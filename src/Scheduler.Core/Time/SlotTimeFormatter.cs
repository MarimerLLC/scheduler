using System.Globalization;
using NodaTime;
using NodaTime.Text;

namespace Scheduler.Core.Time;

/// <summary>
/// Formats slot times for people and agents. Every human-readable time carries its zone abbreviation
/// and UTC offset, so a wrong conversion is visible instead of silent.
/// </summary>
public static class SlotTimeFormatter
{
    // Always +HH:mm: the general "o<G>" form would write -06 for a whole-hour offset.
    private static readonly OffsetDateTimePattern IsoWithOffset =
        OffsetDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mmo<+HH:mm>");

    private static readonly LocalDateTimePattern IsoLocal =
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm");

    /// <summary>The exact instant, e.g. <c>2026-11-03T16:00:00Z</c>.</summary>
    public static string Utc(Instant instant) => InstantPattern.General.Format(instant);

    /// <summary>The instant in <paramref name="timeZoneId"/> with its offset, e.g. <c>2026-11-03T10:00-06:00</c>.</summary>
    public static string IsoIn(Instant instant, string timeZoneId) =>
        IsoWithOffset.Format(instant.InZone(SlotTimeResolver.GetZone(timeZoneId)).ToOffsetDateTime());

    /// <summary>Local wall time with no offset, e.g. <c>2026-11-03T10:00</c>, for creating calendar events.</summary>
    public static string IsoLocalTime(LocalDateTime local) => IsoLocal.Format(local);

    /// <summary>For people, e.g. <c>Tue Nov 3, 10:00 AM CST (UTC-06:00)</c>.</summary>
    public static string Display(Instant instant, string timeZoneId)
    {
        var zone = SlotTimeResolver.GetZone(timeZoneId);
        var zoned = instant.InZone(zone);
        var abbreviation = zone.GetZoneInterval(instant).Name;
        var wallTime = zoned.LocalDateTime.ToString("ddd MMM d, h:mm tt", CultureInfo.InvariantCulture);
        return $"{wallTime} {abbreviation} (UTC{SlotTimeResolver.FormatOffset(zoned.Offset)})";
    }
}
