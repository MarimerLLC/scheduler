namespace Scheduler.Core;

/// <summary>
/// A failure whose message is safe to show the caller, human or agent. The MCP tools and web pages
/// surface these messages verbatim; anything else is reported as a generic error.
/// </summary>
public abstract class SchedulingException(string message) : Exception(message);

/// <summary>The request is malformed: a bad time zone, a slot that doesn't exist, a field too long.</summary>
public sealed class SchedulingValidationException(string message) : SchedulingException(message);

/// <summary>
/// The event doesn't exist or the caller may not see it. One type for both, so the message never
/// reveals whether an event ID is real.
/// </summary>
public sealed class SchedulingAccessException()
    : SchedulingException("No scheduling event with that ID is visible to you.");

/// <summary>The event is in the wrong state for the request, or changed since the caller read it.</summary>
public sealed class SchedulingConflictException(string message) : SchedulingException(message);
