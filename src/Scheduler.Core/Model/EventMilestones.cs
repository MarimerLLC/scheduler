namespace Scheduler.Core.Model;

/// <summary>
/// Names the milestones an event's status has reached (docs/design.md, "Push updates"), from the same values the
/// status reports, so the list can never disagree with them or move the marker on its own. It says what is true
/// now, not what just happened: a client compares it with its last read, and <c>rulesOutEveryTime</c> with it, since
/// a second person ruling out every time leaves the list as it was.
/// </summary>
public static class EventMilestones
{
    /// <summary>
    /// The vote milestones (organizer only, so null values add none), then how the event ended, if it has. The vote
    /// milestones stay after it ends, so finalizing only adds <see cref="EventMilestone.Finalized"/> and a client
    /// that missed the last vote still sees it. Reopening has no name of its own: <see cref="EventMilestone.Finalized"/>
    /// or <see cref="EventMilestone.Expired"/> drops out and the state is open again.
    /// </summary>
    public static IReadOnlyList<EventMilestone> Reached(EventState state, bool? everyoneResponded, IReadOnlyList<string>? rulesOutEveryTime)
    {
        var reached = new List<EventMilestone>(3);
        if (everyoneResponded == true)
            reached.Add(EventMilestone.EveryoneResponded);
        if (rulesOutEveryTime is { Count: > 0 })
            reached.Add(EventMilestone.RulesOutEveryTime);

        switch (state)
        {
            case EventState.Finalized: reached.Add(EventMilestone.Finalized); break;
            case EventState.Cancelled: reached.Add(EventMilestone.Cancelled); break;
            case EventState.Expired: reached.Add(EventMilestone.Expired); break;
        }

        return reached;
    }
}
