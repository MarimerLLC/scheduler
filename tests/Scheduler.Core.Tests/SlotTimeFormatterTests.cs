using NodaTime;
using Scheduler.Core.Time;

namespace Scheduler.Core.Tests;

[TestClass]
public sealed class SlotTimeFormatterTests
{
    private static readonly Instant Nov3TenCentral = Instant.FromUtc(2026, 11, 3, 16, 0);

    [TestMethod]
    public void DisplayIncludesAbbreviationAndOffset()
    {
        Assert.AreEqual("Tue Nov 3, 10:00 AM CST (UTC-06:00)", SlotTimeFormatter.Display(Nov3TenCentral, "America/Chicago"));
    }

    [TestMethod]
    public void IsoInZoneCarriesTheOffset()
    {
        Assert.AreEqual("2026-11-03T10:00-06:00", SlotTimeFormatter.IsoIn(Nov3TenCentral, "America/Chicago"));
    }

    [TestMethod]
    public void UtcIsExact()
    {
        Assert.AreEqual("2026-11-03T16:00:00Z", SlotTimeFormatter.Utc(Nov3TenCentral));
    }

    [TestMethod]
    public void IsoLocalTimeHasNoOffset()
    {
        Assert.AreEqual("2026-11-03T10:00", SlotTimeFormatter.IsoLocalTime(new LocalDateTime(2026, 11, 3, 10, 0)));
    }
}
