namespace Scheduler.Core.Model;

/// <summary>Where a scheduling event is in its life. See docs/design.md, "State machine".</summary>
public enum EventState
{
    /// <summary>Created but not yet sent to participants.</summary>
    Draft,
    /// <summary>Participants have been notified and can vote.</summary>
    Open,
    /// <summary>The organizer picked a slot. Can be reopened to reschedule.</summary>
    Finalized,
    /// <summary>The organizer called it off.</summary>
    Cancelled,
    /// <summary>The respond-by deadline passed without a decision.</summary>
    Expired
}

/// <summary>
/// A milestone an event's status names, so an agent, or a rule a person gives it, can match on it. Tools and resources
/// write these as snake_case (<c>rules_out_every_time</c>). See docs/design.md, "Push updates".
/// </summary>
public enum EventMilestone
{
    /// <summary>
    /// Organizer only: every required participant (every participant, if none is required) has answered every current
    /// time.
    /// </summary>
    EveryoneResponded,
    /// <summary>
    /// Organizer only: at least one required participant answered no to every current time. The status's
    /// <c>rulesOutEveryTime</c> says who.
    /// </summary>
    RulesOutEveryTime,
    /// <summary>The organizer picked a time.</summary>
    Finalized,
    /// <summary>The organizer called it off.</summary>
    Cancelled,
    /// <summary>The respond-by deadline passed without a decision.</summary>
    Expired
}

/// <summary>A participant's answer for one proposed slot, worst to best. Only <see cref="No"/> means no.</summary>
public enum VoteValue
{
    /// <summary>Rules the slot out for this person.</summary>
    No,
    /// <summary>They could make it work, but it's painful: moving other commitments, early or late hours.</summary>
    IfNecessary,
    /// <summary>They can make it work without much trouble.</summary>
    Yes,
    /// <summary>A great time for them.</summary>
    Preferred
}

/// <summary>Whether a participant's "no" rules a slot out.</summary>
public enum AttendeeRole
{
    Required,
    Optional
}

/// <summary>How a request reached the server. Recorded on votes and in the audit log.</summary>
public enum Channel
{
    Web,
    Mcp,
    Email,
    System
}

/// <summary>Delivery state of an outbox email.</summary>
public enum OutboxStatus
{
    Pending,
    Sent,
    /// <summary>Not sent: the recipient unsubscribed, bounced or complained.</summary>
    Suppressed,
    /// <summary>Gave up after the maximum number of attempts.</summary>
    Failed
}

/// <summary>Where a request to organize stands.</summary>
public enum OrganizerRequestStatus
{
    Pending,
    Approved,
    Declined
}

/// <summary>Why an address is on the suppression list.</summary>
public enum SuppressionReason
{
    Unsubscribed,
    Bounced,
    Complained
}

/// <summary>Where a participant's suggested time stands with the organizer.</summary>
public enum SuggestionStatus
{
    /// <summary>Waiting for the organizer to add it or dismiss it.</summary>
    Pending,
    /// <summary>On the poll: the organizer proposed it as one of the event's times.</summary>
    Added,
    /// <summary>The organizer decided not to add it.</summary>
    Dismissed
}
