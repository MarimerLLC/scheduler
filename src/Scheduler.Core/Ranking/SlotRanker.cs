using NodaTime;
using Scheduler.Core.Model;

namespace Scheduler.Core.Ranking;

/// <summary>How one slot is doing. <see cref="Viable"/> means no required participant has said no.</summary>
public sealed record SlotScore(
    Guid SlotId,
    Instant Start,
    int Preferred,
    int IfNecessary,
    int No,
    int RequiredNo,
    int Pending)
{
    public bool Viable => RequiredNo == 0;
}

/// <summary>
/// Orders slots for the organizer's recommendation. The organizer isn't counted: they proposed the slots,
/// so every slot already works for them. Order: fewest "no" from required people, then fewest "no"
/// overall, then most "preferred", then fewest not-yet-voted, then earliest.
/// See docs/design.md, "Ranking".
/// </summary>
public static class SlotRanker
{
    public static IReadOnlyList<SlotScore> Rank(SchedulingEvent ev)
    {
        var roles = ev.Participants.ToDictionary(p => p.Id, p => p.Role);
        var votesBySlot = ev.Participants
            .SelectMany(p => p.Votes)
            .ToLookup(v => v.SlotId);

        return ev.ActiveSlots
            .Select(slot => Score(slot, votesBySlot[slot.Id], roles))
            .OrderBy(s => s.RequiredNo)
            .ThenBy(s => s.No)
            .ThenByDescending(s => s.Preferred)
            .ThenBy(s => s.Pending)
            .ThenBy(s => s.Start)
            .ToList();
    }

    private static SlotScore Score(ProposedSlot slot, IEnumerable<Vote> votes, IReadOnlyDictionary<Guid, AttendeeRole> roles)
    {
        int preferred = 0, ifNecessary = 0, no = 0, requiredNo = 0, voted = 0;
        foreach (var vote in votes)
        {
            if (!roles.TryGetValue(vote.ParticipantId, out var role))
                continue;

            voted++;
            switch (vote.Value)
            {
                case VoteValue.Preferred:
                    preferred++;
                    break;
                case VoteValue.IfNecessary:
                    ifNecessary++;
                    break;
                case VoteValue.No:
                    no++;
                    if (role == AttendeeRole.Required)
                        requiredNo++;
                    break;
            }
        }

        return new SlotScore(slot.Id, slot.Start, preferred, ifNecessary, no, requiredNo, roles.Count - voted);
    }
}
