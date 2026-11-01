using NodaTime;
using Scheduler.Core.Model;

namespace Scheduler.Core.Ranking;

/// <summary>
/// How one slot is doing. Counts cover everyone invited; the <c>Required</c> counts are the part of them that
/// came from required participants. <see cref="Viable"/> means no required participant has said no.
/// </summary>
public sealed record SlotScore(
    Guid SlotId,
    Instant Start,
    int Preferred,
    int Yes,
    int IfNecessary,
    int RequiredIfNecessary,
    int No,
    int RequiredNo,
    int Pending)
{
    public bool Viable => RequiredNo == 0;

    public int OptionalNo => No - RequiredNo;

    public int OptionalIfNecessary => IfNecessary - RequiredIfNecessary;
}

/// <summary>
/// Orders slots for the organizer's recommendation, least misery first: required people before optional
/// people, and "if necessary" as a last resort. Only "no" makes a slot unviable, and only from a required
/// person. Order: fewest "no" from required people, then fewest "if necessary" from required people, then
/// fewest "no" from optional people, then fewest "if necessary" from optional people, then most
/// "preferred", then fewest not-yet-voted, then earliest. "Yes" is not a penalty; it just doesn't add to
/// "preferred". The organizer isn't counted: they proposed the slots, so every slot already works for them.
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
            .ThenBy(s => s.RequiredIfNecessary)
            .ThenBy(s => s.OptionalNo)
            .ThenBy(s => s.OptionalIfNecessary)
            .ThenByDescending(s => s.Preferred)
            .ThenBy(s => s.Pending)
            .ThenBy(s => s.Start)
            .ToList();
    }

    private static SlotScore Score(ProposedSlot slot, IEnumerable<Vote> votes, IReadOnlyDictionary<Guid, AttendeeRole> roles)
    {
        int preferred = 0, yes = 0, ifNecessary = 0, requiredIfNecessary = 0, no = 0, requiredNo = 0, voted = 0;
        foreach (var vote in votes)
        {
            if (!roles.TryGetValue(vote.ParticipantId, out var role))
                continue;

            voted++;
            var required = role == AttendeeRole.Required;
            switch (vote.Value)
            {
                case VoteValue.Preferred:
                    preferred++;
                    break;
                case VoteValue.Yes:
                    yes++;
                    break;
                case VoteValue.IfNecessary:
                    ifNecessary++;
                    if (required)
                        requiredIfNecessary++;
                    break;
                case VoteValue.No:
                    no++;
                    if (required)
                        requiredNo++;
                    break;
            }
        }

        return new SlotScore(
            slot.Id, slot.Start, preferred, yes, ifNecessary, requiredIfNecessary, no, requiredNo, roles.Count - voted);
    }
}
