using NodaTime;

namespace Scheduler.Core.Model;

/// <summary>
/// A participant's answer for one slot. Keyed by (participant, slot), so voting again overwrites:
/// an agent that retries a call can't create duplicates.
/// </summary>
public sealed class Vote
{
    public Guid ParticipantId { get; set; }
    public Guid SlotId { get; set; }
    public VoteValue Value { get; set; }
    public Instant UpdatedAt { get; set; }
    public Channel Channel { get; set; }
}
