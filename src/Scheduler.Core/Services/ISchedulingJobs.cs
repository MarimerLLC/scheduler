namespace Scheduler.Core.Services;

/// <summary>
/// The scheduled work, run by background jobs as the system (audit channel <c>system</c>, no actor). Each method
/// does one pass and is safe to run again. The schedule is in docs/design.md, "Background jobs".
/// </summary>
public interface ISchedulingJobs
{
    /// <summary>Expires open events whose voting deadline has passed, and tells each organizer. Returns how many.</summary>
    Task<int> ExpireOverdueEventsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Emails one reminder to each participant of an open event who hasn't answered every time, once the deadline
    /// is within <see cref="SchedulingOptions.ReminderLeadHours"/>. Once per deadline: a new deadline gets a new
    /// reminder. Returns how many were sent.
    /// </summary>
    Task<int> SendRemindersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes events closed more than <see cref="SchedulingOptions.RetentionDays"/> ago, and drafts unchanged for
    /// <see cref="SchedulingOptions.DraftRetentionDays"/>, with everything about them. Returns how many events.
    /// </summary>
    Task<int> PurgeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// If the time zone rules (tzdb version) changed since the last run, recomputes the UTC instant of every
    /// future slot from its local time and zone. Returns how many slots moved.
    /// </summary>
    Task<int> RecomputeSlotTimesAsync(CancellationToken cancellationToken);
}
