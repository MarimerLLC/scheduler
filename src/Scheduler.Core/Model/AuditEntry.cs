using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>
/// Append-only record of who did what to an event, and through which channel. Answers "why was this
/// finalized?" and is the main tool for debugging agent behavior. Purged with its event.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public Guid? EventId { get; set; }
    public Instant At { get; set; }

    /// <summary>The acting address, or null for the system (deadline expiry, reminders).</summary>
    public string? Actor { get; set; }
    public Channel Channel { get; set; }

    /// <summary>Short verb, e.g. <c>event.created</c>, <c>vote.cast</c>, <c>event.finalized</c>.</summary>
    public required string Action { get; set; }

    /// <summary>Optional JSON detail.</summary>
    public string? Detail { get; set; }
}
