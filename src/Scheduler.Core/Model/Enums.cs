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

/// <summary>A participant's answer for one proposed slot.</summary>
public enum VoteValue
{
    No,
    IfNecessary,
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

/// <summary>Why an address is on the suppression list.</summary>
public enum SuppressionReason
{
    Unsubscribed,
    Bounced,
    Complained
}
