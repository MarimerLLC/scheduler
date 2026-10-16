using NodaTime;
using Scheduler.Core;
using Scheduler.Core.Time;

namespace Scheduler.Core.Tests;

[TestClass]
public sealed class SlotTimeResolverTests
{
    [TestMethod]
    public void LocalTimeAfterFallBackUsesStandardOffset()
    {
        // US DST ends Nov 1, 2026, so Nov 3 in Chicago is CST (UTC-6) even though "today" (Oct) is CDT.
        var slot = SlotTimeResolver.Resolve("2026-11-03T10:00", "America/Chicago");

        Assert.AreEqual(Instant.FromUtc(2026, 11, 3, 16, 0), slot.Start);
        Assert.AreEqual(Offset.FromHours(-6), slot.Offset);
        Assert.AreEqual(new LocalDateTime(2026, 11, 3, 10, 0), slot.LocalStart);
        Assert.AreEqual("America/Chicago", slot.TimeZoneId);
    }

    [TestMethod]
    public void MatchingOffsetIsAccepted()
    {
        var slot = SlotTimeResolver.Resolve("2026-11-03T10:00-06:00", "America/Chicago");

        Assert.AreEqual(Instant.FromUtc(2026, 11, 3, 16, 0), slot.Start);
    }

    [TestMethod]
    public void TodaysOffsetForADateAcrossTheDstChangeIsRejected()
    {
        // The classic agent mistake: using October's CDT offset for a November date.
        var ex = Assert.ThrowsExactly<SchedulingValidationException>(
            () => SlotTimeResolver.Resolve("2026-11-03T10:00-05:00", "America/Chicago"));

        StringAssert.Contains(ex.Message, "UTC-06:00, not UTC-05:00");
    }

    [TestMethod]
    public void SecondsAreOptional()
    {
        var slot = SlotTimeResolver.Resolve("2026-11-03T10:00:00", "America/Chicago");

        Assert.AreEqual(Instant.FromUtc(2026, 11, 3, 16, 0), slot.Start);
    }

    [TestMethod]
    public void RepeatedFallBackTimeIsRejected()
    {
        var ex = Assert.ThrowsExactly<SchedulingValidationException>(
            () => SlotTimeResolver.Resolve("2026-11-01T01:30", "America/Chicago"));

        StringAssert.Contains(ex.Message, "happens twice");
    }

    [TestMethod]
    public void SkippedSpringForwardTimeIsRejected()
    {
        // US DST starts Mar 8, 2026: 2:00 jumps to 3:00.
        var ex = Assert.ThrowsExactly<SchedulingValidationException>(
            () => SlotTimeResolver.Resolve("2026-03-08T02:30", "America/Chicago"));

        StringAssert.Contains(ex.Message, "doesn't exist");
    }

    [TestMethod]
    [DataRow("CST")]
    [DataRow("-06:00")]
    [DataRow("Central Standard Time")]
    [DataRow("")]
    public void NonIanaZonesAreRejected(string zone)
    {
        Assert.ThrowsExactly<SchedulingValidationException>(() => SlotTimeResolver.Resolve("2026-11-03T10:00", zone));
    }

    [TestMethod]
    [DataRow("next Tuesday at 10")]
    [DataRow("2026-11-03")]
    [DataRow("10:00")]
    public void UnparseableStartsAreRejected(string start)
    {
        Assert.ThrowsExactly<SchedulingValidationException>(() => SlotTimeResolver.Resolve(start, "America/Chicago"));
    }

    [TestMethod]
    public void ZonesThatChangeOnDifferentDatesStayCorrect()
    {
        // Oct 26-31, 2026: the UK has fallen back but the US hasn't, so Chicago is 5 hours behind London, not 6.
        var slot = SlotTimeResolver.Resolve("2026-10-28T10:00", "America/Chicago");

        Assert.AreEqual("Wed Oct 28, 3:00 PM GMT (UTC+00:00)", SlotTimeFormatter.Display(slot.Start, "Europe/London"));
    }
}
